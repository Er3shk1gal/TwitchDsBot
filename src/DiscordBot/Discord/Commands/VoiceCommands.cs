using System.ComponentModel;
using DiscordBot.Data.Entities;
using DiscordBot.Discord.TempVoice;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ContextChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /voice ... — owner controls for the caller's temporary voice channel. The caller must currently
/// be connected to a temp channel they own (or be a server administrator).
/// </summary>
[Command("voice")]
[Description("Manage your temporary voice channel.")]
[RequireGuild]
public sealed class VoiceCommands
{
    private readonly TempVoiceManager _tempVoice;

    public VoiceCommands(TempVoiceManager tempVoice) => _tempVoice = tempVoice;

    [Command("limit")]
    [Description("Set the user limit for your channel (0 = unlimited).")]
    public async ValueTask LimitAsync(
        SlashCommandContext ctx,
        [Description("Max users, 0-99.")] int limit)
    {
        limit = Math.Clamp(limit, 0, 99);
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.ModifyAsync(m => m.Userlimit = limit);
        await ReplyAsync(ctx, $"User limit set to **{(limit == 0 ? "unlimited" : limit.ToString())}**.");
    }

    [Command("name")]
    [Description("Rename your channel.")]
    public async ValueTask NameAsync(
        SlashCommandContext ctx,
        [Description("New channel name.")] string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100)
        {
            await ReplyAsync(ctx, "Name must be 1–100 characters.");
            return;
        }

        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.ModifyAsync(m => m.Name = name);
        await ReplyAsync(ctx, $"Renamed to **{name}**.");
    }

    [Command("bitrate")]
    [Description("Set the channel bitrate in kbps (8-96, higher needs server boosts).")]
    public async ValueTask BitrateAsync(
        SlashCommandContext ctx,
        [Description("Bitrate in kbps.")] int kbps)
    {
        kbps = Math.Clamp(kbps, 8, 384);
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.ModifyAsync(m => m.Bitrate = kbps * 1000);
        await ReplyAsync(ctx, $"Bitrate set to **{kbps} kbps**. (Server boost level may cap this.)");
    }

    [Command("lock")]
    [Description("Prevent everyone from joining (existing members stay).")]
    public async ValueTask LockAsync(SlashCommandContext ctx)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(ctx.Guild!.EveryoneRole,
            deny: DiscordPermission.Connect, reason: "temp voice lock");
        await ReplyAsync(ctx, "🔒 Channel locked.");
    }

    [Command("unlock")]
    [Description("Allow everyone to join again.")]
    public async ValueTask UnlockAsync(SlashCommandContext ctx)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(ctx.Guild!.EveryoneRole,
            allow: DiscordPermission.Connect, reason: "temp voice unlock");
        await ReplyAsync(ctx, "🔓 Channel unlocked.");
    }

    [Command("permit")]
    [Description("Allow a specific user to join (useful when locked).")]
    public async ValueTask PermitAsync(
        SlashCommandContext ctx,
        [Description("User to allow.")] DiscordMember member)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(member,
            allow: new DiscordPermissions(DiscordPermission.Connect, DiscordPermission.ViewChannel),
            reason: "temp voice permit");
        await ReplyAsync(ctx, $"Allowed {member.Mention} to join.");
    }

    [Command("kick")]
    [Description("Disconnect a user and block them from rejoining.")]
    public async ValueTask KickAsync(
        SlashCommandContext ctx,
        [Description("User to remove.")] DiscordMember member)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(member,
            deny: DiscordPermission.Connect, reason: "temp voice kick");

        // If they're currently inside, disconnect them from voice.
        if (member.VoiceState?.ChannelId == owned.Value.Channel.Id)
        {
            try { await member.ModifyAsync(m => m.VoiceChannel = null!); } // null => disconnect from voice
            catch { /* best effort */ }
        }

        await ReplyAsync(ctx, $"Removed {member.Mention} and blocked them from rejoining.");
    }

    [Command("claim")]
    [Description("Claim ownership if the current owner has left the channel.")]
    public async ValueTask ClaimAsync(SlashCommandContext ctx)
    {
        var channel = await GetVoiceChannelAsync(ctx.Member);
        if (channel is null)
        {
            await ReplyAsync(ctx, "You must be in a voice channel to claim it.");
            return;
        }

        var record = await _tempVoice.GetRecordAsync(channel.Id);
        if (record is null)
        {
            await ReplyAsync(ctx, "This isn't a temporary channel.");
            return;
        }

        if (record.OwnerId == ctx.User.Id)
        {
            await ReplyAsync(ctx, "You already own this channel.");
            return;
        }

        // Only allow claiming when the owner is no longer connected here.
        if (channel.Users.Any(u => u.Id == record.OwnerId))
        {
            await ReplyAsync(ctx, "The owner is still here — you can't claim it.");
            return;
        }

        await _tempVoice.SetOwnerAsync(channel.Id, ctx.User.Id);
        await channel.AddOverwriteAsync(ctx.Member!,
            allow: new DiscordPermissions(
                DiscordPermission.ViewChannel, DiscordPermission.Connect,
                DiscordPermission.Speak, DiscordPermission.MoveMembers),
            reason: "temp voice claim");
        await ReplyAsync(ctx, "✅ You now own this channel.");
    }

    [Command("transfer")]
    [Description("Transfer ownership of your channel to another member.")]
    public async ValueTask TransferAsync(
        SlashCommandContext ctx,
        [Description("New owner.")] DiscordMember member)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await _tempVoice.SetOwnerAsync(owned.Value.Channel.Id, member.Id);
        await owned.Value.Channel.AddOverwriteAsync(member,
            allow: new DiscordPermissions(
                DiscordPermission.ViewChannel, DiscordPermission.Connect,
                DiscordPermission.Speak, DiscordPermission.MoveMembers),
            reason: "temp voice transfer");
        await ReplyAsync(ctx, $"Ownership transferred to {member.Mention}.");
    }

    /// <summary>
    /// Resolves the caller's current voice channel and verifies they own it (or are an admin).
    /// Sends an ephemeral error and returns null when the check fails.
    /// </summary>
    private async Task<(DiscordChannel Channel, TempVoiceChannel Record)?> ResolveOwnedAsync(SlashCommandContext ctx)
    {
        var channel = await GetVoiceChannelAsync(ctx.Member);
        if (channel is null)
        {
            await ReplyAsync(ctx, "You need to be in your voice channel to do that.");
            return null;
        }

        var record = await _tempVoice.GetRecordAsync(channel.Id);
        if (record is null)
        {
            await ReplyAsync(ctx, "This isn't a temporary channel managed by me.");
            return null;
        }

        var isAdmin = ctx.Member!.Permissions.HasPermission(DiscordPermission.Administrator);
        if (record.OwnerId != ctx.User.Id && !isAdmin)
        {
            await ReplyAsync(ctx, "Only the channel owner can do that. Use `/voice claim` if the owner left.");
            return null;
        }

        return (channel, record);
    }

    private static async Task<DiscordChannel?> GetVoiceChannelAsync(DiscordMember? member)
    {
        var state = member?.VoiceState;
        if (state?.ChannelId is null)
        {
            return null;
        }
        return await state.GetChannelAsync();
    }

    private static async Task ReplyAsync(SlashCommandContext ctx, string message) =>
        await ctx.RespondAsync(message, ephemeral: true);
}
