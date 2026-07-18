using DiscordBot.Data.Entities;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Notifications.Sinks;

/// <summary>
/// Delivers content notifications to a Discord text channel as an embed, optionally mentioning a
/// role and pinning live-stream announcements.
/// </summary>
public sealed class DiscordNotificationSink : INotificationSink
{
    private readonly DiscordClient _client;
    private readonly ILogger<DiscordNotificationSink> _logger;

    public DiscordNotificationSink(DiscordClient client, ILogger<DiscordNotificationSink> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "discord";

    // Every subscription targets a Discord channel today. A future Telegram sink would return true
    // only for subscriptions that carry Telegram delivery config.
    public bool AppliesTo(NotificationSubscription subscription) => true;

    public async Task DeliverAsync(NotificationSubscription subscription, ContentEvent contentEvent, CancellationToken ct)
    {
        // Let channel-lookup and send failures propagate: the dispatcher only marks an event "seen"
        // when delivery succeeds, so a transient error here is retried next cycle rather than lost.
        var channel = await _client.GetChannelAsync(subscription.DiscordChannelId);

        var builder = new DiscordMessageBuilder()
            .WithContent(BuildContent(subscription, contentEvent))
            .AddEmbed(BuildEmbed(contentEvent));

        if (subscription.MentionRoleId is { } roleId)
        {
            builder.WithAllowedMention(new RoleMention(roleId));
        }

        var message = await channel.SendMessageAsync(builder);

        // Pinning is best-effort: the message already delivered, so a pin failure must not fail
        // delivery (which would cause a duplicate announcement next cycle).
        if (subscription.PinLiveStreams && contentEvent.Kind == ContentEventKind.LiveStarted)
        {
            try
            {
                await message.PinAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not pin live notification in {Channel}.", subscription.DiscordChannelId);
            }
        }
    }

    private static string BuildContent(NotificationSubscription subscription, ContentEvent evt)
    {
        var mention = subscription.MentionRoleId is { } roleId ? $"<@&{roleId}> " : string.Empty;
        var who = evt.AuthorName ?? subscription.SourceDisplayName ?? "A channel you follow";

        return evt.Kind switch
        {
            ContentEventKind.LiveStarted => $"{mention}🔴 Ура, друг мой! **{who}** мчится в прямой эфир сию же секунду — вперёд, навстречу зрелищу!",
            ContentEventKind.LiveScheduled => $"{mention}📅 Внемли, доблестный товарищ! **{who}** возвестил о грядущей трансляции — нас ждёт новый квест!",
            ContentEventKind.ShortUploaded => $"{mention}🎬 Эхехе~ **{who}** являет доблестный новый Short — что за славное приключение!",
            _ => $"{mention}📺 Не страшись, дорогой Санчо! **{who}** герольдом возвещает славное новое видео — вперёд, смотреть!",
        };
    }

    private static DiscordEmbed BuildEmbed(ContentEvent evt)
    {
        var (color, label) = evt.Kind switch
        {
            ContentEventKind.LiveStarted => (new DiscordColor(0xFF0000), "🔴 В прямом эфире — ура!"),
            ContentEventKind.LiveScheduled => (new DiscordColor(0x5865F2), "📅 Квест на горизонте"),
            ContentEventKind.ShortUploaded => (new DiscordColor(0xFF0000), "🎬 Доблестный новый Short"),
            _ => (new DiscordColor(0xFF0000), "📺 Славное новое видео"),
        };

        var embed = new DiscordEmbedBuilder()
            .WithColor(color)
            .WithAuthor(evt.AuthorName)
            .WithTitle(evt.Title)
            .WithUrl(evt.Url)
            .WithFooter(label);

        if (!string.IsNullOrEmpty(evt.ThumbnailUrl))
        {
            embed.WithImageUrl(evt.ThumbnailUrl);
        }

        if (evt.Kind == ContentEventKind.LiveScheduled && evt.ScheduledStartAt is { } start)
        {
            // Discord relative timestamp.
            embed.AddField("Квест начнётся", $"<t:{start.ToUnixTimeSeconds()}:R>");
        }

        return embed.Build();
    }
}
