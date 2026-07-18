using DSharpPlus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Discord;

/// <summary>
/// Connects the DiscordClient when the host starts and disconnects it on shutdown. Registering this
/// before other hosted services ensures the gateway is up before the notifier begins polling.
/// </summary>
public sealed class DiscordStartupService : IHostedService
{
    private readonly DiscordClient _client;
    private readonly ILogger<DiscordStartupService> _logger;

    public DiscordStartupService(DiscordClient client, ILogger<DiscordStartupService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to Discord…");
        try
        {
            await _client.ConnectAsync();
        }
        catch (DSharpPlus.Exceptions.UnauthorizedException)
        {
            _logger.LogCritical(
                "Discord rejected the bot token (401 Unauthorized). Check Discord:Token / DISCORD__TOKEN.");
            throw;
        }
        _logger.LogInformation("Discord gateway connection established.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while disconnecting from Discord.");
        }
    }
}
