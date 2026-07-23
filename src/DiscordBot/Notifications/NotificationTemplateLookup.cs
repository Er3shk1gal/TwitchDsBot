using DiscordBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Notifications;

/// <summary>
/// Looks up a per-subscription custom announcement template set via <c>/notify template</c>. Shared by
/// every sink so the same override text drives Discord and Telegram identically.
/// </summary>
internal static class NotificationTemplateLookup
{
    public static async Task<string?> GetAsync(
        IServiceScopeFactory scopeFactory, int subscriptionId, ContentEventKind kind, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var kindStr = kind.ToString();

        return await db.NotificationTemplates.AsNoTracking()
            .Where(t => t.SubscriptionId == subscriptionId && t.EventKind == kindStr)
            .Select(t => t.Text)
            .FirstOrDefaultAsync(ct);
    }
}
