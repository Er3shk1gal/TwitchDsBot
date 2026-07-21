using DiscordBot.Data;
using DiscordBot.Discord.TempVoice;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Discord.Events;

/// <summary>DSharpPlus v4 gateway event handlers, subscribed with += in Program.cs.</summary>
public sealed class DiscordEventHandlers
{
    private readonly TempVoiceManager _tempVoice;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordEventHandlers> _logger;

    public DiscordEventHandlers(
        TempVoiceManager tempVoice, IServiceScopeFactory scopeFactory, ILogger<DiscordEventHandlers> logger)
    {
        _tempVoice = tempVoice;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task OnVoiceStateUpdated(DiscordClient client, VoiceStateUpdateEventArgs e)
    {
        if (e.Guild is null)
        {
            return;
        }

        try
        {
            await _tempVoice.OnVoiceStateChangedAsync(
                e.Guild, e.User.Id, e.Before?.Channel?.Id, e.After?.Channel?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temp voice handling failed for user {User} in guild {Guild}.", e.User.Id, e.Guild.Id);
        }
    }

    public async Task OnGuildDownloadCompleted(DiscordClient client, GuildDownloadCompletedEventArgs e)
    {
        try
        {
            await _tempVoice.SweepAsync(e.Guilds.Values);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temp voice startup sweep failed.");
        }
    }

    public async Task OnGuildMemberAdded(DiscordClient client, GuildMemberAddEventArgs e)
    {
        var member = e.Member;
        if (member.IsBot)
        {
            return;
        }

        var guild = e.Guild;

        ulong? roleId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            roleId = await db.GuildConfigs.AsNoTracking()
                .Where(c => c.GuildId == guild.Id)
                .Select(c => c.AutoRoleId)
                .FirstOrDefaultAsync();
        }

        if (roleId is null)
        {
            return;
        }

        var role = guild.GetRole(roleId.Value);
        if (role is null)
        {
            return;
        }

        try
        {
            await member.GrantRoleAsync(role, "Auto-role on join");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to grant auto-role {Role} to {Member} in {Guild} (check Manage Roles + role hierarchy).",
                roleId, member.Id, guild.Id);
        }
    }
}
