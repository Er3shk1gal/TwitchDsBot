using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using DiscordBot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Twitch;

/// <summary>
/// Connects to Twitch chat over IRC-over-WebSocket as a bot account and replies with a canned
/// message (e.g. social links) when someone types the configured command. Hand-rolled against the
/// plain-text IRC protocol Twitch documents — no client library needed for "join a channel, watch
/// for one command, reply". Only started (see Program.cs) when <see cref="TwitchChatOptions"/> has a
/// token, username and at least one channel configured.
/// </summary>
public sealed class TwitchChatBot : BackgroundService
{
    private const string WebSocketUri = "wss://irc-ws.chat.twitch.tv:443";

    private readonly TwitchChatOptions _options;
    private readonly ILogger<TwitchChatBot> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTriggerAt = new(StringComparer.OrdinalIgnoreCase);

    public TwitchChatBot(IOptions<TwitchChatOptions> options, ILogger<TwitchChatBot> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var command = _options.Command.Trim();
        var channels = _options.Channels
            .Select(c => c.Trim().TrimStart('#').ToLowerInvariant())
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

        _logger.LogInformation(
            "Twitch chat bot starting. Channels: [{Channels}], command: {Command}.",
            string.Join(", ", channels), command);

        var backoff = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            var authFailed = false;
            try
            {
                authFailed = await RunSessionAsync(channels, command, stoppingToken);
                backoff = TimeSpan.FromSeconds(5); // a session that ran (even briefly) resets backoff
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Twitch chat session failed; reconnecting in {Delay}.", backoff);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Bad credentials won't fix themselves on retry — back off hard so we don't hammer
            // Twitch with a token that will keep failing.
            var delay = authFailed ? TimeSpan.FromMinutes(5) : backoff;
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    /// <summary>Runs one connection until it closes or the command line tells us to reconnect. Returns true if the session ended because of an auth failure.</summary>
    private async Task<bool> RunSessionAsync(List<string> channels, string command, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(WebSocketUri), ct);
        _logger.LogInformation("Connected to Twitch IRC.");

        var token = _options.OAuthToken.Trim();
        if (!token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
        {
            token = "oauth:" + token;
        }

        await SendLineAsync(socket, $"PASS {token}", ct);
        await SendLineAsync(socket, $"NICK {_options.BotUsername.Trim().ToLowerInvariant()}", ct);
        await SendLineAsync(socket, "CAP REQ :twitch.tv/commands", ct);
        foreach (var channel in channels)
        {
            await SendLineAsync(socket, $"JOIN #{channel}", ct);
        }

        var buffer = new byte[8192];
        var pending = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var messageBytes = new List<byte>();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Twitch IRC closed the connection ({Status}).", result.CloseStatus);
                    return false;
                }
                messageBytes.AddRange(buffer.AsSpan(0, result.Count).ToArray());
            }
            while (!result.EndOfMessage);

            pending.Append(Encoding.UTF8.GetString(messageBytes.ToArray()));

            // IRC lines are \r\n-terminated; a WebSocket frame can split mid-line, so only process
            // complete lines and keep any trailing partial line buffered for the next frame.
            var text = pending.ToString();
            var lines = text.Split("\r\n");
            pending.Clear();
            pending.Append(lines[^1]);

            for (var i = 0; i < lines.Length - 1; i++)
            {
                var authFailed = await HandleLineAsync(socket, lines[i], channels, command, ct);
                if (authFailed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns true if this line indicates a fatal auth failure (caller should back off hard).</summary>
    private async Task<bool> HandleLineAsync(
        ClientWebSocket socket, string line, List<string> channels, string command, CancellationToken ct)
    {
        if (line.Length == 0)
        {
            return false;
        }

        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            await SendLineAsync(socket, "PONG" + line["PING".Length..], ct);
            return false;
        }

        if (line.Contains("NOTICE", StringComparison.Ordinal)
            && line.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Twitch chat login failed — check TwitchChat:OAuthToken (needs chat:read + chat:edit scopes) and BotUsername.");
            return true;
        }

        if (line.StartsWith("RECONNECT", StringComparison.Ordinal))
        {
            _logger.LogInformation("Twitch IRC requested a reconnect.");
            return false; // outer loop reconnects; the socket will close right after this notice.
        }

        var privmsgMarker = line.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
        if (privmsgMarker < 0)
        {
            return false;
        }

        var afterMarker = line[(privmsgMarker + " PRIVMSG #".Length)..];
        var spaceIdx = afterMarker.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return false;
        }

        var channel = afterMarker[..spaceIdx];
        var rest = afterMarker[(spaceIdx + 1)..];
        var message = rest.StartsWith(':') ? rest[1..] : rest;

        if (!string.Equals(message.Trim(), command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var last = _lastTriggerAt.GetOrAdd(channel, DateTimeOffset.MinValue);
        if (now - last < TimeSpan.FromSeconds(_options.CooldownSeconds))
        {
            return false;
        }
        _lastTriggerAt[channel] = now;

        await SendLineAsync(socket, $"PRIVMSG #{channel} :{_options.Message}", ct);
        return false;
    }

    private static async Task SendLineAsync(ClientWebSocket socket, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
