using System.ComponentModel;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;

namespace DiscordBot.Discord.Commands;

/// <summary>/help — Sancho introduces herself and lists the non-admin commands, in-character.</summary>
public sealed class HelpCommands
{
    [Command("help")]
    [Description("Санчо расскажет о себе и своих умениях.")]
    public async ValueTask HelpAsync(SlashCommandContext ctx)
    {
        var embed = new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0xFFD700))
            .WithTitle("⚔️ Эхехе~ Я — Санчо, странствующий бард-рыцарь!")
            .WithDescription(
                "Не страшись, друг мой! Я странствую по вашим залам, несу баллады и правосудие " +
                "и возвожу чертоги для доблестных товарищей. Вот что я умею — призывай смело!")
            .AddField("🎵 Баллады (музыка)",
                "`/play <название или ссылка>` — заказать балладу\n" +
                "`/skip` — следующая\n" +
                "`/stop` — оборвать и очистить список\n" +
                "`/pause` · `/resume` — пауза / продолжить\n" +
                "`/queue` — список баллад\n" +
                "`/nowplaying` — что звучит сейчас\n" +
                "`/volume 0-200` — громкость\n" +
                "`/seek 1:30` — перемотка\n" +
                "`/loop` — повтор баллады\n" +
                "`/shuffle` — перемешать список\n" +
                "`/leave` — покинуть зал")
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
            .AddField("ℹ️ Прочее", "`/help` — сей свиток")
            .WithFooter("Вперёд, Росинант! Правосудие восторжествует!")
            .Build();

        await ctx.RespondAsync(embed);
    }
}
