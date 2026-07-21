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

/// <summary>
/// Music/radio playback settings, bound from the "Music" config section. Playback runs through a
/// Lavalink server (which owns the Discord voice connection), so no ffmpeg/yt-dlp is needed here.
/// </summary>
public sealed class MusicOptions
{
    public const string Section = "Music";

    /// <summary>Base HTTP address of the Lavalink server (REST + WebSocket).</summary>
    public string LavalinkAddress { get; set; } = "http://localhost:2333";

    /// <summary>Lavalink server password (must match the server's <c>lavalink.server.password</c>).</summary>
    public string LavalinkPassphrase { get; set; } = "youshallnotpass";

    /// <summary>
    /// Search prefix used when a /play query is not a URL. "scsearch" = SoundCloud (built into
    /// Lavalink, no API key, no bot-detection); "ytsearch" = YouTube (needs the youtube-source
    /// plugin on the Lavalink server); "bcsearch" = Bandcamp.
    /// </summary>
    public string DefaultSearchMode { get; set; } = "scsearch";
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
