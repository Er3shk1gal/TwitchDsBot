using DiscordBot.Data.Entities;

namespace DiscordBot.Notifications;

/// <summary>What happened at the source.</summary>
public enum ContentEventKind
{
    VideoUploaded,
    ShortUploaded,
    LiveScheduled,
    LiveStarted,
    LiveEnded,
}

/// <summary>
/// A normalized "something new happened" event, produced by an <see cref="INotificationSource"/>
/// and delivered by one or more <see cref="INotificationSink"/>s. Source-agnostic on purpose so
/// YouTube today and Twitch/Telegram tomorrow flow through the exact same pipeline.
/// </summary>
public sealed record ContentEvent
{
    public required ContentSourceType SourceType { get; init; }
    public required ContentEventKind Kind { get; init; }

    /// <summary>Source-native id (videoId, stream id, ...). Used for dedup.</summary>
    public required string ExternalId { get; init; }

    public required string Title { get; init; }
    public required string Url { get; init; }

    public string? AuthorName { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public string? ThumbnailUrl { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? ScheduledStartAt { get; init; }
}
