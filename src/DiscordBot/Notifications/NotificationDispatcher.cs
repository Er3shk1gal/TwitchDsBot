using DiscordBot.Configuration;
using DiscordBot.Data;
using DiscordBot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Notifications;

/// <summary>
/// Periodically polls every notification source for every subscription, dedups against the store,
/// and fans genuinely-new events out to the applicable sinks. Source- and sink-agnostic: adding a
/// Twitch source or a Telegram sink requires no changes here.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<INotificationSource> _sources;
    private readonly IReadOnlyList<INotificationSink> _sinks;
    private readonly YouTubeOptions _youTubeOptions;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IServiceScopeFactory scopeFactory,
        IEnumerable<INotificationSource> sources,
        IEnumerable<INotificationSink> sinks,
        IOptions<YouTubeOptions> youTubeOptions,
        ILogger<NotificationDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _sources = sources.ToList();
        _sinks = sinks.ToList();
        _youTubeOptions = youTubeOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _youTubeOptions.PollIntervalSeconds));
        _logger.LogInformation(
            "Notification dispatcher started. Sources: [{Sources}], sinks: [{Sinks}], interval {Interval}.",
            string.Join(", ", _sources.Select(s => s.SourceType)),
            string.Join(", ", _sinks.Select(s => s.Name)),
            interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification poll cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        List<int> subscriptionIds;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            subscriptionIds = await db.Subscriptions.AsNoTracking().Select(s => s.Id).ToListAsync(ct);
        }

        foreach (var id in subscriptionIds)
        {
            ct.ThrowIfCancellationRequested();

            // A fresh scope (and DbContext) per subscription so a failure in one can't leak tracked
            // entities into another's SaveChanges.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (subscription is null)
            {
                continue; // removed since we listed ids
            }

            var source = _sources.FirstOrDefault(s => s.SourceType == subscription.SourceType);
            if (source is null)
            {
                continue; // e.g. a Twitch subscription while the Twitch source isn't wired yet.
            }

            try
            {
                await ProcessSubscriptionAsync(db, source, subscription, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling subscription {Id} ({Source} {Channel}) failed.",
                    subscription.Id, subscription.SourceType, subscription.SourceChannelId);
            }
        }
    }

    private async Task ProcessSubscriptionAsync(
        BotDbContext db, INotificationSource source, NotificationSubscription subscription, CancellationToken ct)
    {
        // Priming: the first successful poll records existing content as seen WITHOUT notifying, so
        // we don't backfill-spam. Tracked by an explicit PrimedAt marker (not "any rows exist") so a
        // brand-new channel with no videos still finishes priming.
        var isPriming = subscription.PrimedAt is null;

        await foreach (var evt in source.PollAsync(subscription, ct))
        {
            if (!IsKindEnabled(subscription, evt.Kind))
            {
                continue;
            }

            var eventKind = evt.Kind.ToString();
            var alreadySeen = await db.SeenContent.AnyAsync(
                s => s.SubscriptionId == subscription.Id
                     && s.ExternalId == evt.ExternalId
                     && s.EventKind == eventKind, ct);
            if (alreadySeen)
            {
                continue;
            }

            if (isPriming)
            {
                // Batch: added to the change tracker, committed once at the end together with the
                // PrimedAt flag so priming is all-or-nothing.
                db.SeenContent.Add(NewSeen(subscription.Id, evt, eventKind));
                continue;
            }

            // Deliver FIRST; only mark seen if delivery succeeded, so a transient sink failure is
            // retried next cycle instead of being silently lost forever.
            var delivered = await DeliverAsync(subscription, evt, ct);
            if (delivered)
            {
                db.SeenContent.Add(NewSeen(subscription.Id, evt, eventKind));
                await db.SaveChangesAsync(ct);
            }
        }

        if (isPriming)
        {
            subscription.PrimedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); // one transaction: all primed rows + the marker
        }
    }

    /// <summary>
    /// Deliver to every applicable sink. Returns true only if no applicable sink threw (so the
    /// caller may safely mark the event seen). Returns true when there are no applicable sinks.
    /// </summary>
    private async Task<bool> DeliverAsync(NotificationSubscription subscription, ContentEvent evt, CancellationToken ct)
    {
        var applicable = _sinks.Where(s => s.AppliesTo(subscription)).ToList();
        if (applicable.Count == 0)
        {
            return true;
        }

        var anyFailed = false;
        foreach (var sink in applicable)
        {
            try
            {
                await sink.DeliverAsync(subscription, evt, ct);
            }
            catch (Exception ex)
            {
                anyFailed = true;
                _logger.LogWarning(ex, "Sink {Sink} failed to deliver {Kind} {Id}.",
                    sink.Name, evt.Kind, evt.ExternalId);
            }
        }

        return !anyFailed;
    }

    private static SeenContent NewSeen(int subscriptionId, ContentEvent evt, string eventKind) => new()
    {
        SubscriptionId = subscriptionId,
        ExternalId = evt.ExternalId,
        EventKind = eventKind,
        SeenAt = DateTimeOffset.UtcNow,
    };

    private static bool IsKindEnabled(NotificationSubscription sub, ContentEventKind kind) => kind switch
    {
        ContentEventKind.VideoUploaded => sub.NotifyUploads,
        ContentEventKind.ShortUploaded => sub.NotifyShorts,
        ContentEventKind.LiveStarted or ContentEventKind.LiveScheduled => sub.NotifyLiveStreams,
        ContentEventKind.LiveEnded => false,
        _ => false,
    };
}
