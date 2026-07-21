using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace DiscordBot.Discord.Commands;

/// <summary>Concise, consistent response helpers over the DSharpPlus v4 InteractionContext.</summary>
public static class InteractionExtensions
{
    public static Task ReplyAsync(this InteractionContext ctx, string content, bool ephemeral = false) =>
        ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
            new DiscordInteractionResponseBuilder().WithContent(content).AsEphemeral(ephemeral));

    public static Task ReplyAsync(this InteractionContext ctx, DiscordEmbed embed, bool ephemeral = false) =>
        ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
            new DiscordInteractionResponseBuilder().AddEmbed(embed).AsEphemeral(ephemeral));

    public static Task DeferAsync(this InteractionContext ctx, bool ephemeral = false) =>
        ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
            new DiscordInteractionResponseBuilder().AsEphemeral(ephemeral));

    public static Task EditAsync(this InteractionContext ctx, string content) =>
        ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent(content));

    public static Task EditAsync(this InteractionContext ctx, DiscordEmbed embed) =>
        ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
}
