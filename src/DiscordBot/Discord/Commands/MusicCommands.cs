using System.Globalization;
using System.Text;
using DiscordBot.Data;
using DiscordBot.Discord.Music;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>Top-level music commands (/play, /skip, /queue, ...). Backed by a Lavalink server.</summary>
[SlashRequireGuild]
public sealed class MusicCommands : ApplicationCommandModule
{
    private const string SilenceReply = "Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!";

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

        var voiceChannelId = ctx.Member?.VoiceState?.Channel?.Id;
        if (voiceChannelId is null)
        {
            await ctx.ReplyAsync("Не страшись, но сперва встань в голосовом зале, дабы начать наш квест!", ephemeral: true);
            return;
        }

        await ctx.DeferAsync();

        var (player, error) = await _music.GetPlayerAsync(ctx.Guild!.Id, voiceChannelId, connect: true);
        if (player is null)
        {
            await ctx.EditAsync(ErrorMessage(error));
            return;
        }

        var isUrl = query.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || query.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var searchMode = isUrl ? TrackSearchMode.None : _music.DefaultSearchMode;

        var track = await _music.LoadTrackAsync(query.Trim(), searchMode);
        if (track is null)
        {
            await ctx.EditAsync("Увы, для сего квеста не сыскалось ни одной играбельной баллады, дорогой Санчо!");
            return;
        }

        var position = await player.PlayAsync(track, enqueue: true);

        if (position == 0)
        {
            await ctx.EditAsync(MusicPlaybackService.BuildNowPlayingEmbed(track, ctx.User.Id));
        }
        else
        {
            var embed = new DiscordEmbedBuilder()
                .WithColor(new DiscordColor(0x1DB954))
                .WithAuthor("Добавлено в наш список, ура!")
                .WithTitle(track.Title)
                .AddField("Позиция", $"#{position}", inline: true)
                .AddField("Длительность", MusicPlaybackService.FormatDuration(track), inline: true);
            if (track.Uri is not null)
            {
                embed.WithUrl(track.Uri.ToString());
            }
            await ctx.EditAsync(embed.Build());
        }
    }

    [SlashCommand("skip", "Пропустить текущую балладу.")]
    public async Task SkipAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player?.CurrentTrack is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        var title = player.CurrentTrack.Title;
        await player.SkipAsync();
        await ctx.ReplyAsync($"⏭️ Вперёд! Оставляем **{title}** позади и мчим к следующему стиху!");
    }

    [SlashCommand("stop", "Остановить балладу и очистить список.")]
    public async Task StopAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        await player.StopAsync();
        await ctx.ReplyAsync("⏹️ Баллада умолкла, а список очищен — до нового приключения, товарищ!");
    }

    [SlashCommand("pause", "Приостановить текущую балладу.")]
    public async Task PauseAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player?.CurrentTrack is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        if (player.State == PlayerState.Paused)
        {
            await ctx.ReplyAsync("Не страшись, баллада уже отдыхает, друг мой!");
            return;
        }
        await player.PauseAsync();
        await ctx.ReplyAsync("⏸️ Наш бард замирает, чтобы перевести дух!");
    }

    [SlashCommand("resume", "Возобновить балладу.")]
    public async Task ResumeAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        if (player.State != PlayerState.Paused)
        {
            await ctx.ReplyAsync("Увы, нет приостановленной баллады, чтобы оживить её, друг мой!");
            return;
        }
        await player.ResumeAsync();
        await ctx.ReplyAsync("▶️ Вперёд! Баллада вновь звенит над чертогом!");
    }

    [SlashCommand("volume", "Задать громкость баллады (0-200%).")]
    public async Task VolumeAsync(
        InteractionContext ctx,
        [Option("percent", "Громкость в процентах, 0-200.")] long percent)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        percent = Math.Clamp(percent, 0, 200);
        await player.SetVolumeAsync(percent / 100f);
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
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player?.CurrentTrack is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        if (!TryParsePosition(position, out var target))
        {
            await ctx.ReplyAsync("Увы, не разберу сей метки, друг мой! Используй секунды (`90`) или `m:ss` (`1:30`).", ephemeral: true);
            return;
        }
        if (!player.CurrentTrack.IsSeekable)
        {
            await ctx.ReplyAsync("Увы, сию балладу не перемотать, товарищ!", ephemeral: true);
            return;
        }
        await player.SeekAsync(target);
        await ctx.ReplyAsync($"⏩ Вперёд, к стиху на отметке **{Format(target)}**!");
    }

    [SlashCommand("loop", "Переключить повтор текущей баллады.")]
    public async Task LoopAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        var enabling = player.RepeatMode == TrackRepeatMode.None;
        player.RepeatMode = enabling ? TrackRepeatMode.Track : TrackRepeatMode.None;
        await ctx.ReplyAsync(enabling ? "🔁 Ура! Сия баллада отныне будет звучать вечно!" : "➡️ Бесконечный припев расколдован — вперёд, в поход!");
    }

    [SlashCommand("shuffle", "Перемешать список баллад.")]
    public async Task ShuffleAsync(InteractionContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is null || player.Queue.Count < 2)
        {
            await ctx.ReplyAsync("Увы, слишком мало баллад осталось, чтобы перемешать, друг мой!", ephemeral: true);
            return;
        }
        await player.Queue.ShuffleAsync();
        await ctx.ReplyAsync("🔀 Ура! Наш список весело перетасован заново!");
    }

    [SlashCommand("nowplaying", "Показать текущую балладу.")]
    public async Task NowPlayingAsync(InteractionContext ctx)
    {
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player?.CurrentTrack is null)
        {
            await ctx.ReplyAsync(SilenceReply, ephemeral: true);
            return;
        }
        await ctx.ReplyAsync(MusicPlaybackService.BuildNowPlayingEmbed(player.CurrentTrack));
    }

    [SlashCommand("queue", "Показать список грядущих баллад.")]
    public async Task QueueAsync(InteractionContext ctx)
    {
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is null || (player.CurrentTrack is null && player.Queue.Count == 0))
        {
            await ctx.ReplyAsync("Наш список пуст — ни единой баллады не ждёт впереди, друг мой!", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        if (player.CurrentTrack is { } current)
        {
            var link = current.Uri is null ? current.Title : $"[{current.Title}]({current.Uri})";
            sb.AppendLine($"**Ныне наш бард поёт:** {link} `{MusicPlaybackService.FormatDuration(current)}`");
            sb.AppendLine();
        }

        var queue = player.Queue;
        if (queue.Count > 0)
        {
            sb.AppendLine("**Баллады, что впереди:**");
            for (var i = 0; i < Math.Min(queue.Count, 10); i++)
            {
                var t = queue[i].Track;
                if (t is null)
                {
                    continue;
                }
                var link = t.Uri is null ? t.Title : $"[{t.Title}]({t.Uri})";
                sb.AppendLine($"`{i + 1}.` {link} `{MusicPlaybackService.FormatDuration(t)}`");
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
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is not null)
        {
            await player.DisconnectAsync();
        }
        await ctx.ReplyAsync("👋 Прощай покуда — я покидаю чертог! Вперёд, Росинант!");
    }

    private static string ErrorMessage(MusicPlaybackService.RetrieveError error) => error switch
    {
        MusicPlaybackService.RetrieveError.NotInVoice => "Не страшись, но сперва встань в голосовом зале, дабы начать наш квест!",
        MusicPlaybackService.RetrieveError.NotConnected => "Увы, чертог безмолвен — ни единой баллады не звучит, друг мой!",
        _ => "Увы, я не смог войти в твой чертог, товарищ — проверь же мои права!",
    };

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
