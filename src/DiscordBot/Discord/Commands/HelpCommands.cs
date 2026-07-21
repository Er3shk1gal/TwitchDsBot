using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /help — Sancho introduces herself as an interaction reply.
/// /about — the same, but posted as a standalone channel message everyone sees.
/// </summary>
public sealed class HelpCommands : ApplicationCommandModule
{
    [SlashCommand("help", "Санчо расскажет о себе и своих умениях.")]
    public async Task HelpAsync(InteractionContext ctx)
    {
        await ctx.ReplyAsync(BuildHelpEmbed());
    }

    [SlashCommand("about", "Санчо расскажет о себе отдельным сообщением — чтобы видели все.")]
    public async Task AboutAsync(InteractionContext ctx)
    {
        // Post as a normal channel message (not tied to the command reply) so everyone sees it,
        // then quietly acknowledge the interaction to the caller only.
        await ctx.ReplyAsync("📜 Разворачиваю свиток для всех!", ephemeral: true);
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
            .AddField("🎵 Баллады",
                "`/play <ссылка или поиск>` — сыграть балладу (SoundCloud и иные вольные источники)\n" +
                "`/skip` · `/stop` · `/pause` · `/resume` — правь потоком\n" +
                "`/queue` · `/nowplaying` — список и что звучит ныне\n" +
                "`/volume <0-200>` · `/seek <m:ss>` · `/loop` · `/shuffle`\n" +
                "`/leave` — покинуть зал")
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
