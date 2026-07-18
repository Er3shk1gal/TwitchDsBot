using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

            // Joined the lobby -> create a personal channel and move the user into it.
            if (afterChannelId is { } joined && config?.LobbyChannelId == joined)
            {
                await CreateForAsync(guild, userId, config!);
            }

            // Left a channel -> if it was a temp channel and is now empty, delete it.
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
        catch (DSharpPlus.Exceptions.NotFoundException)
        {
            return;
        }

        // Explicit category if configured; otherwise fall back to the lobby channel's own category
        // (the documented "same as lobby" behaviour) rather than the guild root.
        DiscordChannel? category = null;
        if (config.TempCategoryId is { } categoryId)
        {
            category = await TryGetChannelAsync(guild, categoryId);
        }
        else if (config.LobbyChannelId is { } lobbyId)
        {
            var lobby = await TryGetChannelAsync(guild, lobbyId);
            category = lobby?.Parent;
        }

        var name = config.TempNameTemplate.Replace("{user}", member.DisplayName);
        if (name.Length > 100)
        {
            name = name[..100];
        }

        // Grant the owner management-relevant permissions on their own channel.
        var overwrites = new List<DiscordOverwriteBuilder>
        {
            new DiscordOverwriteBuilder(member).Allow(new DiscordPermissions(
                DiscordPermission.ViewChannel,
                DiscordPermission.Connect,
                DiscordPermission.Speak,
                DiscordPermission.MoveMembers)),
        };

        DiscordChannel channel;
        try
        {
            channel = await guild.CreateVoiceChannelAsync(
                name: name,
                parent: category,
                userLimit: config.DefaultUserLimit > 0 ? config.DefaultUserLimit : null,
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
            await member.ModifyAsync(m => m.VoiceChannel = channel); // move the user into their new channel
        }
        catch (Exception ex)
        {
            // IMPORTANT: do NOT delete the channel here. Some gateway/library hiccups throw even when
            // the move actually succeeded — deleting on that made freshly-created channels vanish a
            // second after being made. The grace-period check below cleans up only if it's truly empty.
            _logger.LogDebug(ex, "Move of {User} into temp channel {Channel} reported an error.", member.Id, channel.Id);
        }

        // Safety net: a short while later, if the channel is still empty (move truly failed, or the
        // user left immediately), remove it. Replaces the old, too-eager delete-on-move-failure.
        ScheduleEmptyCheck(guild, channel.Id, TimeSpan.FromSeconds(30));
    }

    /// <summary>Fire-and-forget: after <paramref name="delay"/>, delete the channel if it is empty.</summary>
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
            return; // not a temp channel we manage
        }

        var channel = await TryGetChannelAsync(guild, channelId);
        if (channel is null)
        {
            // Already gone on Discord's side; just drop the record.
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
                continue; // guild not available this session; leave the record for later
            }

            var channel = await TryGetChannelAsync(guild, record.ChannelId);
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

    private static async Task<DiscordChannel?> TryGetChannelAsync(DiscordGuild guild, ulong channelId)
    {
        try
        {
            return await guild.GetChannelAsync(channelId);
        }
        catch (DSharpPlus.Exceptions.NotFoundException)
        {
            return null;
        }
    }
}
