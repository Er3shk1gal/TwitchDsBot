using DiscordBot.Data;
using DiscordBot.Data.Entities;
using DiscordBot.Discord.Music;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Lavalink4NET.Rest.Entities.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordBot.Discord.Commands;

/// <summary>/radio — admins add named streams; anyone plays one (direct URL → Lavalink http source).</summary>
[SlashCommandGroup("radio", "Радиопотоки Санчо: добавить и слушать.")]
[SlashRequireGuild]
public sealed class RadioCommands : ApplicationCommandModule
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MusicPlaybackService _music;

    public RadioCommands(IServiceScopeFactory scopeFactory, MusicPlaybackService music)
    {
        _scopeFactory = scopeFactory;
        _music = music;
    }

    [SlashCommand("add", "Добавить радиопоток (админ).")]
    [SlashRequirePermissions(Permissions.Administrator)]
    public async Task AddAsync(
        InteractionContext ctx,
        [Option("name", "Название потока.")] string name,
        [Option("url", "Прямой URL аудиопотока (http/https).")] string url)
    {
        name = name.Trim();
        url = url.Trim();
        if (name.Length is 0 or > 80)
        {
            await ctx.ReplyAsync("Название должно быть в 1–80 знаков, друг мой!", ephemeral: true);
            return;
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.ReplyAsync("URL должен начинаться с `http://` или `https://`!", ephemeral: true);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        if (await db.RadioStreams.AnyAsync(s => s.GuildId == ctx.Guild!.Id && s.Name == name))
        {
            await ctx.ReplyAsync($"Поток **{name}** уже в наших свитках!", ephemeral: true);
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
        await ctx.ReplyAsync($"Славно! Поток **{name}** вписан в свитки — зови его через `/radio play`!", ephemeral: true);
    }

    [SlashCommand("remove", "Убрать радиопоток (админ).")]
    [SlashRequirePermissions(Permissions.Administrator)]
    public async Task RemoveAsync(
        InteractionContext ctx,
        [Option("name", "Название потока.")]
        [Autocomplete(typeof(RadioAutoCompleteProvider))] string name)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var stream = await db.RadioStreams.FirstOrDefaultAsync(s => s.GuildId == ctx.Guild!.Id && s.Name == name);
        if (stream is null)
        {
            await ctx.ReplyAsync("Нет такого потока в свитках, друг мой.", ephemeral: true);
            return;
        }

        db.RadioStreams.Remove(stream);
        await db.SaveChangesAsync();
        await ctx.ReplyAsync($"Поток **{name}** вычеркнут из свитков.", ephemeral: true);
    }

    [SlashCommand("list", "Показать добавленные радиопотоки.")]
    public async Task ListAsync(InteractionContext ctx)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var streams = await db.RadioStreams.AsNoTracking()
            .Where(s => s.GuildId == ctx.Guild!.Id)
            .OrderBy(s => s.Name)
            .ToListAsync();

        if (streams.Count == 0)
        {
            await ctx.ReplyAsync("Свитки пусты — пусть владыка добавит поток через `/radio add`.", ephemeral: true);
            return;
        }

        var list = string.Join("\n", streams.Select(s => $"📻 **{s.Name}**"));
        await ctx.ReplyAsync(new DiscordEmbedBuilder()
            .WithColor(new DiscordColor(0x1DB954))
            .WithTitle("Радиопотоки Санчо")
            .WithDescription(list)
            .Build());
    }

    [SlashCommand("play", "Включить радиопоток из добавленных.")]
    public async Task PlayAsync(
        InteractionContext ctx,
        [Option("name", "Какой поток включить.")]
        [Autocomplete(typeof(RadioAutoCompleteProvider))] string name)
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
            await ctx.ReplyAsync("Нет такого потока — глянь `/radio list`.", ephemeral: true);
            return;
        }

        var voiceChannelId = ctx.Member?.VoiceState?.Channel?.Id;
        if (voiceChannelId is null)
        {
            await ctx.ReplyAsync("Встань в голосовой зал, дабы я заиграл, друг мой!", ephemeral: true);
            return;
        }

        await ctx.DeferAsync();

        var (player, _) = await _music.GetPlayerAsync(ctx.Guild!.Id, voiceChannelId, connect: true);
        if (player is null)
        {
            await ctx.EditAsync("Не смог войти в зал — проверьте мои права!");
            return;
        }

        // Radio is a single live stream: load the direct URL (Lavalink's http source) and play it,
        // clearing whatever was queued so the stream takes over immediately.
        var track = await _music.LoadTrackAsync(stream.Url, TrackSearchMode.None);
        if (track is null)
        {
            await ctx.EditAsync($"Увы, поток **{stream.Name}** не отозвался — проверь URL, друг мой!");
            return;
        }

        await player.StopAsync();
        await player.PlayAsync(track, enqueue: false);

        await ctx.EditAsync($"📻 Ставлю **{stream.Name}** — да звучит вечно!");
    }

    [SlashCommand("stop", "Выключить радио и покинуть зал.")]
    public async Task StopAsync(InteractionContext ctx)
    {
        var player = await _music.TryGetAsync(ctx.Guild!.Id);
        if (player is not null)
        {
            await player.DisconnectAsync();
        }
        await ctx.ReplyAsync("📻 Радио умолкло — до новых встреч, товарищ!");
    }
}

/// <summary>Autocompletes a radio stream name from the guild's saved streams.</summary>
public sealed class RadioAutoCompleteProvider : IAutocompleteProvider
{
    public async Task<IEnumerable<DiscordAutoCompleteChoice>> Provider(AutocompleteContext context)
    {
        if (context.Guild is null)
        {
            return [];
        }

        var scopeFactory = context.Services.GetRequiredService<IServiceScopeFactory>();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var input = (context.OptionValue as string ?? string.Empty);
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
