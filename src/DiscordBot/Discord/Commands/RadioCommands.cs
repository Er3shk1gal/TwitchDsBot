using System.ComponentModel;
using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DiscordBot.Discord.Music;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ContextChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Processors.SlashCommands.ArgumentModifiers;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>/radio — admins add named streams; anyone plays one (direct URL → ffmpeg → VoiceNext).</summary>
[Command("radio")]
[Description("Радиопотоки Санчо: добавить и слушать.")]
[RequireGuild]
public sealed class RadioCommands
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MusicPlaybackService _music;

    public RadioCommands(IServiceScopeFactory scopeFactory, MusicPlaybackService music)
    {
        _scopeFactory = scopeFactory;
        _music = music;
    }

    [Command("add")]
    [Description("Добавить радиопоток (админ).")]
    [RequirePermissions(DiscordPermission.Administrator)]
    public async ValueTask AddAsync(
        SlashCommandContext ctx,
        [Description("Название потока.")] string name,
        [Description("Прямой URL аудиопотока (http/https).")] string url)
    {
        name = name.Trim();
        url = url.Trim();
        if (name.Length is 0 or > 80)
        {
            await ctx.RespondAsync("Название должно быть в 1–80 знаков, друг мой!", ephemeral: true);
            return;
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.RespondAsync("URL должен начинаться с `http://` или `https://`!", ephemeral: true);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        if (await db.RadioStreams.AnyAsync(s => s.GuildId == ctx.Guild!.Id && s.Name == name))
        {
            await ctx.RespondAsync($"Поток **{name}** уже в наших свитках!", ephemeral: true);
            return;
        }

        db.RadioStreams.Add(new RadioStream
        {
            GuildId = ctx.Guild!.Id,
            Name = name,
            Url = url,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await ctx.RespondAsync($"Славно! Поток **{name}** вписан в свитки — зови его через `/radio play`!", ephemeral: true);
    }

    [Command("remove")]
    [Description("Убрать радиопоток (админ).")]
    [RequirePermissions(DiscordPermission.Administrator)]
    public async ValueTask RemoveAsync(
        SlashCommandContext ctx,
        [Description("Название потока.")]
        [SlashAutoCompleteProvider(typeof(RadioAutoCompleteProvider))] string name)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var stream = await db.RadioStreams.FirstOrDefaultAsync(s => s.GuildId == ctx.Guild!.Id && s.Name == name);
        if (stream is null)
        {
            await ctx.RespondAsync("Нет такого потока в свитках, друг мой.", ephemeral: true);
            return;
        }

        db.RadioStreams.Remove(stream);
        await db.SaveChangesAsync();
        await ctx.RespondAsync($"Поток **{name}** вычеркнут из свитков.", ephemeral: true);
    }

    [Command("list")]
    [Description("Показать добавленные радиопотоки.")]
    public async ValueTask ListAsync(SlashCommandContext ctx)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var streams = await db.RadioStreams.AsNoTracking()
            .Where(s => s.GuildId == ctx.Guild!.Id)
            .OrderBy(s => s.Name)
            .ToListAsync();

        if (streams.Count == 0)
        {
            await ctx.RespondAsync("Свитки пусты — пусть владыка добавит поток через `/radio add`.", ephemeral: true);
            return;
        }

        var list = string.Join("\n", streams.Select(s => $"📻 **{s.Name}**"));
        await ctx.RespondAsync(new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithTitle("Радиопотоки Санчо")
            .WithDescription(list)
            .Build());
    }

    [Command("play")]
    [Description("Включить радиопоток из добавленных.")]
    public async ValueTask PlayAsync(
        SlashCommandContext ctx,
        [Description("Какой поток включить.")]
        [SlashAutoCompleteProvider(typeof(RadioAutoCompleteProvider))] string name)
    {
        RadioStream? stream;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            stream = await db.RadioStreams.AsNoTracking()
                .FirstOrDefaultAsync(s => s.GuildId == ctx.Guild!.Id && s.Name == name);
        }
        if (stream is null)
        {
            await ctx.RespondAsync("Нет такого потока — глянь `/radio list`.", ephemeral: true);
            return;
        }

        var voiceChannel = await GetVoiceChannelAsync(ctx.Member);
        if (voiceChannel is null)
        {
            await ctx.RespondAsync("Встань в голосовой зал, дабы я заиграл, друг мой!", ephemeral: true);
            return;
        }

        await ctx.DeferResponseAsync();

        GuildMusicPlayer player;
        try
        {
            player = await _music.GetOrCreateAsync(voiceChannel, ctx.Channel);
        }
        catch
        {
            await ctx.EditResponseAsync("Не смог войти в зал — проверьте мои права!");
            return;
        }

        player.Stop(); // replace whatever was playing
        player.Enqueue(new ResolvedTrack
        {
            Title = "📻 " + stream.Name,
            StreamUrl = stream.Url,
            WebpageUrl = stream.Url,
            IsDirectStream = true,
            RequestedBy = ctx.User.Id,
        });

        await ctx.EditResponseAsync($"📻 Ставлю **{stream.Name}** — да звучит вечно!");
    }

    [Command("stop")]
    [Description("Выключить радио и покинуть зал.")]
    public async ValueTask StopAsync(SlashCommandContext ctx)
    {
        await _music.DisconnectAsync(ctx.Guild!.Id);
        await ctx.RespondAsync("📻 Радио умолкло — до новых встреч, товарищ!");
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
}

/// <summary>Autocompletes a radio stream name from the guild's saved streams.</summary>
public sealed class RadioAutoCompleteProvider : IAutoCompleteProvider
{
    public async ValueTask<IEnumerable<DiscordAutoCompleteChoice>> AutoCompleteAsync(AutoCompleteContext context)
    {
        if (context.Guild is null)
        {
            return [];
        }

        var scopeFactory = context.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var input = context.UserInput ?? string.Empty;
        var streams = await db.RadioStreams.AsNoTracking()
            .Where(s => s.GuildId == context.Guild.Id)
            .OrderBy(s => s.Name)
            .ToListAsync();

        return streams
            .Where(s => s.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(s => new DiscordAutoCompleteChoice(s.Name, s.Name));
    }
}
