using DSharpPlus;
using DSharpPlus.VoiceNext;

namespace DiscordBot.Discord;

/// <summary>
/// Holds the DiscordClient and VoiceNext extension, which are created after the DI container is
/// built (v4 has no DI-first client builder). Services that need them (music, notifier sink) inject
/// this accessor and read the instances lazily — they are set before the gateway connects, so they
/// are always present by the time any command or poll runs.
/// </summary>
public sealed class BotClientAccessor
{
    public DiscordClient Client { get; set; } = null!;
    public VoiceNextExtension VoiceNext { get; set; } = null!;
}
