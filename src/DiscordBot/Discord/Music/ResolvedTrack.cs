namespace DiscordBot.Discord.Music;

/// <summary>A track resolved by yt-dlp: display metadata plus a direct audio stream URL for ffmpeg.</summary>
public sealed record ResolvedTrack
{
    public required string Title { get; init; }
    public required string StreamUrl { get; init; }
    public required string WebpageUrl { get; init; }
    public string? Uploader { get; init; }
    public string? ThumbnailUrl { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>Id of the user who requested the track (for "requested by" display).</summary>
    public ulong RequestedBy { get; init; }

    /// <summary>True for direct stream URLs (radio): play the URL as-is, skip the yt-dlp re-resolve.</summary>
    public bool IsDirectStream { get; init; }

    public string DurationString => Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : "live/unknown";
}
