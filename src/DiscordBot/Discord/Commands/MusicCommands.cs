using System.ComponentModel;
using System.Globalization;
using System.Text;
using DiscordBot.Data;
using DiscordBot.Discord.Music;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ContextChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>Top-level music commands (/play, /skip, /queue, ...). Backed by VoiceNext + yt-dlp/ffmpeg.</summary>
[RequireGuild]
public sealed class MusicCommands
{
    private readonly MusicPlaybackService _music;
    private readonly IServiceScopeFactory _scopeFactory;

    public MusicCommands(MusicPlaybackService music, IServiceScopeFactory scopeFactory)
    {
        _music = music;
        _scopeFactory = scopeFactory;
    }

    [Command("play")]
    [Description("Сыграть балладу с SoundCloud или иного вольного источника (ссылка или поиск).")]
    public async ValueTask PlayAsync(
        SlashCommandContext ctx,
        [Description("Ссылка или поисковый запрос.")] string query)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }

        var voiceChannel = await GetVoiceChannelAsync(ctx.Member);
        if (voiceChannel is null)
        {
            await ctx.RespondAsync("Не страшись, но сперва встань в голосовом зале, дабы начать наш квест!", ephemeral: true);
            return;
        }

        await ctx.DeferResponseAsync();

        ResolvedTrack? track;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            track = await _music.Resolver.ResolveAsync(query, ctx.User.Id, timeout.Token);
        }
        catch (Exception)
        {
            await ctx.EditResponseAsync("Увы, друг мой, сия баллада ускользает — поиск потерпел неудачу!");
            return;
        }

        if (track is null)
        {
            await ctx.EditResponseAsync("Увы, для сего квеста не сыскалось ни одной играбельной баллады, дорогой Санчо!");
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
            await ctx.EditResponseAsync("Увы, я не смог войти в твой чертог, товарищ — проверь же мои права!");
            return;
        }

        if (position < 0)
        {
            await ctx.EditResponseAsync("Увы, баллада ускользнула из нашего списка — попробуй же снова, друг мой!");
            return;
        }

        var willPlayNow = position == 1 && player.Current is null;

        if (willPlayNow)
        {
            // The now-playing embed will be posted by the player loop; acknowledge the command.
            await ctx.EditResponseAsync($"▶️ Ура! Наш бард готовит балладу **{track.Title}**…");
        }
        else
        {
            await ctx.EditResponseAsync(new DiscordEmbedBuilder()
                .WithColor(new DiscordColor(0x1DB954))
                .WithAuthor("Добавлено в наш список, ура!")
                .WithTitle(track.Title)
                .WithUrl(track.WebpageUrl)
                .AddField("Позиция", $"#{position}", inline: true)
                .AddField("Длительность", track.DurationString, inline: true)
                .Build());
        }
    }

    [Command("skip")]
    [Description("Пропустить текущую балладу.")]
    public async ValueTask SkipAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        var title = player.Current.Title;
        player.Skip();
        await ctx.RespondAsync($"⏭️ Вперёд! Оставляем **{title}** позади и мчим к следующему стиху!");
    }

    [Command("stop")]
    [Description("Остановить балладу и очистить список.")]
    public async ValueTask StopAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        player.Stop();
        await ctx.RespondAsync("⏹️ Баллада умолкла, а список очищен — до нового приключения, товарищ!");
    }

    [Command("pause")]
    [Description("Приостановить текущую балладу.")]
    public async ValueTask PauseAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null || player.Current is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(player.Pause() ? "⏸️ Наш бард замирает, чтобы перевести дух!" : "Не страшись, баллада уже отдыхает, друг мой!");
    }

    [Command("resume")]
    [Description("Возобновить балладу.")]
    public async ValueTask ResumeAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(await player.ResumeAsync() ? "▶️ Вперёд! Баллада вновь звенит над чертогом!" : "Увы, нет приостановленной баллады, чтобы оживить её, друг мой!");
    }

    [Command("volume")]
    [Description("Задать громкость баллады (0-200%).")]
    public async ValueTask VolumeAsync(
        SlashCommandContext ctx,
        [Description("Громкость в процентах, 0-200.")] int percent)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        percent = Math.Clamp(percent, 0, 200);
        player.SetVolume(percent / 100.0);
        await ctx.RespondAsync($"🔊 Славно! Наш бард поёт теперь в **{percent}%**!");
    }

    [Command("seek")]
    [Description("Перемотать текущую балладу (напр. 90 или 1:30).")]
    public async ValueTask SeekAsync(
        SlashCommandContext ctx,
        [Description("Метка в секундах или m:ss.")] string position)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        if (!TryParsePosition(position, out var target))
        {
            await ctx.RespondAsync("Увы, не разберу сей метки, друг мой! Используй секунды (`90`) или `m:ss` (`1:30`).", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(player.Seek(target)
            ? $"⏩ Вперёд, к стиху на отметке **{Format(target)}**!"
            : "Увы, не сумел перескочить к сей метке, товарищ!");
    }

    [Command("loop")]
    [Description("Переключить повтор текущей баллады.")]
    public async ValueTask LoopAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        player.Loop = !player.Loop;
        await ctx.RespondAsync(player.Loop ? "🔁 Ура! Сия баллада отныне будет звучать вечно!" : "➡️ Бесконечный припев расколдован — вперёд, в поход!");
    }

    [Command("shuffle")]
    [Description("Перемешать список баллад.")]
    public async ValueTask ShuffleAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(player.Shuffle() ? "🔀 Ура! Наш список весело перетасован заново!" : "Увы, слишком мало баллад осталось, чтобы перемешать, друг мой!");
    }

    [Command("nowplaying")]
    [Description("Показать текущую балладу.")]
    public async ValueTask NowPlayingAsync(SlashCommandContext ctx)
    {
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.RespondAsync("Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(MusicPlaybackService.BuildNowPlayingEmbed(player.Current));
    }

    [Command("queue")]
    [Description("Показать список грядущих баллад.")]
    public async ValueTask QueueAsync(SlashCommandContext ctx)
    {
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null || (player.Current is null && player.QueueSnapshot().Count == 0))
        {
            await ctx.RespondAsync("Наш список пуст — ни единой баллады не ждёт впереди, друг мой!", ephemeral: true);
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

        await ctx.RespondAsync(new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithTitle("Наш список баллад")
            .WithDescription(sb.ToString())
            .Build());
    }

    [Command("leave")]
    [Description("Покинуть голосовой зал.")]
    public async ValueTask LeaveAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        await _music.DisconnectAsync(ctx.Guild!.Id);
        await ctx.RespondAsync("👋 Прощай покуда — я покидаю чертог! Вперёд, Росинант!");
    }

    /// <summary>If a DJ role is configured, require the caller to have it (admins always pass).</summary>
    private async Task<bool> EnsureDjAsync(SlashCommandContext ctx)
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

        var isAdmin = ctx.Member.Permissions.HasPermission(DiscordPermission.Administrator);
        var hasRole = ctx.Member.Roles.Any(r => r.Id == djRoleId.Value);
        if (isAdmin || hasRole)
        {
            return true;
        }

        await ctx.RespondAsync($"Стой, товарищ! Лишь носитель роли <@&{djRoleId.Value}> волен повелевать нашими балладами!", ephemeral: true);
        return false;
    }

    private static async Task<DiscordChannel?> GetVoiceChannelAsync(DiscordMember? member)
    {
        var state = member?.VoiceState;
        if (state?.ChannelId is null)
        {
            return null;
        }
        return await state.GetChannelAsync();
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
