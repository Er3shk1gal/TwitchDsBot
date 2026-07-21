using System.ComponentModel;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /help — Sancho introduces herself as an interaction reply.
/// /about — the same, but posted as a standalone channel message everyone sees.
/// </summary>
public sealed class HelpCommands
{
    [Command("help")]
    [Description("Санчо расскажет о себе и своих умениях.")]
    public async ValueTask HelpAsync(SlashCommandContext ctx)
    {
        await ctx.RespondAsync(BuildHelpEmbed());
    }

    [Command("about")]
    [Description("Санчо расскажет о себе отдельным сообщением — чтобы видели все.")]
    public async ValueTask AboutAsync(SlashCommandContext ctx)
    {
        // Post as a normal channel message (not tied to the command reply) so everyone sees it,
        // then quietly acknowledge the interaction to the caller only.
        await ctx.RespondAsync("📜 Разворачиваю свиток для всех!", ephemeral: true);
        await ctx.Channel.SendMessageAsync(BuildHelpEmbed());
    }

    private static DiscordEmbed BuildHelpEmbed() =>
        new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0xFFD700))
            .WithTitle("⚔️ Эхехе~ Я — Санчо, странствующий рыцарь!")
            .WithDescription(
                "Не страшись, друг мой! Я странствую по вашим залам, возвожу чертоги для доблестных " +
                "товарищей и несу правосудие. Вот что я умею — призывай смело!")
            .AddField("🔊 Твой временный зал",
                "Войди в зал сбора — и восстанет твой чертог. Правь им:\n" +
                "`/voice limit <n>` — предел гостей\n" +
                "`/voice name <имя>` — переименовать\n" +
                "`/voice bitrate <kbps>` — качество голоса\n" +
                "`/voice lock` · `/voice unlock` — запереть / отпереть врата\n" +
                "`/voice permit <@кто>` — впустить гостя\n" +
                "`/voice kick <@кто>` — изгнать\n" +
                "`/voice claim` — забрать зал, коли владыка бежал\n" +
                "`/voice transfer <@кому>` — передать зал")
            .AddField("📻 Радио",
                "`/radio play <поток>` — включить радио (выбор из добавленных)\n" +
                "`/radio list` — список потоков\n" +
                "`/radio stop` — выключить и уйти")
            .AddField("💡 Прочее",
                "`/suggest <идея>` — предложить функционал для Санчо\n" +
                "`/about` — рассказать о себе для всех\n" +
                "`/help` — сей свиток")
            .WithFooter("Вперёд, Росинант! Правосудие восторжествует!")
            .Build();
}
