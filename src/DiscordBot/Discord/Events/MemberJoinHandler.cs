using DiscordBot.Data;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Discord.Events;

/// <summary>Grants the configured auto-role to members when they join. Needs the GuildMembers intent.</summary>
public sealed class MemberJoinHandler : IEventHandler<GuildMemberAddedEventArgs>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MemberJoinHandler> _logger;

    public MemberJoinHandler(IServiceScopeFactory scopeFactory, ILogger<MemberJoinHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task HandleEventAsync(DiscordClient sender, GuildMemberAddedEventArgs eventArgs)
    {
        var member = eventArgs.Member;
        if (member.IsBot)
        {
            return;
        }

        var guild = eventArgs.Guild;

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

        var role = guild.Roles.GetValueOrDefault(roleId.Value);
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
