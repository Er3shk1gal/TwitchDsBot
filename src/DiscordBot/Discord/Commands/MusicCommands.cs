using System.Globalization;
using System.Text;
using DiscordBot.Data;
using DiscordBot.Discord.Music;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>Top-level music commands (/play, /skip, /queue, ...). Backed by VoiceNext + yt-dlp/ffmpeg.</summary>
[SlashRequireGuild]
public sealed class MusicCommands : ApplicationCommandModule
{
    private readonly MusicPlaybackService _music;
    private readonly IServiceScopeFactory _scopeFactory;

    public MusicCommands(MusicPlaybackService music, IServiceScopeFactory scopeFactory)
    {
        _music = music;
        _scopeFactory = scopeFactory;
    }

    [SlashCommand("play", "Сыграть балладу с SoundCloud или иного вольного источника (ссылка или поиск).")]
    public async Task PlayAsync(
        InteractionContext ctx,
        [Option("query", "Ссылка или поисковый запрос.")] string query)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }

        var voiceChannel = ctx.Member?.VoiceState?.Channel;
        if (voiceChannel is null)
        {
            await ctx.ReplyAsync("Не страшись, но сперва встань в голосовом зале, дабы начать наш квест!", ephemeral: true);
            return;
        }

        await ctx.DeferAsync();

        ResolvedTrack? track;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            track = await _music.Resolver.ResolveAsync(query, ctx.User.Id, timeout.Token);
        }
        catch (Exception)
        {
            await ctx.EditAsync("Увы, друг мой, сия баллада ускользает — поиск потерпел неудачу!");
            return;
        }

        if (track is null)
        {
            await ctx.EditAsync("Увы, для сего квеста не сыскалось ни одной играбельной баллады, дорогой Санчо!");
            return;
        }

        GuildMusicPlayer player;
        int position;
        try
        {
            player = await _music.GetOrCreateAsync(voiceChannel, ctx.Channel);
            position = player.Enqueue(track);
            if (position < 0)
            {
                // The player terminated (idled out) between creation and enqueue — rebuild once.
                player = await _music.GetOrCreateAsync(voiceChannel, ctx.Channel);
                position = player.Enqueue(track);
            }
        }
        catch (Exception)
        {
            await ctx.EditAsync("Увы, я не смог войти в твой чертог, товарищ — проверь же мои права!");
            return;
        }

        if (position < 0)
        {
            await ctx.EditAsync("Увы, баллада ускользнула из нашего списка — попробуй же снова, друг мой!");
            return;
        }

        var willPlayNow = position == 1 && player.Current is null;

        if (willPlayNow)
        {
            // The now-playing embed will be posted by the player loop; acknowledge the command.
            await ctx.EditAsync($"▶️ Ура! Наш бард готовит балладу **{track.Title}**…");
        }
        else
        {
            await ctx.EditAsync(new DiscordEmbedBuilder()
                .WithColor(new DiscordColor(0x1DB954))
                .WithAuthor("Добавлено в наш список, ура!")
                .WithTitle(track.Title)
                .WithUrl(track.WebpageUrl)
                .AddField("Позиция", $"#{position}", inline: true)
                .AddField("Длительность", track.DurationString, inline: true)
                .Build());
        }
    }

    [SlashCommand("skip", "Пропустить текущую балладу.")]
    public async Task SkipAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        var title = player.Current.Title;
        player.Skip();
        await ctx.ReplyAsync($"⏭️ Вперёд! Оставляем **{title}** позади и мчим к следующему стиху!");
    }

    [SlashCommand("stop", "Остановить балладу и очистить список.")]
    public async Task StopAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        player.Stop();
        await ctx.ReplyAsync("⏹️ Баллада умолкла, а список очищен — до нового приключения, товарищ!");
    }

    [SlashCommand("pause", "Приостановить текущую балладу.")]
    public async Task PauseAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null || player.Current is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.ReplyAsync(player.Pause() ? "⏸️ Наш бард замирает, чтобы перевести дух!" : "Не страшись, баллада уже отдыхает, друг мой!");
    }

    [SlashCommand("resume", "Возобновить балладу.")]
    public async Task ResumeAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.ReplyAsync(await player.ResumeAsync() ? "▶️ Вперёд! Баллада вновь звенит над чертогом!" : "Увы, нет приостановленной баллады, чтобы оживить её, друг мой!");
    }

    [SlashCommand("volume", "Задать громкость баллады (0-200%).")]
    public async Task VolumeAsync(
        InteractionContext ctx,
        [Option("percent", "Громкость в процентах, 0-200.")] int percent)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        percent = Math.Clamp(percent, 0, 200);
        player.SetVolume(percent / 100.0);
        await ctx.ReplyAsync($"🔊 Славно! Наш бард поёт теперь в **{percent}%**!");
    }

    [SlashCommand("seek", "Перемотать текущую балладу (напр. 90 или 1:30).")]
    public async Task SeekAsync(
        InteractionContext ctx,
        [Option("position", "Метка в секундах или m:ss.")] string position)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        if (!TryParsePosition(position, out var target))
        {
            await ctx.ReplyAsync("Увы, не разберу сей метки, друг мой! Используй секунды (`90`) или `m:ss` (`1:30`).", ephemeral: true);
            return;
        }
        await ctx.ReplyAsync(player.Seek(target)
            ? $"⏩ Вперёд, к стиху на отметке **{Format(target)}**!"
            : "Увы, не сумел перескочить к сей метке, товарищ!");
    }

    [SlashCommand("loop", "Переключить повтор текущей баллады.")]
    public async Task LoopAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        player.Loop = !player.Loop;
        await ctx.ReplyAsync(player.Loop ? "🔁 Ура! Сия баллада отныне будет звучать вечно!" : "➡️ Бесконечный припев расколдован — вперёд, в поход!");
    }

    [SlashCommand("shuffle", "Перемешать список баллад.")]
    public async Task ShuffleAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.ReplyAsync(player.Shuffle() ? "🔀 Ура! Наш список весело перетасован заново!" : "Увы, слишком мало баллад осталось, чтобы перемешать, друг мой!");
    }

    [SlashCommand("nowplaying", "Показать текущую балладу.")]
    public async Task NowPlayingAsync(InteractionContext ctx)
    {
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.ReplyAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.ReplyAsync(MusicPlaybackService.BuildNowPlayingEmbed(player.Current));
    }

    [SlashCommand("queue", "Показать список грядущих баллад.")]
    public async Task QueueAsync(InteractionContext ctx)
    {
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null || (player.Current is null && player.QueueSnapshot().Count == 0))
        {
            await ctx.ReplyAsync("Наш список пуст — ни единой баллады не ждёт впереди, друг мой!", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        if (player.Current is { } current)
        {
            sb.AppendLine($"**Ныне наш бард поёт:** [{current.Title}]({current.WebpageUrl}) `{current.DurationString}`");
            sb.AppendLine();
        }

        var queue = player.QueueSnapshot();
        if (queue.Count > 0)
        {
            sb.AppendLine("**Баллады, что впереди:**");
            for (var i = 0; i < Math.Min(queue.Count, 10); i++)
            {
                sb.AppendLine($"`{i + 1}.` [{queue[i].Title}]({queue[i].WebpageUrl}) `{queue[i].DurationString}`");
            }
            if (queue.Count > 10)
            {
                sb.AppendLine($"…и ещё {queue.Count - 10} приключений ждут впереди!");
            }
        }

        await ctx.ReplyAsync(new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithTitle("Наш список баллад")
            .WithDescription(sb.ToString())
            .Build());
    }

    [SlashCommand("leave", "Покинуть голосовой зал.")]
    public async Task LeaveAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        await _music.DisconnectAsync(ctx.Guild!.Id);
        await ctx.ReplyAsync("👋 Прощай покуда — я покидаю чертог! Вперёд, Росинант!");
    }

    /// <summary>If a DJ role is configured, require the caller to have it (admins always pass).</summary>
    private async Task<bool> EnsureDjAsync(InteractionContext ctx)
    {
        ulong? djRoleId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            djRoleId = await db.GuildConfigs
                .AsNoTracking()
                .Where(c => c.GuildId == ctx.Guild!.Id)
                .Select(c => c.DjRoleId)
                .FirstOrDefaultAsync();
        }

        if (djRoleId is null || ctx.Member is null)
        {
            return true;
        }

        var isAdmin = ctx.Member.Permissions.HasPermission(Permissions.Administrator);
        var hasRole = ctx.Member.Roles.Any(r => r.Id == djRoleId.Value);
        if (isAdmin || hasRole)
        {
            return true;
        }

        await ctx.ReplyAsync($"Стой, товарищ! Лишь носитель роли <@&{djRoleId.Value}> волен повелевать нашими балладами!", ephemeral: true);
        return false;
    }

    private static bool TryParsePosition(string input, out TimeSpan result)
    {
        input = input.Trim();
        if (input.Contains(':'))
        {
            var parts = input.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var m)
                && int.TryParse(parts[1], out var s))
            {
                result = new TimeSpan(0, m, s);
                return true;
            }
        }
        else if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            result = TimeSpan.FromSeconds(seconds);
            return true;
        }

        result = default;
        return false;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
