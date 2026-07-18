using System.ComponentModel;
using System.Text;
using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DiscordBot.Notifications;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ContextChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /notify ... — manage where new-video / Shorts / live-stream notifications are posted. Admin-only.
/// Currently supports YouTube; Twitch/Telegram slot in the same way once their sources exist.
/// </summary>
[Command("notify")]
[Description("Manage stream and upload notifications.")]
[RequireGuild]
[RequirePermissions(DiscordPermission.Administrator)]
public sealed class NotifyCommands
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<INotificationSource> _sources;

    public NotifyCommands(IServiceScopeFactory scopeFactory, IEnumerable<INotificationSource> sources)
    {
        _scopeFactory = scopeFactory;
        _sources = sources;
    }

    [Command("youtube")]
    [Description("Notify a channel about a YouTube channel's uploads, Shorts and live streams.")]
    public async ValueTask YouTubeAsync(
        SlashCommandContext ctx,
        [Description("YouTube channel URL, @handle or channel id.")] string youtubeChannel,
        [Description("Discord channel to post notifications in.")] DiscordChannel target,
        [Description("Notify on normal uploads.")] bool uploads = true,
        [Description("Notify on Shorts.")] bool shorts = true,
        [Description("Notify on live streams.")] bool liveStreams = true,
        [Description("Pin live-stream messages.")] bool pinLive = false,
        [Description("Role to mention.")] DiscordRole? mention = null)
    {
        if (!IsTextChannel(target))
        {
            await ctx.RespondAsync("The target must be a text channel.", ephemeral: true);
            return;
        }

        var source = _sources.FirstOrDefault(s => s.SourceType == ContentSourceType.YouTube);
        if (source is null)
        {
            await ctx.RespondAsync("YouTube notifications aren't available (missing API key?).", ephemeral: true);
            return;
        }

        await ctx.DeferResponseAsync(ephemeral: true);

        (string SourceChannelId, string DisplayName)? resolved;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            resolved = await source.ResolveAsync(youtubeChannel, timeout.Token);
        }
        catch (Exception)
        {
            resolved = null;
        }

        if (resolved is null)
        {
            await ctx.EditResponseAsync("Couldn't find that YouTube channel. Try the full channel URL or its id (starts with `UC`).");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var exists = await db.Subscriptions.AnyAsync(s =>
            s.GuildId == ctx.Guild!.Id &&
            s.SourceType == ContentSourceType.YouTube &&
            s.SourceChannelId == resolved.Value.SourceChannelId &&
            s.DiscordChannelId == target.Id);
        if (exists)
        {
            await ctx.EditResponseAsync($"**{resolved.Value.DisplayName}** is already sending notifications to {target.Mention}.");
            return;
        }

        db.Subscriptions.Add(new NotificationSubscription
        {
            GuildId = ctx.Guild!.Id,
            SourceType = ContentSourceType.YouTube,
            SourceChannelId = resolved.Value.SourceChannelId,
            SourceDisplayName = resolved.Value.DisplayName,
            DiscordChannelId = target.Id,
            MentionRoleId = mention?.Id,
            NotifyUploads = uploads,
            NotifyShorts = shorts,
            NotifyLiveStreams = liveStreams,
            PinLiveStreams = pinLive,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await ctx.EditResponseAsync(
            $"✅ Now notifying {target.Mention} about **{resolved.Value.DisplayName}** " +
            $"(uploads: {YesNo(uploads)}, shorts: {YesNo(shorts)}, live: {YesNo(liveStreams)}). " +
            "Existing videos won't be re-announced.");
    }

    [Command("list")]
    [Description("List notification subscriptions on this server.")]
    public async ValueTask ListAsync(SlashCommandContext ctx)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var subs = await db.Subscriptions.AsNoTracking()
            .Where(s => s.GuildId == ctx.Guild!.Id)
            .OrderBy(s => s.Id)
            .ToListAsync();

        if (subs.Count == 0)
        {
            await ctx.RespondAsync("No notification subscriptions yet. Add one with `/notify youtube`.", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var s in subs)
        {
            var kinds = new List<string>();
            if (s.NotifyUploads) kinds.Add("uploads");
            if (s.NotifyShorts) kinds.Add("shorts");
            if (s.NotifyLiveStreams) kinds.Add("live");

            sb.AppendLine($"`#{s.Id}` **{s.SourceType}** — {s.SourceDisplayName ?? s.SourceChannelId} → <#{s.DiscordChannelId}>");
            sb.AppendLine($"   {string.Join(", ", kinds)}{(s.PinLiveStreams ? ", 📌 pins live" : "")}{(s.MentionRoleId is { } r ? $", mentions <@&{r}>" : "")}");
        }

        await ctx.RespondAsync(new DiscordEmbedBuilder()
            .WithTitle("Notification subscriptions")
            .WithDescription(sb.ToString())
            .Build());
    }

    [Command("remove")]
    [Description("Remove a subscription by its id (see /notify list).")]
    public async ValueTask RemoveAsync(
        SlashCommandContext ctx,
        [Description("Subscription id.")] int id)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id && s.GuildId == ctx.Guild!.Id);
        if (sub is null)
        {
            await ctx.RespondAsync("No subscription with that id on this server.", ephemeral: true);
            return;
        }

        // Also drop dedup rows so re-adding later primes cleanly.
        var seen = db.SeenContent.Where(x => x.SubscriptionId == sub.Id);
        db.SeenContent.RemoveRange(seen);
        db.Subscriptions.Remove(sub);
        await db.SaveChangesAsync();

        await ctx.RespondAsync($"Removed subscription `#{id}`.", ephemeral: true);
    }

    private static bool IsTextChannel(DiscordChannel channel) =>
        channel.Type is DiscordChannelType.Text or DiscordChannelType.News;

    private static string YesNo(bool value) => value ? "yes" : "no";
}
