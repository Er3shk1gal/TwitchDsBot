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

/// <summary>
/// Twitch Helix API settings (stream-live polling for /notify twitch), bound from the "Twitch"
/// config section. Uses an app access token (client-credentials grant) — no user login needed.
/// </summary>
public sealed class TwitchOptions
{
    public const string Section = "Twitch";

    /// <summary>Client id of a Twitch application — <see href="https://dev.twitch.tv/console/apps"/>.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;
}

/// <summary>Telegram Bot API settings (notification sink), bound from the "Telegram" config section.</summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    /// <summary>Bot token from @BotFather, e.g. "123456:AAE...". Omit to disable the Telegram sink.</summary>
    public string BotToken { get; set; } = string.Empty;
}

/// <summary>
/// Settings for the Twitch chat responder (posts a canned "my links" message on a command), bound
/// from the "TwitchChat" config section. Needs a Twitch **user** OAuth token (chat:read + chat:edit
/// scopes) for the account that should post — this is a different credential than
/// <see cref="TwitchOptions"/>'s app token, which can't send chat messages.
/// </summary>
public sealed class TwitchChatOptions
{
    public const string Section = "TwitchChat";

    /// <summary>User access token, with or without the "oauth:" prefix (added automatically).</summary>
    public string OAuthToken { get; set; } = string.Empty;

    /// <summary>Login (not display name) of the account the token belongs to.</summary>
    public string BotUsername { get; set; } = string.Empty;

    /// <summary>Channel logins to join (without '#'), e.g. ["mychannel"].</summary>
    public List<string> Channels { get; set; } = new();

    /// <summary>Chat command that triggers the reply, case-insensitive (e.g. "!links").</summary>
    public string Command { get; set; } = "!links";

    /// <summary>The message posted back in chat when the command fires.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Minimum seconds between two triggers in the same channel (anti-spam).</summary>
    public int CooldownSeconds { get; set; } = 15;
}
