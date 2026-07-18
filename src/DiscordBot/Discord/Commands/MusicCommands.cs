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
    [Description("Play a track from SoundCloud or another open source (URL or search text).")]
    public async ValueTask PlayAsync(
        SlashCommandContext ctx,
        [Description("URL or search query.")] string query)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }

        var voiceChannel = await GetVoiceChannelAsync(ctx.Member);
        if (voiceChannel is null)
        {
            await ctx.RespondAsync("Join a voice channel first.", ephemeral: true);
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
            await ctx.EditResponseAsync("Failed to resolve that query.");
            return;
        }

        if (track is null)
        {
            await ctx.EditResponseAsync("Nothing playable found for that query.");
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
            await ctx.EditResponseAsync("I couldn't join your voice channel (check my permissions).");
            return;
        }

        if (position < 0)
        {
            await ctx.EditResponseAsync("Couldn't queue the track — please try again.");
            return;
        }

        var willPlayNow = position == 1 && player.Current is null;

        if (willPlayNow)
        {
            // The now-playing embed will be posted by the player loop; acknowledge the command.
            await ctx.EditResponseAsync($"▶️ Loading **{track.Title}**…");
        }
        else
        {
            await ctx.EditResponseAsync(new DiscordEmbedBuilder()
                .WithColor(new DiscordColor(0x1DB954))
                .WithAuthor("Added to queue")
                .WithTitle(track.Title)
                .WithUrl(track.WebpageUrl)
                .AddField("Position", $"#{position}", inline: true)
                .AddField("Duration", track.DurationString, inline: true)
                .Build());
        }
    }

    [Command("skip")]
    [Description("Skip the current track.")]
    public async ValueTask SkipAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        var title = player.Current.Title;
        player.Skip();
        await ctx.RespondAsync($"⏭️ Skipped **{title}**.");
    }

    [Command("stop")]
    [Description("Stop playback and clear the queue.")]
    public async ValueTask StopAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        player.Stop();
        await ctx.RespondAsync("⏹️ Stopped and cleared the queue.");
    }

    [Command("pause")]
    [Description("Pause the current track.")]
    public async ValueTask PauseAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null || player.Current is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(player.Pause() ? "⏸️ Paused." : "Already paused.");
    }

    [Command("resume")]
    [Description("Resume playback.")]
    public async ValueTask ResumeAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(await player.ResumeAsync() ? "▶️ Resumed." : "Nothing to resume.");
    }

    [Command("volume")]
    [Description("Set playback volume (0-200%).")]
    public async ValueTask VolumeAsync(
        SlashCommandContext ctx,
        [Description("Volume percent, 0-200.")] int percent)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        percent = Math.Clamp(percent, 0, 200);
        player.SetVolume(percent / 100.0);
        await ctx.RespondAsync($"🔊 Volume set to **{percent}%**.");
    }

    [Command("seek")]
    [Description("Seek within the current track (e.g. 90 or 1:30).")]
    public async ValueTask SeekAsync(
        SlashCommandContext ctx,
        [Description("Position as seconds or m:ss.")] string position)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        if (!TryParsePosition(position, out var target))
        {
            await ctx.RespondAsync("Couldn't parse that position. Use seconds (`90`) or `m:ss` (`1:30`).", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(player.Seek(target)
            ? $"⏩ Seeking to **{Format(target)}**."
            : "Couldn't seek.");
    }

    [Command("loop")]
    [Description("Toggle looping the current track.")]
    public async ValueTask LoopAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        player.Loop = !player.Loop;
        await ctx.RespondAsync(player.Loop ? "🔁 Loop enabled." : "➡️ Loop disabled.");
    }

    [Command("shuffle")]
    [Description("Shuffle the queue.")]
    public async ValueTask ShuffleAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(player.Shuffle() ? "🔀 Queue shuffled." : "Not enough tracks to shuffle.");
    }

    [Command("nowplaying")]
    [Description("Show the current track.")]
    public async ValueTask NowPlayingAsync(SlashCommandContext ctx)
    {
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player?.Current is null)
        {
            await ctx.RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }
        await ctx.RespondAsync(MusicPlaybackService.BuildNowPlayingEmbed(player.Current));
    }

    [Command("queue")]
    [Description("Show the upcoming queue.")]
    public async ValueTask QueueAsync(SlashCommandContext ctx)
    {
        var player = _music.TryGet(ctx.Guild!.Id);
        if (player is null || (player.Current is null && player.QueueSnapshot().Count == 0))
        {
            await ctx.RespondAsync("The queue is empty.", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        if (player.Current is { } current)
        {
            sb.AppendLine($"**Now playing:** [{current.Title}]({current.WebpageUrl}) `{current.DurationString}`");
            sb.AppendLine();
        }

        var queue = player.QueueSnapshot();
        if (queue.Count > 0)
        {
            sb.AppendLine("**Up next:**");
            for (var i = 0; i < Math.Min(queue.Count, 10); i++)
            {
                sb.AppendLine($"`{i + 1}.` [{queue[i].Title}]({queue[i].WebpageUrl}) `{queue[i].DurationString}`");
            }
            if (queue.Count > 10)
            {
                sb.AppendLine($"…and {queue.Count - 10} more.");
            }
        }

        await ctx.RespondAsync(new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithTitle("Queue")
            .WithDescription(sb.ToString())
            .Build());
    }

    [Command("leave")]
    [Description("Disconnect the bot from voice.")]
    public async ValueTask LeaveAsync(SlashCommandContext ctx)
    {
        if (!await EnsureDjAsync(ctx))
        {
            return;
        }
        await _music.DisconnectAsync(ctx.Guild!.Id);
        await ctx.RespondAsync("👋 Left the voice channel.");
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

        await ctx.RespondAsync($"You need the <@&{djRoleId.Value}> role to use music commands.", ephemeral: true);
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
