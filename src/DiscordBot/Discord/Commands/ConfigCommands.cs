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
[Description("Configure temporary voice channels and music.")]
[RequireGuild]
[RequirePermissions(DiscordPermission.Administrator)]
public sealed class ConfigCommands
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ConfigCommands(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    [Command("lobby")]
    [Description("Set the 'join to create' voice channel.")]
    public async ValueTask LobbyAsync(
        SlashCommandContext ctx,
        [Description("The voice channel users join to spawn a temp channel.")] DiscordChannel channel)
    {
        if (channel.Type != DiscordChannelType.Voice)
        {
            await ctx.RespondAsync("That must be a voice channel.", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.LobbyChannelId = channel.Id);
        await ctx.RespondAsync($"Lobby channel set to {channel.Mention}. Joining it now creates a temp channel.", ephemeral: true);
    }

    [Command("category")]
    [Description("Set the category new temp channels are created under.")]
    public async ValueTask CategoryAsync(
        SlashCommandContext ctx,
        [Description("A category channel (leave temp channels here).")] DiscordChannel category)
    {
        if (category.Type != DiscordChannelType.Category)
        {
            await ctx.RespondAsync("That must be a category.", ephemeral: true);
            return;
        }

        await UpdateAsync(ctx.Guild!.Id, c => c.TempCategoryId = category.Id);
        await ctx.RespondAsync($"Temp channels will be created under **{category.Name}**.", ephemeral: true);
    }

    [Command("userlimit")]
    [Description("Set the default user limit for new temp channels (0 = unlimited).")]
    public async ValueTask UserLimitAsync(
        SlashCommandContext ctx,
        [Description("Default max users, 0-99.")] int limit)
    {
        limit = Math.Clamp(limit, 0, 99);
        await UpdateAsync(ctx.Guild!.Id, c => c.DefaultUserLimit = limit);
        await ctx.RespondAsync($"Default user limit set to **{(limit == 0 ? "unlimited" : limit.ToString())}**.", ephemeral: true);
    }

    [Command("nametemplate")]
    [Description("Set the temp channel name template. Use {user} for the owner's name.")]
    public async ValueTask NameTemplateAsync(
        SlashCommandContext ctx,
        [Description("Template, e.g. \"{user}'s room\".")] string template)
    {
        template = template.Trim();
        if (template.Length is 0 or > 90)
        {
            await ctx.RespondAsync("Template must be 1–90 characters.", ephemeral: true);
            return;
        }
        await UpdateAsync(ctx.Guild!.Id, c => c.TempNameTemplate = template);
        await ctx.RespondAsync($"Name template set to `{template}`.", ephemeral: true);
    }

    [Command("djrole")]
    [Description("Restrict music commands to a role (omit the role to allow everyone).")]
    public async ValueTask DjRoleAsync(
        SlashCommandContext ctx,
        [Description("Role required for music commands.")] DiscordRole? role = null)
    {
        await UpdateAsync(ctx.Guild!.Id, c => c.DjRoleId = role?.Id);
        await ctx.RespondAsync(role is null
            ? "Music commands are now open to everyone."
            : $"Music commands now require {role.Mention}.", ephemeral: true);
    }

    [Command("show")]
    [Description("Show the current configuration.")]
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
            .WithTitle("Server configuration")
            .AddField("Lobby channel", config.LobbyChannelId is { } l ? $"<#{l}>" : "_not set_", inline: true)
            .AddField("Temp category", config.TempCategoryId is { } cat ? $"<#{cat}>" : "_same as lobby_", inline: true)
            .AddField("Default user limit", config.DefaultUserLimit == 0 ? "unlimited" : config.DefaultUserLimit.ToString(), inline: true)
            .AddField("Name template", $"`{config.TempNameTemplate}`", inline: false)
            .AddField("DJ role", config.DjRoleId is { } dj ? $"<@&{dj}>" : "_everyone_", inline: true)
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
