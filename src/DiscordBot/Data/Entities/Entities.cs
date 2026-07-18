namespace DiscordBot.Data.Entities;

/// <summary>The kind of external content source. Extendable to Twitch / Telegram later.</summary>
public enum ContentSourceType
{
    YouTube = 0,
    Twitch = 1,
    Telegram = 2,
}

/// <summary>Per-guild configuration for temp voice channels and music.</summary>
public sealed class GuildConfig
{
    /// <summary>Discord guild id (primary key).</summary>
    public ulong GuildId { get; set; }

    /// <summary>The "join to create" lobby voice channel. Joining it spawns a temp channel.</summary>
    public ulong? LobbyChannelId { get; set; }

    /// <summary>Category new temp channels are created under. Null = same category as the lobby.</summary>
    public ulong? TempCategoryId { get; set; }

    /// <summary>Default user limit for new temp channels. 0 = unlimited.</summary>
    public int DefaultUserLimit { get; set; }

    /// <summary>Name template for temp channels. "{user}" is replaced with the owner's display name.</summary>
    public string TempNameTemplate { get; set; } = "Зал {user}";

    /// <summary>Optional role required to use music commands. Null = everyone may.</summary>
    public ulong? DjRoleId { get; set; }

    /// <summary>Text channel where user /suggest submissions are posted. Null = not configured.</summary>
    public ulong? SuggestionsChannelId { get; set; }

    /// <summary>Role auto-granted to every member on join. Null = disabled.</summary>
    public ulong? AutoRoleId { get; set; }
}

/// <summary>A live, bot-created temporary voice channel and who owns it.</summary>
public sealed class TempVoiceChannel
{
    /// <summary>The created voice channel's id (primary key).</summary>
    public ulong ChannelId { get; set; }

    public ulong GuildId { get; set; }

    /// <summary>User who triggered creation and may control the channel.</summary>
    public ulong OwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A subscription: watch an external source and post notifications to a Discord channel.</summary>
public sealed class NotificationSubscription
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ContentSourceType SourceType { get; set; }

    /// <summary>Source identifier: a YouTube channel id (UC...), or later a Twitch login / TG handle.</summary>
    public string SourceChannelId { get; set; } = string.Empty;

    /// <summary>Human-friendly name resolved at add time (e.g. the channel title), for display.</summary>
    public string? SourceDisplayName { get; set; }

    /// <summary>Discord text channel that notifications are posted to.</summary>
    public ulong DiscordChannelId { get; set; }

    /// <summary>Optional role mentioned in notifications.</summary>
    public ulong? MentionRoleId { get; set; }

    public bool NotifyUploads { get; set; } = true;
    public bool NotifyShorts { get; set; } = true;
    public bool NotifyLiveStreams { get; set; } = true;

    /// <summary>Pin the message when a live stream starts. Twitch/YouTube live.</summary>
    public bool PinLiveStreams { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set once the first poll has recorded the channel's existing content as "seen" (so we never
    /// backfill-spam). Null means priming hasn't completed yet. This is an explicit marker rather
    /// than "are there any SeenContent rows?" so a brand-new channel with no videos still primes.
    /// </summary>
    public DateTimeOffset? PrimedAt { get; set; }
}

/// <summary>Dedup record so each subscription notifies about a given item exactly once.</summary>
public sealed class SeenContent
{
    public int Id { get; set; }

    public int SubscriptionId { get; set; }

    /// <summary>Source-native id of the item (videoId, stream id, ...).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Discriminates event kinds for the same id (e.g. a live video's "scheduled" vs "started").</summary>
    public string EventKind { get; set; } = string.Empty;

    public DateTimeOffset SeenAt { get; set; }
}
