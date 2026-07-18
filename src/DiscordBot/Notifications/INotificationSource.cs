using DiscordBot.Data.Entities;

namespace DiscordBot.Notifications;

/// <summary>
/// Produces recent <see cref="ContentEvent"/>s for a subscription. Implementations only need to
/// return recent candidates — the dispatcher handles dedup (so sources stay stateless).
/// Add a new platform by implementing this and registering it in DI; the dispatcher picks it up
/// by <see cref="SourceType"/> automatically.
/// </summary>
public interface INotificationSource
{
    ContentSourceType SourceType { get; }

    /// <summary>
    /// Resolve a user-supplied identifier (URL, @handle, id, login) into a canonical source id and
    /// a display name, or null if it can't be found. Called when a subscription is created.
    /// </summary>
    Task<(string SourceChannelId, string DisplayName)?> ResolveAsync(string userInput, CancellationToken ct);

    /// <summary>Return recent candidate events for a subscription (newest-first not required).</summary>
    IAsyncEnumerable<ContentEvent> PollAsync(NotificationSubscription subscription, CancellationToken ct);
}
