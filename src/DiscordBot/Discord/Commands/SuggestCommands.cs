using DiscordBot.Data;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>/suggest — any user proposes a feature; it's posted to the configured suggestions channel.</summary>
[SlashRequireGuild]
public sealed class SuggestCommands : ApplicationCommandModule
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SuggestCommands(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    [SlashCommand("suggest", "Предложить идею или функционал для Санчо.")]
    public async Task SuggestAsync(
        InteractionContext ctx,
        [Option("idea", "Твоя идея.")] string idea)
    {
        idea = idea.Trim();
        if (idea.Length is 0 or > 1500)
        {
            await ctx.ReplyAsync("Молви яснее, друг мой — идея должна быть в 1–1500 знаков!", ephemeral: true);
            return;
        }

        ulong? channelId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            channelId = await db.GuildConfigs.AsNoTracking()
                .Where(c => c.GuildId == ctx.Guild!.Id)
                .Select(c => c.SuggestionsChannelId)
                .FirstOrDefaultAsync();
        }

        if (channelId is null)
        {
            await ctx.ReplyAsync(
                "Увы, зал для предложений ещё не назначен — попроси владыку задать его через `/config suggestions`.",
                ephemeral: true);
            return;
        }

        DiscordChannel channel;
        try
        {
            channel = ctx.Guild!.GetChannel(channelId.Value);
        }
        catch
        {
            await ctx.ReplyAsync(
                "Не могу сыскать зал для предложений — быть может, его снесли? Пусть владыка перезадаст `/config suggestions`.",
                ephemeral: true);
            return;
        }

        var embed = new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x5865F2))
            .WithAuthor(ctx.User.Username)
            .WithTitle("💡 Новое предложение для Санчо")
            .WithDescription(idea)
            .WithFooter($"От {ctx.User.Username} • {ctx.User.Id}")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();

        try
        {
            var message = await channel.SendMessageAsync(embed);
            try
            {
                await message.CreateReactionAsync(DiscordEmoji.FromUnicode("✅"));
                await message.CreateReactionAsync(DiscordEmoji.FromUnicode("❌"));
            }
            catch
            {
                // reactions are best-effort (missing Add Reactions permission, etc.)
            }
        }
        catch
        {
            await ctx.ReplyAsync(
                "Не смог доставить идею в зал предложений — проверьте мои права в том канале.",
                ephemeral: true);
            return;
        }

        await ctx.ReplyAsync(
            "Ура! Твоя идея услышана и отправлена на суд — благодарю, доблестный товарищ!",
            ephemeral: true);
    }
}
