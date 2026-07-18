using DiscordBot.Data.Entities;

namespace DiscordBot.Notifications;

/// <summary>
/// Delivers a <see cref="ContentEvent"/> somewhere. Discord today; add Telegram tomorrow by
/// implementing this and registering it in DI. The dispatcher fans each event out to every sink
/// that <see cref="AppliesTo"/> the subscription.
/// </summary>
public interface INotificationSink
{
    /// <summary>Stable identifier for logging (e.g. "discord", "telegram").</summary>
    string Name { get; }

    /// <summary>Whether this sink should handle the given subscription.</summary>
    bool AppliesTo(NotificationSubscription subscription);

    Task DeliverAsync(NotificationSubscription subscription, ContentEvent contentEvent, CancellationToken ct);
}
