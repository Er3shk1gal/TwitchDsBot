using System.Collections.Concurrent;
using DiscordBot.Configuration;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Discord.Music;

/// <summary>
/// Owns one <see cref="GuildMusicPlayer"/> per guild and the VoiceNext connection behind it.
/// Commands go through this service to enqueue tracks and control playback.
/// </summary>
public sealed class MusicPlaybackService
{
    private readonly VoiceNextExtension _voiceNext;
    private readonly TrackResolver _resolver;
    private readonly MusicOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MusicPlaybackService> _logger;

    private readonly ConcurrentDictionary<ulong, GuildMusicPlayer> _players = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public MusicPlaybackService(
        VoiceNextExtension voiceNext,
        TrackResolver resolver,
        IOptions<MusicOptions> options,
        ILoggerFactory loggerFactory)
    {
        _voiceNext = voiceNext;
        _resolver = resolver;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MusicPlaybackService>();
    }

    public TrackResolver Resolver => _resolver;

    public GuildMusicPlayer? TryGet(ulong guildId) =>
        _players.TryGetValue(guildId, out var player) ? player : null;

    /// <summary>
    /// Ensure the bot is connected to <paramref name="voiceChannel"/> and return the guild's player,
    /// creating it (and the voice connection) on first use.
    /// </summary>
    public async Task<GuildMusicPlayer> GetOrCreateAsync(DiscordChannel voiceChannel, DiscordChannel announceChannel)
    {
        var guild = voiceChannel.Guild;

        if (_players.TryGetValue(guild.Id, out var existing) && !existing.IsTerminated)
        {
            existing.AnnouncementChannel = announceChannel;
            return existing;
        }

        // _connectLock serializes GetOrCreate against DisconnectAsync so a torn-down player fully
        // releases its voice connection before we build a replacement over the same guild.
        await _connectLock.WaitAsync();
        try
        {
            if (_players.TryGetValue(guild.Id, out existing) && !existing.IsTerminated)
            {
                existing.AnnouncementChannel = announceChannel;
                return existing;
            }

            var connection = await ConnectWithFallbackAsync(guild, voiceChannel);

            var player = new GuildMusicPlayer(
                guild.Id,
                connection,
                _resolver,
                _options,
                _loggerFactory.CreateLogger<GuildMusicPlayer>(),
                announce: AnnounceNowPlayingAsync,
                onIdle: OnPlayerIdle)
            {
                AnnouncementChannel = announceChannel,
            };
            player.Start();

            _players[guild.Id] = player;
            return player;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Connect to a voice channel, tolerating a DSharpPlus 5 nightly quirk where VoiceNext's
    /// ConnectAsync task stays pending even though the bot has actually joined the channel. We poll
    /// GetConnection and use the live connection the moment it registers, instead of waiting on the
    /// (possibly hanging) task.
    /// </summary>
    private async Task<VoiceNextConnection> ConnectWithFallbackAsync(DiscordGuild guild, DiscordChannel voiceChannel)
    {
        var existing = _voiceNext.GetConnection(guild);
        if (existing is not null)
        {
            return existing;
        }

        Task<VoiceNextConnection> connectTask;
        try
        {
            connectTask = _voiceNext.ConnectAsync(voiceChannel);
        }
        catch
        {
            // e.g. "already connected in this guild" — use whatever is registered.
            var registered = _voiceNext.GetConnection(guild);
            if (registered is not null)
            {
                return registered;
            }
            throw;
        }

        // Poll for up to ~30s: whichever comes first — the task finishing or the connection appearing.
        for (var i = 0; i < 120; i++)
        {
            await Task.WhenAny(connectTask, Task.Delay(250));

            var live = _voiceNext.GetConnection(guild);
            if (live is not null)
            {
                ObserveFaults(connectTask);
                return live;
            }

            if (connectTask.IsCompleted)
            {
                break;
            }
        }

        if (connectTask.IsCompletedSuccessfully)
        {
            return connectTask.Result;
        }

        ObserveFaults(connectTask);
        throw new TimeoutException("Voice connection didn't establish in time (voice gateway/UDP unreachable?).");
    }

    // Swallow a possibly-still-pending connect task's eventual fault so it isn't an unobserved exception.
    private static void ObserveFaults(Task task) =>
        _ = task.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);

    /// <summary>Stop playback, disconnect and dispose the guild's player.</summary>
    public async Task DisconnectAsync(ulong guildId)
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_players.TryRemove(guildId, out var player))
            {
                await player.DisposeAsync();
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // Called from the player's own loop as it winds down after an idle timeout; must not dispose
    // (that would await the loop from within itself). Remove only if this exact instance is still
    // mapped, so it can't evict a replacement player created under the same guild id.
    private void OnPlayerIdle(GuildMusicPlayer player) =>
        ((ICollection<KeyValuePair<ulong, GuildMusicPlayer>>)_players)
            .Remove(new KeyValuePair<ulong, GuildMusicPlayer>(player.GuildId, player));

    private async Task AnnounceNowPlayingAsync(ResolvedTrack track, GuildMusicPlayer player)
    {
        var channel = player.AnnouncementChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendMessageAsync(BuildNowPlayingEmbed(track));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to post now-playing message.");
        }
    }

    public static DiscordEmbed BuildNowPlayingEmbed(ResolvedTrack track)
    {
        var embed = new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithAuthor("Ныне звучит наша баллада! Ура!")
            .WithTitle(track.Title)
            .WithUrl(track.WebpageUrl)
            .AddField("Длительность", track.DurationString, inline: true);

        if (!string.IsNullOrEmpty(track.Uploader))
        {
            embed.AddField("Доблестный бард", track.Uploader, inline: true);
        }
        if (track.RequestedBy != 0)
        {
            embed.AddField("Квест поручил", $"<@{track.RequestedBy}>", inline: true);
        }
        if (!string.IsNullOrEmpty(track.ThumbnailUrl))
        {
            embed.WithThumbnail(track.ThumbnailUrl);
        }

        return embed.Build();
    }
}
