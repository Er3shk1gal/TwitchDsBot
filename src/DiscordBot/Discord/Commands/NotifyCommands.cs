using System.Text;
using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DiscordBot.Notifications;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /notify ... — manage where new-video / Shorts / live-stream notifications are posted. Admin-only.
/// Supports YouTube and Twitch as sources; every subscription's Discord target is mandatory, and
/// <c>/notify telegram</c> additionally mirrors an existing subscription to a Telegram chat.
/// </summary>
[SlashCommandGroup("notify", "Управление уведомлениями о стримах и новых видео.")]
[SlashRequireGuild]
[SlashRequirePermissions(Permissions.Administrator)]
public sealed class NotifyCommands : ApplicationCommandModule
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<INotificationSource> _sources;
    private readonly IEnumerable<INotificationSink> _sinks;

    public NotifyCommands(
        IServiceScopeFactory scopeFactory, IEnumerable<INotificationSource> sources, IEnumerable<INotificationSink> sinks)
    {
        _scopeFactory = scopeFactory;
        _sources = sources;
        _sinks = sinks;
    }

    [SlashCommand("youtube", "Возвещать в канал о новых видео, Shorts и стримах YouTube-канала.")]
    public async Task YouTubeAsync(
        InteractionContext ctx,
        [Option("youtube_channel", "URL YouTube-канала, @handle или id канала.")] string youtubeChannel,
        [Option("target", "Discord-канал для уведомлений.")] DiscordChannel target,
        [Option("uploads", "Уведомлять об обычных видео.")] bool uploads = true,
        [Option("shorts", "Уведомлять о Shorts.")] bool shorts = true,
        [Option("live_streams", "Уведомлять о стримах.")] bool liveStreams = true,
        [Option("pin_live", "Закреплять сообщения о стримах.")] bool pinLive = false,
        [Option("mention", "Роль для упоминания.")] DiscordRole? mention = null)
    {
        if (!IsTextChannel(target))
        {
            await ctx.ReplyAsync("Не страшись, друг мой, но сей герольд может возвещать лишь в текстовом канале!", ephemeral: true);
            return;
        }

        var source = _sources.FirstOrDefault(s => s.SourceType == ContentSourceType.YouTube);
        if (source is null)
        {
            await ctx.ReplyAsync("Увы, дорогой Санчо, герольды YouTube почивают и не могут выступить в поход (быть может, утерян ключ API?).", ephemeral: true);
            return;
        }

        await ctx.DeferAsync(ephemeral: true);

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
            await ctx.EditAsync("Увы, сие царство YouTube ускользает от моих поисков, доблестный товарищ! Испробуй полный URL канала или его id (начинается с `UC`).");
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
            await ctx.EditAsync($"Не страшись — деяния **{resolved.Value.DisplayName}** уже возвещаются в {target.Mention}!");
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

        await ctx.EditAsync(
            $"✅ Ура! Отныне я буду возвещать деяния **{resolved.Value.DisplayName}** в {target.Mention} " +
            $"(видео: {YesNo(uploads)}, shorts: {YesNo(shorts)}, стримы: {YesNo(liveStreams)})! " +
            "Не страшись — былые видео не будут провозглашены заново. Что за славное приключение!");
    }

    [SlashCommand("twitch", "Возвещать в канал о начале трансляции на Twitch.")]
    public async Task TwitchAsync(
        InteractionContext ctx,
        [Option("twitch_channel", "Логин канала Twitch или ссылка на него.")] string twitchChannel,
        [Option("target", "Discord-канал для уведомлений.")] DiscordChannel target,
        [Option("pin_live", "Закреплять сообщения о стримах.")] bool pinLive = false,
        [Option("mention", "Роль для упоминания.")] DiscordRole? mention = null)
    {
        if (!IsTextChannel(target))
        {
            await ctx.ReplyAsync("Не страшись, друг мой, но сей герольд может возвещать лишь в текстовом канале!", ephemeral: true);
            return;
        }

        var source = _sources.FirstOrDefault(s => s.SourceType == ContentSourceType.Twitch);
        if (source is null)
        {
            await ctx.ReplyAsync("Увы, дорогой Санчо, герольды Twitch почивают и не могут выступить в поход (быть может, утерян ключ приложения?).", ephemeral: true);
            return;
        }

        await ctx.DeferAsync(ephemeral: true);

        (string SourceChannelId, string DisplayName)? resolved;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            resolved = await source.ResolveAsync(twitchChannel, timeout.Token);
        }
        catch (Exception)
        {
            resolved = null;
        }

        if (resolved is null)
        {
            await ctx.EditAsync("Увы, сие царство Twitch ускользает от моих поисков, доблестный товарищ! Проверь логин канала.");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var exists = await db.Subscriptions.AnyAsync(s =>
            s.GuildId == ctx.Guild!.Id &&
            s.SourceType == ContentSourceType.Twitch &&
            s.SourceChannelId == resolved.Value.SourceChannelId &&
            s.DiscordChannelId == target.Id);
        if (exists)
        {
            await ctx.EditAsync($"Не страшись — деяния **{resolved.Value.DisplayName}** уже возвещаются в {target.Mention}!");
            return;
        }

        db.Subscriptions.Add(new NotificationSubscription
        {
            GuildId = ctx.Guild!.Id,
            SourceType = ContentSourceType.Twitch,
            SourceChannelId = resolved.Value.SourceChannelId,
            SourceDisplayName = resolved.Value.DisplayName,
            DiscordChannelId = target.Id,
            MentionRoleId = mention?.Id,
            NotifyUploads = false,
            NotifyShorts = false,
            NotifyLiveStreams = true,
            PinLiveStreams = pinLive,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await ctx.EditAsync(
            $"✅ Ура! Отныне я буду возвещать о трансляциях **{resolved.Value.DisplayName}** в {target.Mention}! " +
            "Что за славное приключение!");
    }

    [SlashCommand("telegram", "Добавить или снять Telegram-цель для существующей подписки (см. /notify list).")]
    public async Task TelegramAsync(
        InteractionContext ctx,
        [Option("id", "Id подписки (см. /notify list).")] long id,
        [Option("chat_id", "Id чата Telegram (напр. -1001234567890) или @username канала. Пусто — убрать цель.")] string? chatId = null)
    {
        var sink = _sinks.FirstOrDefault(s => s.Name == "telegram");
        if (sink is null)
        {
            await ctx.ReplyAsync("Увы, герольд Telegram почивает и не может выступить в поход (быть может, не задан токен бота?).", ephemeral: true);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var idInt = (int)id;
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == idInt && s.GuildId == ctx.Guild!.Id);
        if (sub is null)
        {
            await ctx.ReplyAsync("Увы, ни один герольд не носит сей id в этих владениях, товарищ!", ephemeral: true);
            return;
        }

        chatId = chatId?.Trim();
        sub.TelegramChatId = string.IsNullOrEmpty(chatId) ? null : chatId;
        await db.SaveChangesAsync();

        await ctx.ReplyAsync(
            sub.TelegramChatId is null
                ? $"Герольд `#{id}` более не скачет в земли Telegram."
                : $"✅ Герольд `#{id}` отныне скачет и в Telegram (`{sub.TelegramChatId}`)! Не забудь добавить меня в тот чат.",
            ephemeral: true);
    }

    [SlashCommand("template", "Задать свой текст уведомления для вида события (пусто — сбросить на образец по умолчанию).")]
    public async Task TemplateAsync(
        InteractionContext ctx,
        [Option("id", "Id подписки (см. /notify list).")] long id,
        [Option("kind", "Какое событие.")] ContentEventKind kind,
        [Option("text", "Текст. Плейсхолдеры: {who} {title} {url}. Роль-упоминание (если задана) добавляется впереди сама. Пусто — сбросить.")]
        string? text = null)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var idInt = (int)id;
        var subExists = await db.Subscriptions.AsNoTracking().AnyAsync(s => s.Id == idInt && s.GuildId == ctx.Guild!.Id);
        if (!subExists)
        {
            await ctx.ReplyAsync("Увы, ни один герольд не носит сей id в этих владениях, товарищ!", ephemeral: true);
            return;
        }

        text = text?.Trim();
        var kindStr = kind.ToString();
        var existing = await db.NotificationTemplates.FirstOrDefaultAsync(t => t.SubscriptionId == idInt && t.EventKind == kindStr);

        if (string.IsNullOrEmpty(text))
        {
            if (existing is not null)
            {
                db.NotificationTemplates.Remove(existing);
                await db.SaveChangesAsync();
            }
            await ctx.ReplyAsync($"Текст для `{kind}` у герольда `#{id}` сброшен на образец по умолчанию.", ephemeral: true);
            return;
        }

        if (text.Length > 500)
        {
            await ctx.ReplyAsync("Не страшись, но сей текст слишком длинен (макс. 500 знаков)!", ephemeral: true);
            return;
        }

        if (existing is not null)
        {
            existing.Text = text;
        }
        else
        {
            db.NotificationTemplates.Add(new NotificationTemplate { SubscriptionId = idInt, EventKind = kindStr, Text = text });
        }
        await db.SaveChangesAsync();

        await ctx.ReplyAsync($"✅ Отныне для `{kind}` герольд `#{id}` возвещает: {text}", ephemeral: true);
    }

    [SlashCommand("list", "Показать подписки на уведомления на этом сервере.")]
    public async Task ListAsync(InteractionContext ctx)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var subs = await db.Subscriptions.AsNoTracking()
            .Where(s => s.GuildId == ctx.Guild!.Id)
            .OrderBy(s => s.Id)
            .ToListAsync();

        if (subs.Count == 0)
        {
            await ctx.ReplyAsync("Ни один герольд ещё не выехал, друг мой! Призови его через `/notify youtube` — и да начнётся квест!", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var s in subs)
        {
            var kinds = new List<string>();
            if (s.NotifyUploads) kinds.Add("видео");
            if (s.NotifyShorts) kinds.Add("shorts");
            if (s.NotifyLiveStreams) kinds.Add("стримы");

            var telegram = s.TelegramChatId is { } tg ? $" + Telegram (`{tg}`)" : "";
            sb.AppendLine($"`#{s.Id}` **{s.SourceType}** — {s.SourceDisplayName ?? s.SourceChannelId} → <#{s.DiscordChannelId}>{telegram}");
            sb.AppendLine($"   {string.Join(", ", kinds)}{(s.PinLiveStreams ? ", 📌 закрепляет стримы" : "")}{(s.MentionRoleId is { } r ? $", упоминает <@&{r}>" : "")}");
        }

        await ctx.ReplyAsync(new DiscordEmbedBuilder()
            .WithTitle("Баллада о герольдах — деяния, что мы возвещаем")
            .WithDescription(sb.ToString())
            .Build());
    }

    [SlashCommand("remove", "Удалить подписку по её id (см. /notify list).")]
    public async Task RemoveAsync(
        InteractionContext ctx,
        [Option("id", "Id подписки.")] long id)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var idInt = (int)id;
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == idInt && s.GuildId == ctx.Guild!.Id);
        if (sub is null)
        {
            await ctx.ReplyAsync("Увы, ни один герольд не носит сей id в этих владениях, товарищ!", ephemeral: true);
            return;
        }

        // Also drop dedup rows so re-adding later primes cleanly.
        var seen = db.SeenContent.Where(x => x.SubscriptionId == sub.Id);
        db.SeenContent.RemoveRange(seen);
        db.Subscriptions.Remove(sub);
        await db.SaveChangesAsync();

        await ctx.ReplyAsync($"Герольд `#{id}` ускакал прочь, его квест завершён — вперёд, Росинант!", ephemeral: true);
    }

    private static bool IsTextChannel(DiscordChannel channel) =>
        channel.Type is ChannelType.Text or ChannelType.News;

    private static string YesNo(bool value) => value ? "yes" : "no";
}
