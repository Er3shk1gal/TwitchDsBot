using DiscordBot.Discord.TempVoice;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Discord.Events;

/// <summary>
/// Bridges DSharpPlus voice events to <see cref="TempVoiceManager"/>. Also sweeps stale temp
/// channels once guilds finish downloading after (re)connect. A single class implementing several
/// IEventHandler&lt;T&gt; is registered once via AddEventHandlers&lt;VoiceStateHandler&gt;().
/// </summary>
public sealed class VoiceStateHandler
    : IEventHandler<VoiceStateUpdatedEventArgs>, IEventHandler<GuildDownloadCompletedEventArgs>
{
    private readonly TempVoiceManager _tempVoice;
    private readonly ILogger<VoiceStateHandler> _logger;

    public VoiceStateHandler(TempVoiceManager tempVoice, ILogger<VoiceStateHandler> logger)
    {
        _tempVoice = tempVoice;
        _logger = logger;
    }

    public async Task HandleEventAsync(DiscordClient sender, VoiceStateUpdatedEventArgs eventArgs)
    {
        var guild = await eventArgs.GetGuildAsync();
        if (guild is null)
        {
            return;
        }

        try
        {
            await _tempVoice.OnVoiceStateChangedAsync(
                guild,
                eventArgs.UserId,
                eventArgs.Before?.ChannelId,
                eventArgs.After?.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temp voice handling failed for user {User} in guild {Guild}.",
                eventArgs.UserId, guild.Id);
        }
    }

    public async Task HandleEventAsync(DiscordClient sender, GuildDownloadCompletedEventArgs eventArgs)
    {
        try
        {
            await _tempVoice.SweepAsync(eventArgs.Guilds.Values);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temp voice startup sweep failed.");
        }
    }
}
