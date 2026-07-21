using DiscordBot.Data.Entities;
using DiscordBot.Discord.TempVoice;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /voice ... — owner controls for the caller's temporary voice channel. The caller must currently
/// be connected to a temp channel they own (or be a server administrator).
/// </summary>
[SlashCommandGroup("voice", "Повелевай своим временным голосовым чертогом.")]
[SlashRequireGuild]
public sealed class VoiceCommands : ApplicationCommandModule
{
    private readonly TempVoiceManager _tempVoice;

    public VoiceCommands(TempVoiceManager tempVoice) => _tempVoice = tempVoice;

    [SlashCommand("limit", "Задать лимит соратников в чертоге (0 = без предела).")]
    public async Task LimitAsync(
        InteractionContext ctx,
        [Option("limit", "Сколько соратников, 0-99.")] int limit)
    {
        limit = Math.Clamp(limit, 0, 99);
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.ModifyAsync(m => m.Userlimit = limit);
        await ReplyAsync(ctx, $"Ура! Наш чертог примет **{(limit == 0 ? "без счёта" : limit.ToString())}** доблестных товарищей!");
    }

    [SlashCommand("name", "Дать чертогу новое имя.")]
    public async Task NameAsync(
        InteractionContext ctx,
        [Option("name", "Новое имя чертога.")] string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100)
        {
            await ReplyAsync(ctx, "Не страшись, друг мой, но имя чертога должно нести 1–100 знаков!");
            return;
        }

        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.ModifyAsync(m => m.Name = name);
        await ReplyAsync(ctx, $"Славно! Отныне наш чертог наречён **{name}**!");
    }

    [SlashCommand("bitrate", "Задать битрейт чертога в kbps (8-96, выше требует бустов).")]
    public async Task BitrateAsync(
        InteractionContext ctx,
        [Option("kbps", "Битрейт в kbps.")] int kbps)
    {
        kbps = Math.Clamp(kbps, 8, 384);
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.ModifyAsync(m => m.Bitrate = kbps * 1000);
        await ReplyAsync(ctx, $"Вперёд, Росинант! Наши голоса звенят на **{kbps} kbps**! (Уровень бустов королевства может урезать сие, дорогой Санчо.)");
    }

    [SlashCommand("lock", "Запереть вход для всех (кто внутри — остаётся).")]
    public async Task LockAsync(InteractionContext ctx)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(ctx.Guild!.EveryoneRole,
            Permissions.None, Permissions.UseVoice, "temp voice lock");
        await ReplyAsync(ctx, "🔒 Врата заперты! Никто не пройдёт без твоего дозволения, доблестный товарищ!");
    }

    [SlashCommand("unlock", "Вновь открыть вход для всех.")]
    public async Task UnlockAsync(InteractionContext ctx)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(ctx.Guild!.EveryoneRole,
            Permissions.UseVoice, Permissions.None, "temp voice unlock");
        await ReplyAsync(ctx, "🔓 Врата распахнуты! Пусть все товарищи входят — ура!");
    }

    [SlashCommand("permit", "Дозволить войти конкретному соратнику (когда заперто).")]
    public async Task PermitAsync(
        InteractionContext ctx,
        [Option("member", "Кому дозволить вход.")] DiscordMember member)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(member,
            Permissions.UseVoice | Permissions.AccessChannels, Permissions.None,
            "temp voice permit");
        await ReplyAsync(ctx, $"Клянусь честью, {member.Mention} желанный гость в нашем чертоге!");
    }

    [SlashCommand("kick", "Изгнать соратника и закрыть ему путь назад.")]
    public async Task KickAsync(
        InteractionContext ctx,
        [Option("member", "Кого изгнать.")] DiscordMember member)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await owned.Value.Channel.AddOverwriteAsync(member,
            Permissions.None, Permissions.UseVoice, "temp voice kick");

        // If they're currently inside, disconnect them from voice.
        if (member.VoiceState?.Channel?.Id == owned.Value.Channel.Id)
        {
            try { await member.ModifyAsync(m => m.VoiceChannel = null); } // null => disconnect from voice
            catch { /* best effort */ }
        }

        await ReplyAsync(ctx, $"Правосудие восторжествует! {member.Mention} изгнан из нашего чертога, и путь назад ему заказан!");
    }

    [SlashCommand("claim", "Заявить права на чертог, коли владелец покинул его.")]
    public async Task ClaimAsync(InteractionContext ctx)
    {
        var channel = ctx.Member?.VoiceState?.Channel;
        if (channel is null)
        {
            await ReplyAsync(ctx, "Не страшись, но сперва встань в голосовом зале, дабы начать наш квест!");
            return;
        }

        var record = await _tempVoice.GetRecordAsync(channel.Id);
        if (record is null)
        {
            await ReplyAsync(ctx, "Сей чертог не моих рук творение, друг.");
            return;
        }

        if (record.OwnerId == ctx.User.Id)
        {
            await ReplyAsync(ctx, "Эхехе~ сей чертог уж и так твой, благородный владыка!");
            return;
        }

        // Only allow claiming when the owner is no longer connected here.
        if (channel.Users.Any(u => u.Id == record.OwnerId))
        {
            await ReplyAsync(ctx, "Стой, товарищ! Законный владыка сего чертога всё ещё здесь — честь не велит отнимать!");
            return;
        }

        await _tempVoice.SetOwnerAsync(channel.Id, ctx.User.Id);
        await channel.AddOverwriteAsync(ctx.Member!,
            Permissions.AccessChannels | Permissions.UseVoice |
            Permissions.Speak | Permissions.MoveMembers,
            Permissions.None,
            "temp voice claim");
        await ReplyAsync(ctx, "✅ Ура! Сей чертог теперь в твоей власти, доблестный владыка!");
    }

    [SlashCommand("transfer", "Передать чертог во владение другому соратнику.")]
    public async Task TransferAsync(
        InteractionContext ctx,
        [Option("member", "Новый владелец.")] DiscordMember member)
    {
        var owned = await ResolveOwnedAsync(ctx);
        if (owned is null)
        {
            return;
        }

        await _tempVoice.SetOwnerAsync(owned.Value.Channel.Id, member.Id);
        await owned.Value.Channel.AddOverwriteAsync(member,
            Permissions.AccessChannels | Permissions.UseVoice |
            Permissions.Speak | Permissions.MoveMembers,
            Permissions.None,
            "temp voice transfer");
        await ReplyAsync(ctx, $"Что за славное приключение! Владычество над нашим чертогом переходит к {member.Mention}!");
    }

    /// <summary>
    /// Resolves the caller's current voice channel and verifies they own it (or are an admin).
    /// Sends an ephemeral error and returns null when the check fails.
    /// </summary>
    private async Task<(DiscordChannel Channel, TempVoiceChannel Record)?> ResolveOwnedAsync(InteractionContext ctx)
    {
        var channel = ctx.Member?.VoiceState?.Channel;
        if (channel is null)
        {
            await ReplyAsync(ctx, "Не страшись, но сперва встань в голосовом зале, дабы начать наш квест!");
            return null;
        }

        var record = await _tempVoice.GetRecordAsync(channel.Id);
        if (record is null)
        {
            await ReplyAsync(ctx, "Сей чертог не моих рук творение, друг.");
            return null;
        }

        var isAdmin = ctx.Member!.Permissions.HasPermission(Permissions.Administrator);
        if (record.OwnerId != ctx.User.Id && !isAdmin)
        {
            await ReplyAsync(ctx, "Стой, товарищ! Лишь законный владыка сего чертога волен им повелевать — заяви права через `/voice claim`, коли владыка бежал!");
            return null;
        }

        return (channel, record);
    }

    private static async Task ReplyAsync(InteractionContext ctx, string message) =>
        await ctx.ReplyAsync(message, ephemeral: true);
}
