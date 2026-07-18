using System.ComponentModel;
using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ContextChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /config ... — server administrator settings for temp voice and music. Admin-only.
/// </summary>
[Command("config")]
[Description("Настроить временные голосовые залы и балладу-музыку.")]
[RequireGuild]
[RequirePermissions(DiscordPermission.Administrator)]
public sealed class ConfigCommands
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ConfigCommands(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    [Command("lobby")]
    [Description("Задать голосовой зал «войди, чтобы создать».")]
    public async ValueTask LobbyAsync(
        SlashCommandContext ctx,
        [Description("Голосовой зал, войдя в который рождают временный зал.")] DiscordChannel channel)
    {
        if (channel.Type != DiscordChannelType.Voice)
        {
            await ctx.RespondAsync("Увы, друг мой, но это должен быть голосовой зал!", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.LobbyChannelId = channel.Id);
        await ctx.RespondAsync($"Ура! Зал сбора назначен — это {channel.Mention}, войди в него, и новый чертог восстанет для нашего квеста!", ephemeral: true);
    }

    [Command("category")]
    [Description("Задать категорию, под которой рождаются временные залы.")]
    public async ValueTask CategoryAsync(
        SlashCommandContext ctx,
        [Description("Канал-категория, где обитают временные залы.")] DiscordChannel category)
    {
        if (category.Type != DiscordChannelType.Category)
        {
            await ctx.RespondAsync("Не страшись, доблестный товарищ, но это должна быть категория!", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.TempCategoryId = category.Id);
        await ctx.RespondAsync($"Славно! Отныне новые чертоги восстанут под сенью **{category.Name}**!", ephemeral: true);
    }

    [Command("userlimit")]
    [Description("Задать предел гостей для новых временных залов (0 = без предела).")]
    public async ValueTask UserLimitAsync(
        SlashCommandContext ctx,
        [Description("Предел гостей по умолчанию, 0-99.")] int limit)
    {
        limit = Math.Clamp(limit, 0, 99);
        await UpdateAsync(ctx.Guild!.Id, c => c.DefaultUserLimit = limit);
        await ctx.RespondAsync($"Ура! Отныне каждый чертог примет **{(limit == 0 ? "без счёта" : limit.ToString())}** доблестных товарищей!", ephemeral: true);
    }

    [Command("nametemplate")]
    [Description("Задать шаблон имени временного зала. {user} — имя владыки.")]
    public async ValueTask NameTemplateAsync(
        SlashCommandContext ctx,
        [Description("Шаблон, напр. \"Чертог {user}\".")] string template)
    {
        template = template.Trim();
        if (template.Length is 0 or > 90)
        {
            await ctx.RespondAsync("Увы, друг мой, но титул должен быть в 1–90 знаков!", ephemeral: true);
            return;
        }
        await UpdateAsync(ctx.Guild!.Id, c => c.TempNameTemplate = template);
        await ctx.RespondAsync($"Славно! Отныне каждый чертог наречётся `{template}`!", ephemeral: true);
    }

    [Command("djrole")]
    [Description("Отдать музыку одной роли (без роли — играть волен каждый).")]
    public async ValueTask DjRoleAsync(
        SlashCommandContext ctx,
        [Description("Роль, нужная для команд музыки-баллады.")] DiscordRole? role = null)
    {
        await UpdateAsync(ctx.Guild!.Id, c => c.DjRoleId = role?.Id);
        await ctx.RespondAsync(role is null
            ? "Ура! Баллады барда отныне открыты каждому товарищу!"
            : $"Да будет так! Отныне лишь носители {role.Mention} вольны призвать барда!", ephemeral: true);
    }

    [Command("show")]
    [Description("Показать нынешние настройки нашего квеста.")]
    public async ValueTask ShowAsync(SlashCommandContext ctx)
    {
        GuildConfig? config;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            config = await db.GuildConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == ctx.Guild!.Id);
        }

        config ??= new GuildConfig { GuildId = ctx.Guild!.Id };

        var embed = new DiscordEmbedBuilder()
            .WithTitle("Эхехе~ Грамота нашего квеста!")
            .AddField("Зал сбора", config.LobbyChannelId is { } l ? $"<#{l}>" : "_зала сбора ещё нет, друг мой_", inline: true)
            .AddField("Категория залов", config.TempCategoryId is { } cat ? $"<#{cat}>" : "_подле зала сбора_", inline: true)
            .AddField("Предел гостей", config.DefaultUserLimit == 0 ? "товарищей без счёта" : config.DefaultUserLimit.ToString(), inline: true)
            .AddField("Шаблон имени", $"`{config.TempNameTemplate}`", inline: false)
            .AddField("Роль барда", config.DjRoleId is { } dj ? $"<@&{dj}>" : "_каждый товарищ_", inline: true)
            .Build();

        await ctx.RespondAsync(embed);
    }

    private async Task UpdateAsync(ulong guildId, Action<GuildConfig> mutate)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var config = await db.GuildConfigs.FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config is null)
        {
            config = new GuildConfig { GuildId = guildId };
            db.GuildConfigs.Add(config);
        }

        mutate(config);
        await db.SaveChangesAsync();
    }
}
