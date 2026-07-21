using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>
/// /config ... — server administrator settings for temp voice and music. Admin-only.
/// </summary>
[SlashCommandGroup("config", "Настроить временные голосовые залы и балладу-музыку.")]
[SlashRequireGuild]
[SlashRequirePermissions(Permissions.Administrator)]
public sealed class ConfigCommands : ApplicationCommandModule
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ConfigCommands(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    [SlashCommand("lobby", "Задать голосовой зал «войди, чтобы создать».")]
    public async Task LobbyAsync(
        InteractionContext ctx,
        [Option("channel", "Голосовой зал, войдя в который рождают временный зал.")] DiscordChannel channel)
    {
        if (channel.Type != ChannelType.Voice)
        {
            await ctx.ReplyAsync("Увы, друг мой, но это должен быть голосовой зал!", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.LobbyChannelId = channel.Id);
        await ctx.ReplyAsync($"Ура! Зал сбора назначен — это {channel.Mention}, войди в него, и новый чертог восстанет для нашего квеста!", ephemeral: true);
    }

    [SlashCommand("category", "Задать категорию, под которой рождаются временные залы.")]
    public async Task CategoryAsync(
        InteractionContext ctx,
        [Option("category", "Канал-категория, где обитают временные залы.")] DiscordChannel category)
    {
        if (category.Type != ChannelType.Category)
        {
            await ctx.ReplyAsync("Не страшись, доблестный товарищ, но это должна быть категория!", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.TempCategoryId = category.Id);
        await ctx.ReplyAsync($"Славно! Отныне новые чертоги восстанут под сенью **{category.Name}**!", ephemeral: true);
    }

    [SlashCommand("userlimit", "Задать предел гостей для новых временных залов (0 = без предела).")]
    public async Task UserLimitAsync(
        InteractionContext ctx,
        [Option("limit", "Предел гостей по умолчанию, 0-99.")] long limit)
    {
        limit = Math.Clamp(limit, 0, 99);
        await UpdateAsync(ctx.Guild!.Id, c => c.DefaultUserLimit = (int)limit);
        await ctx.ReplyAsync($"Ура! Отныне каждый чертог примет **{(limit == 0 ? "без счёта" : limit.ToString())}** доблестных товарищей!", ephemeral: true);
    }

    [SlashCommand("nametemplate", "Задать шаблон имени временного зала. {user} — имя владыки.")]
    public async Task NameTemplateAsync(
        InteractionContext ctx,
        [Option("template", "Шаблон, напр. \"Чертог {user}\".")] string template)
    {
        template = template.Trim();
        if (template.Length is 0 or > 90)
        {
            await ctx.ReplyAsync("Увы, друг мой, но титул должен быть в 1–90 знаков!", ephemeral: true);
            return;
        }
        await UpdateAsync(ctx.Guild!.Id, c => c.TempNameTemplate = template);
        await ctx.ReplyAsync($"Славно! Отныне каждый чертог наречётся `{template}`!", ephemeral: true);
    }

    [SlashCommand("djrole", "Отдать музыку одной роли (без роли — играть волен каждый).")]
    public async Task DjRoleAsync(
        InteractionContext ctx,
        [Option("role", "Роль, нужная для команд музыки-баллады.")] DiscordRole? role = null)
    {
        await UpdateAsync(ctx.Guild!.Id, c => c.DjRoleId = role?.Id);
        await ctx.ReplyAsync(role is null
            ? "Ура! Баллады барда отныне открыты каждому товарищу!"
            : $"Да будет так! Отныне лишь носители {role.Mention} вольны призвать барда!", ephemeral: true);
    }

    [SlashCommand("suggestions", "Задать канал, куда падают предложения от /suggest.")]
    public async Task SuggestionsAsync(
        InteractionContext ctx,
        [Option("channel", "Текстовый канал для предложений.")] DiscordChannel channel)
    {
        if (channel.Type is not ChannelType.Text and not ChannelType.News)
        {
            await ctx.ReplyAsync("Не страшись, но это должен быть текстовый канал!", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.SuggestionsChannelId = channel.Id);
        await ctx.ReplyAsync($"Славно! Предложения товарищей отныне падают в {channel.Mention}!", ephemeral: true);
    }

    [SlashCommand("autorole", "Роль, выдаваемая всем при входе (без роли — выключить).")]
    public async Task AutoRoleAsync(
        InteractionContext ctx,
        [Option("role", "Роль для новых участников.")] DiscordRole? role = null)
    {
        await UpdateAsync(ctx.Guild!.Id, c => c.AutoRoleId = role?.Id);
        await ctx.ReplyAsync(role is null
            ? "Отныне новобранцы вступают в ряды без роли."
            : $"Славно! Каждый новый товарищ получит {role.Mention} при входе в наши ряды!", ephemeral: true);
    }

    [SlashCommand("autorolesync", "Пожаловать авто-роль всем нынешним участникам.")]
    public async Task AutoRoleSyncAsync(InteractionContext ctx)
    {
        ulong? roleId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            roleId = await db.GuildConfigs.AsNoTracking()
                .Where(c => c.GuildId == ctx.Guild!.Id)
                .Select(c => c.AutoRoleId)
                .FirstOrDefaultAsync();
        }

        if (roleId is null)
        {
            await ctx.ReplyAsync("Сперва назначь авто-роль через `/config autorole`, друг мой!", ephemeral: true);
            return;
        }

        var role = ctx.Guild!.GetRole(roleId.Value);
        if (role is null)
        {
            await ctx.ReplyAsync("Не сыскал сей роли — быть может, её упразднили?", ephemeral: true);
            return;
        }

        await ctx.DeferAsync(ephemeral: true);

        int granted = 0, failed = 0;
        foreach (var member in await ctx.Guild.GetAllMembersAsync())
        {
            if (member.IsBot || member.Roles.Any(r => r.Id == role.Id))
            {
                continue;
            }
            try { await member.GrantRoleAsync(role, "autorole sync"); granted++; }
            catch { failed++; }
        }

        await ctx.EditAsync(
            $"Готово! Роль {role.Mention} пожалована **{granted}** товарищам" +
            (failed > 0 ? $" (не вышло у {failed} — проверь мои права и старшинство роли)." : "."));
    }

    [SlashCommand("show", "Показать нынешние настройки нашего квеста.")]
    public async Task ShowAsync(InteractionContext ctx)
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
            .AddField("Зал предложений", config.SuggestionsChannelId is { } sug ? $"<#{sug}>" : "_не задан_", inline: true)
            .AddField("Авто-роль при входе", config.AutoRoleId is { } ar ? $"<@&{ar}>" : "_нет_", inline: true)
            .Build();

        await ctx.ReplyAsync(embed);
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
