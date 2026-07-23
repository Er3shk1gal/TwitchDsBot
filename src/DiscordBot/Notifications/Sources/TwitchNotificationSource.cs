using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DiscordBot.Configuration;
using DiscordBot.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Notifications.Sources;

/// <summary>
/// Polls the Twitch Helix API for a channel going live. Authenticates with an app access token
/// (client-credentials grant) — no user login is needed, only a Client ID + Secret from
/// <see href="https://dev.twitch.tv/console/apps"/>.
/// </summary>
public sealed class TwitchNotificationSource : INotificationSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitchOptions _options;
    private readonly ILogger<TwitchNotificationSource> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public TwitchNotificationSource(
        IHttpClientFactory httpClientFactory, IOptions<TwitchOptions> options, ILogger<TwitchNotificationSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public ContentSourceType SourceType => ContentSourceType.Twitch;

    public async Task<(string SourceChannelId, string DisplayName)?> ResolveAsync(string userInput, CancellationToken ct)
    {
        var login = NormalizeLogin(userInput);
        if (login.Length == 0)
        {
            return null;
        }

        try
        {
            using var resp = await SendAsync($"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<TwitchUsersResponse>(cancellationToken: ct);
            var user = body?.Data?.FirstOrDefault();
            return user is null ? null : (user.Id, user.DisplayName ?? user.Login ?? login);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twitch channel resolve failed for '{Input}'.", userInput);
            return null;
        }
    }

    public async IAsyncEnumerable<ContentEvent> PollAsync(
        NotificationSubscription subscription, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var evt in await FetchAsync(subscription, ct))
        {
            yield return evt;
        }
    }

    private async Task<IReadOnlyList<ContentEvent>> FetchAsync(NotificationSubscription subscription, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(
                $"https://api.twitch.tv/helix/streams?user_id={Uri.EscapeDataString(subscription.SourceChannelId)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await resp.Content.ReadFromJsonAsync<TwitchStreamsResponse>(cancellationToken: ct);
            var stream = body?.Data?.FirstOrDefault();
            if (stream is null)
            {
                // Not live. No LiveEnded is emitted — the dispatcher only needs "new" events, and the
                // next time they go live gets a fresh stream id (see ExternalId below), which is all
                // that's needed to re-notify correctly.
                return [];
            }

            var thumb = stream.ThumbnailUrl?.Replace("{width}", "1280").Replace("{height}", "720");

            return
            [
                new ContentEvent
                {
                    SourceType = ContentSourceType.Twitch,
                    Kind = ContentEventKind.LiveStarted,
                    // Twitch mints a new stream id per broadcast session, so going offline and live
                    // again naturally dedups as a new event instead of being suppressed as "seen".
                    ExternalId = stream.Id ?? $"{stream.UserId}@{stream.StartedAt:O}",
                    Title = stream.Title ?? "(без названия)",
                    Url = $"https://twitch.tv/{stream.UserLogin ?? subscription.SourceChannelId}",
                    AuthorName = stream.UserName,
                    ThumbnailUrl = thumb,
                    PublishedAt = stream.StartedAt,
                },
            ];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twitch poll failed for channel {Channel}.", subscription.SourceChannelId);
            return [];
        }
    }

    /// <summary>Sends an authorized Helix request, retrying once with a fresh token on 401.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct, bool isRetry = false)
    {
        var token = await GetAppTokenAsync(forceRefresh: isRetry, ct);
        if (token is null)
        {
            throw new InvalidOperationException("Could not obtain a Twitch app access token.");
        }

        var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Client-Id", _options.ClientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !isRetry)
        {
            response.Dispose();
            return await SendAsync(url, ct, isRetry: true);
        }

        return response;
    }

    private async Task<string?> GetAppTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(5))
        {
            return _token;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(5))
            {
                return _token;
            }

            var http = _httpClientFactory.CreateClient();
            var url = "https://id.twitch.tv/oauth2/token" +
                      $"?client_id={Uri.EscapeDataString(_options.ClientId)}" +
                      $"&client_secret={Uri.EscapeDataString(_options.ClientSecret)}" +
                      "&grant_type=client_credentials";

            using var response = await http.PostAsync(url, content: null, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Twitch app token request failed with {Status}.", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>(cancellationToken: ct);
            if (string.IsNullOrEmpty(body?.AccessToken))
            {
                return null;
            }

            _token = body.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string NormalizeLogin(string input)
    {
        input = input.Trim();

        var idx = input.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            input = input[(idx + "twitch.tv/".Length)..];
            var end = input.IndexOfAny(['/', '?', '&']);
            if (end >= 0)
            {
                input = input[..end];
            }
        }

        return input.TrimStart('@').Trim().ToLowerInvariant();
    }

    private sealed record TwitchTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record TwitchUsersResponse(
        [property: JsonPropertyName("data")] List<TwitchUser>? Data);

    private sealed record TwitchUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    private sealed record TwitchStreamsResponse(
        [property: JsonPropertyName("data")] List<TwitchStream>? Data);

    private sealed record TwitchStream(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("user_login")] string? UserLogin,
        [property: JsonPropertyName("user_name")] string? UserName,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl,
        [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt);
}
