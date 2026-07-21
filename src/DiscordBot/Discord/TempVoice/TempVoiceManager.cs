using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Permissions = DSharpPlus.Permissions;

namespace DiscordBot.Discord.TempVoice;

/// <summary>
/// Core logic for "join to create" temporary voice channels: creating a personal channel when a
/// user joins the configured lobby, tracking ownership, and cleaning up empty channels. Shared by
/// the voice-state event handler and the /voice owner commands.
/// </summary>
public sealed class TempVoiceManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TempVoiceManager> _logger;

    // Serializes create/cleanup per guild so rapid join/leave bursts don't double-create or race deletes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, SemaphoreSlim> _guildLocks = new();

    public TempVoiceManager(IServiceScopeFactory scopeFactory, ILogger<TempVoiceManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>React to a voice-state change: spawn a temp channel on lobby-join, clean up on leave.</summary>
    public async Task OnVoiceStateChangedAsync(DiscordGuild guild, ulong userId, ulong? beforeChannelId, ulong? afterChannelId)
    {
        var gate = _guildLocks.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var config = await GetConfigAsync(guild.Id);

            if (afterChannelId is { } joined && config?.LobbyChannelId == joined)
            {
                await CreateForAsync(guild, userId, config!);
            }

            if (beforeChannelId is { } left && left != afterChannelId)
            {
                await CleanupIfEmptyAsync(guild, left);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CreateForAsync(DiscordGuild guild, ulong userId, GuildConfig config)
    {
        DiscordMember member;
        try
        {
            member = await guild.GetMemberAsync(userId);
        }
        catch (NotFoundException)
        {
            return;
        }

        DiscordChannel? category = null;
        if (config.TempCategoryId is { } categoryId)
        {
            category = guild.GetChannel(categoryId);
        }
        else if (config.LobbyChannelId is { } lobbyId)
        {
            category = guild.GetChannel(lobbyId)?.Parent;
        }

        var name = config.TempNameTemplate.Replace("{user}", member.DisplayName);
        if (name.Length > 100)
        {
            name = name[..100];
        }

        var overwrites = new List<DiscordOverwriteBuilder>
        {
            new DiscordOverwriteBuilder(member).Allow(
                Permissions.AccessChannels | Permissions.UseVoice | Permissions.Speak | Permissions.MoveMembers),
        };

        DiscordChannel channel;
        try
        {
            channel = await guild.CreateVoiceChannelAsync(
                name: name,
                parent: category,
                user_limit: config.DefaultUserLimit > 0 ? config.DefaultUserLimit : null,
                overwrites: overwrites,
                reason: $"Temp voice for {member.Username}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create temp voice channel in guild {Guild}.", guild.Id);
            return;
        }

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.TempVoiceChannels.Add(new TempVoiceChannel
            {
                ChannelId = channel.Id,
                GuildId = guild.Id,
                OwnerId = member.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await member.ModifyAsync(m => m.VoiceChannel = channel);
        }
        catch (Exception ex)
        {
            // Don't delete on move failure — grace-period check below handles a genuinely empty channel.
            _logger.LogDebug(ex, "Move of {User} into temp channel {Channel} reported an error.", member.Id, channel.Id);
        }

        ScheduleEmptyCheck(guild, channel.Id, TimeSpan.FromSeconds(30));
    }

    private void ScheduleEmptyCheck(DiscordGuild guild, ulong channelId, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                var gate = _guildLocks.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                try
                {
                    await CleanupIfEmptyAsync(guild, channelId);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Grace-period cleanup failed for temp channel {Channel}.", channelId);
            }
        });
    }

    private async Task CleanupIfEmptyAsync(DiscordGuild guild, ulong channelId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var record = await db.TempVoiceChannels.FirstOrDefaultAsync(x => x.ChannelId == channelId);
        if (record is null)
        {
            return;
        }

        var channel = guild.GetChannel(channelId);
        if (channel is null)
        {
            db.TempVoiceChannels.Remove(record);
            await db.SaveChangesAsync();
            return;
        }

        if (channel.Users.Count == 0)
        {
            try
            {
                await channel.DeleteAsync("Temp voice empty");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete empty temp channel {Channel}.", channelId);
            }

            db.TempVoiceChannels.Remove(record);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>On startup, delete any tracked temp channels that are gone or empty (crash recovery).</summary>
    public async Task SweepAsync(IEnumerable<DiscordGuild> guilds)
    {
        var byGuild = guilds.ToDictionary(g => g.Id);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var records = await db.TempVoiceChannels.ToListAsync();
        foreach (var record in records)
        {
            if (!byGuild.TryGetValue(record.GuildId, out var guild))
            {
                continue;
            }

            var channel = guild.GetChannel(record.ChannelId);
            if (channel is null)
            {
                db.TempVoiceChannels.Remove(record);
            }
            else if (channel.Users.Count == 0)
            {
                try { await channel.DeleteAsync("Temp voice cleanup on startup"); }
                catch (Exception ex) { _logger.LogDebug(ex, "Startup sweep delete failed for {Channel}.", record.ChannelId); }
                db.TempVoiceChannels.Remove(record);
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Returns the tracked temp-channel record for a channel, or null if it isn't one.</summary>
    public async Task<TempVoiceChannel?> GetRecordAsync(ulong channelId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.TempVoiceChannels.AsNoTracking().FirstOrDefaultAsync(x => x.ChannelId == channelId);
    }

    /// <summary>Reassigns ownership of a temp channel (used by /voice claim and /voice transfer).</summary>
    public async Task SetOwnerAsync(ulong channelId, ulong newOwnerId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var record = await db.TempVoiceChannels.FirstOrDefaultAsync(x => x.ChannelId == channelId);
        if (record is not null)
        {
            record.OwnerId = newOwnerId;
            await db.SaveChangesAsync();
        }
    }

    private async Task<GuildConfig?> GetConfigAsync(ulong guildId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.GuildConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.GuildId == guildId);
    }
}
