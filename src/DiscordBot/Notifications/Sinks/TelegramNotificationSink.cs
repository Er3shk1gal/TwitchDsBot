using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using DiscordBot.Configuration;
using DiscordBot.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Notifications.Sinks;

/// <summary>
/// Mirrors content notifications to a Telegram chat/channel via the Bot API, for subscriptions that
/// additionally carry a <see cref="NotificationSubscription.TelegramChatId"/>. A subscription always
/// keeps its mandatory Discord target; Telegram is an extra delivery target set with
/// <c>/notify telegram</c>.
/// </summary>
public sealed class TelegramNotificationSink : INotificationSink
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramNotificationSink> _logger;

    public TelegramNotificationSink(
        IHttpClientFactory httpClientFactory, IOptions<TelegramOptions> options, ILogger<TelegramNotificationSink> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "telegram";

    public bool AppliesTo(NotificationSubscription subscription) => !string.IsNullOrEmpty(subscription.TelegramChatId);

    public async Task DeliverAsync(NotificationSubscription subscription, ContentEvent contentEvent, CancellationToken ct)
    {
        var chatId = subscription.TelegramChatId;
        if (string.IsNullOrEmpty(chatId))
        {
            return; // AppliesTo already guards this; defensive only.
        }

        var http = _httpClientFactory.CreateClient();
        var payload = new
        {
            chat_id = chatId,
            text = BuildMessage(subscription, contentEvent),
            disable_web_page_preview = false,
        };

        // Let send failures propagate: the dispatcher only marks an event "seen" when every
        // applicable sink succeeds, so a transient Telegram outage is retried next cycle.
        using var response = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{_options.BotToken}/sendMessage", payload, ct);
        response.EnsureSuccessStatusCode();

        if (subscription.PinLiveStreams && contentEvent.Kind == ContentEventKind.LiveStarted)
        {
            await TryPinAsync(http, chatId, response, ct);
        }
    }

    private async Task TryPinAsync(HttpClient http, string chatId, HttpResponseMessage sendResponse, CancellationToken ct)
    {
        // Pinning is best-effort: the message already delivered, so a pin failure must not fail
        // delivery (which would cause a duplicate announcement next cycle).
        try
        {
            var sent = await sendResponse.Content.ReadFromJsonAsync<TelegramSendMessageResponse>(cancellationToken: ct);
            var messageId = sent?.Result?.MessageId;
            if (messageId is null)
            {
                return;
            }

            var pinPayload = new { chat_id = chatId, message_id = messageId.Value, disable_notification = true };
            using var pinResponse = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{_options.BotToken}/pinChatMessage", pinPayload, ct);
            if (!pinResponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("Could not pin Telegram notification in chat {Chat}: {Status}.", chatId, pinResponse.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not pin Telegram notification in chat {Chat}.", chatId);
        }
    }

    private static string BuildMessage(NotificationSubscription subscription, ContentEvent evt)
    {
        var who = evt.AuthorName ?? subscription.SourceDisplayName ?? "Канал, на который вы подписаны";
        var headline = evt.Kind switch
        {
            ContentEventKind.LiveStarted => $"🔴 Ура, друг мой! {who} мчится в прямой эфир сию же секунду — вперёд, навстречу зрелищу!",
            ContentEventKind.LiveScheduled => $"📅 Внемли, доблестный товарищ! {who} возвестил о грядущей трансляции!",
            ContentEventKind.ShortUploaded => $"🎬 Эхехе~ {who} являет доблестный новый Short!",
            _ => $"📺 Не страшись, дорогой Санчо! {who} герольдом возвещает славное новое видео!",
        };

        var sb = new StringBuilder();
        sb.AppendLine(headline);
        sb.AppendLine();
        sb.AppendLine(evt.Title);
        sb.AppendLine(evt.Url);

        if (evt.Kind == ContentEventKind.LiveScheduled && evt.ScheduledStartAt is { } start)
        {
            sb.AppendLine();
            sb.Append("Начало: ").Append(start.ToString("yyyy-MM-dd HH:mm")).Append(" UTC");
        }

        return sb.ToString().TrimEnd();
    }

    private sealed record TelegramSendMessageResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] TelegramMessage? Result);

    private sealed record TelegramMessage(
        [property: JsonPropertyName("message_id")] long MessageId);
}
