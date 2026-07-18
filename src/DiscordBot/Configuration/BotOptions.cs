namespace DiscordBot.Configuration;

/// <summary>Discord gateway / bot settings, bound from the "Discord" config section.</summary>
public sealed class DiscordOptions
{
    public const string Section = "Discord";

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// When set, slash commands register to this guild instantly (great for development).
    /// When null, commands register globally (propagation can take up to an hour).
    /// </summary>
    public ulong? DebugGuildId { get; set; }
}

/// <summary>Music playback settings, bound from the "Music" config section.</summary>
public sealed class MusicOptions
{
    public const string Section = "Music";

    /// <summary>Path to the ffmpeg executable. "ffmpeg" resolves via PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path to the yt-dlp executable (resolves tracks from SoundCloud &amp; other sources). "yt-dlp" resolves via PATH.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Default search provider used when a /play query is not a URL. "ytsearch" = YouTube (widest
    /// catalogue, avoids SoundCloud's DRM-protected tracks); "scsearch" = SoundCloud. If a search on
    /// the default provider finds nothing, the resolver automatically retries the other one.
    /// </summary>
    public string DefaultSearchPrefix { get; set; } = "ytsearch";

    /// <summary>Default playback volume (0.0–2.0, where 1.0 = 100%).</summary>
    public double DefaultVolume { get; set; } = 1.0;

    /// <summary>Leave the voice channel after this many seconds with nothing playing / no listeners.</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Optional path to a Netscape-format cookies.txt passed to yt-dlp (<c>--cookies</c>). Unlocks
    /// YouTube on server/datacenter IPs that hit "Sign in to confirm you're not a bot". Leave empty
    /// to disable; the file must exist at runtime or it is ignored.
    /// </summary>
    public string CookiesPath { get; set; } = string.Empty;
}

/// <summary>YouTube Data API v3 notifier settings, bound from the "YouTube" config section.</summary>
public sealed class YouTubeOptions
{
    public const string Section = "YouTube";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>How often to poll each subscribed channel. Mind the API quota (see README).</summary>
    public int PollIntervalSeconds { get; set; } = 180;
}

/// <summary>Database settings, bound from the "Database" config section.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public string ConnectionString { get; set; } = "Data Source=bot.db";
}
