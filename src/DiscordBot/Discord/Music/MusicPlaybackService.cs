using DiscordBot.Configuration;
using DSharpPlus.Entities;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Options;

namespace DiscordBot.Discord.Music;

/// <summary>
/// Thin wrapper over Lavalink4NET's <see cref="IAudioService"/>. Commands go through this to retrieve
/// the per-guild <see cref="QueuedLavalinkPlayer"/>, load tracks and control playback. Lavalink itself
/// owns the Discord voice connection, so there is no VoiceNext/ffmpeg here.
/// </summary>
public sealed class MusicPlaybackService
{
    private readonly IAudioService _audio;
    private readonly MusicOptions _options;

    public MusicPlaybackService(IAudioService audio, IOptions<MusicOptions> options)
    {
        _audio = audio;
        _options = options.Value;
    }

    /// <summary>The configured default search mode (SoundCloud unless overridden).</summary>
    public TrackSearchMode DefaultSearchMode => new(_options.DefaultSearchMode);

    /// <summary>Why a player retrieval failed (used to pick a persona error message).</summary>
    public enum RetrieveError
    {
        None,
        NotInVoice,
        NotConnected,
        Other,
    }

    /// <summary>
    /// Retrieve the guild's queued player, optionally joining the caller's voice channel. Returns the
    /// player on success, or a <see cref="RetrieveError"/> describing why it could not be obtained.
    /// </summary>
    public async Task<(QueuedLavalinkPlayer? Player, RetrieveError Error)> GetPlayerAsync(
        ulong guildId, ulong? voiceChannelId, bool connect)
    {
        var retrieveOptions = new PlayerRetrieveOptions(
            ChannelBehavior: connect ? PlayerChannelBehavior.Join : PlayerChannelBehavior.None);

        var playerOptions = new QueuedLavalinkPlayerOptions { HistoryCapacity = 100 };

        var result = await _audio.Players.RetrieveAsync(
            guildId,
            voiceChannelId,
            PlayerFactory.Queued,
            Options.Create(playerOptions),
            retrieveOptions);

        if (result.IsSuccess)
        {
            return (result.Player, RetrieveError.None);
        }

        var error = result.Status switch
        {
            PlayerRetrieveStatus.UserNotInVoiceChannel => RetrieveError.NotInVoice,
            PlayerRetrieveStatus.BotNotConnected => RetrieveError.NotConnected,
            _ => RetrieveError.Other,
        };
        return (null, error);
    }

    /// <summary>Get the guild's existing player without joining a channel (null if none).</summary>
    public ValueTask<QueuedLavalinkPlayer?> TryGetAsync(ulong guildId) =>
        _audio.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildId);

    /// <summary>Resolve a single track from a URL or a search query.</summary>
    public ValueTask<LavalinkTrack?> LoadTrackAsync(string identifier, TrackSearchMode searchMode) =>
        _audio.Tracks.LoadTrackAsync(identifier, searchMode);

    /// <summary>Now-playing embed for a Lavalink track (Don Quixote flavour preserved).</summary>
    public static DiscordEmbed BuildNowPlayingEmbed(LavalinkTrack track, ulong requestedBy = 0)
    {
        var embed = new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithAuthor("Ныне звучит наша баллада! Ура!")
            .WithTitle(track.Title)
            .AddField("Длительность", FormatDuration(track), inline: true);

        if (track.Uri is not null)
        {
            embed.WithUrl(track.Uri.ToString());
        }
        if (!string.IsNullOrEmpty(track.Author))
        {
            embed.AddField("Доблестный бард", track.Author, inline: true);
        }
        if (requestedBy != 0)
        {
            embed.AddField("Квест поручил", $"<@{requestedBy}>", inline: true);
        }
        if (track.ArtworkUri is not null)
        {
            embed.WithThumbnail(track.ArtworkUri.ToString());
        }

        return embed.Build();
    }

    /// <summary>"LIVE" for streams, otherwise m:ss / h:mm:ss.</summary>
    public static string FormatDuration(LavalinkTrack track) =>
        track.IsLiveStream ? "🔴 LIVE" : FormatDuration(track.Duration);

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
}
