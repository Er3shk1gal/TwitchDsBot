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
[Description("Управление уведомлениями о стримах и новых видео.")]
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
    [Description("Возвещать в канал о новых видео, Shorts и стримах YouTube-канала.")]
    public async ValueTask YouTubeAsync(
        SlashCommandContext ctx,
        [Description("URL YouTube-канала, @handle или id канала.")] string youtubeChannel,
        [Description("Discord-канал для уведомлений.")] DiscordChannel target,
        [Description("Уведомлять об обычных видео.")] bool uploads = true,
        [Description("Уведомлять о Shorts.")] bool shorts = true,
        [Description("Уведомлять о стримах.")] bool liveStreams = true,
        [Description("Закреплять сообщения о стримах.")] bool pinLive = false,
        [Description("Роль для упоминания.")] DiscordRole? mention = null)
    {
        if (!IsTextChannel(target))
        {
            await ctx.RespondAsync("Не страшись, друг мой, но сей герольд может возвещать лишь в текстовом канале!", ephemeral: true);
            return;
        }

        var source = _sources.FirstOrDefault(s => s.SourceType == ContentSourceType.YouTube);
        if (source is null)
        {
            await ctx.RespondAsync("Увы, дорогой оруженосец, герольды YouTube почивают и не могут выступить в поход (быть может, утерян ключ API?).", ephemeral: true);
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
            await ctx.EditResponseAsync("Увы, сие царство YouTube ускользает от моих поисков, доблестный товарищ! Испробуй полный URL канала или его id (начинается с `UC`).");
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
            await ctx.EditResponseAsync($"Не страшись — деяния **{resolved.Value.DisplayName}** уже возвещаются в {target.Mention}!");
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
            $"✅ Ура! Отныне я буду возвещать деяния **{resolved.Value.DisplayName}** в {target.Mention} " +
            $"(видео: {YesNo(uploads)}, shorts: {YesNo(shorts)}, стримы: {YesNo(liveStreams)})! " +
            "Не страшись — былые видео не будут провозглашены заново. Что за славное приключение!");
    }

    [Command("list")]
    [Description("Показать подписки на уведомления на этом сервере.")]
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
            await ctx.RespondAsync("Ни один герольд ещё не выехал, друг мой! Призови его через `/notify youtube` — и да начнётся квест!", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var s in subs)
        {
            var kinds = new List<string>();
            if (s.NotifyUploads) kinds.Add("видео");
            if (s.NotifyShorts) kinds.Add("shorts");
            if (s.NotifyLiveStreams) kinds.Add("стримы");

            sb.AppendLine($"`#{s.Id}` **{s.SourceType}** — {s.SourceDisplayName ?? s.SourceChannelId} → <#{s.DiscordChannelId}>");
            sb.AppendLine($"   {string.Join(", ", kinds)}{(s.PinLiveStreams ? ", 📌 закрепляет стримы" : "")}{(s.MentionRoleId is { } r ? $", упоминает <@&{r}>" : "")}");
        }

        await ctx.RespondAsync(new DiscordEmbedBuilder()
            .WithTitle("Баллада о герольдах — деяния, что мы возвещаем")
            .WithDescription(sb.ToString())
            .Build());
    }

    [Command("remove")]
    [Description("Удалить подписку по её id (см. /notify list).")]
    public async ValueTask RemoveAsync(
        SlashCommandContext ctx,
        [Description("Id подписки.")] int id)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id && s.GuildId == ctx.Guild!.Id);
        if (sub is null)
        {
            await ctx.RespondAsync("Увы, ни один герольд не носит сей id в этих владениях, товарищ!", ephemeral: true);
            return;
        }

        // Also drop dedup rows so re-adding later primes cleanly.
        var seen = db.SeenContent.Where(x => x.SubscriptionId == sub.Id);
        db.SeenContent.RemoveRange(seen);
        db.Subscriptions.Remove(sub);
        await db.SaveChangesAsync();

        await ctx.RespondAsync($"Герольд `#{id}` ускакал прочь, его квест завершён — вперёд, Росинант!", ephemeral: true);
    }

    private static bool IsTextChannel(DiscordChannel channel) =>
        channel.Type is DiscordChannelType.Text or DiscordChannelType.News;

    private static string YesNo(bool value) => value ? "yes" : "no";
}
