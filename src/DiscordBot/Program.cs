using DiscordBot.Configuration;
using DiscordBot.Data;
using DiscordBot.Discord;
using DiscordBot.Discord.Commands;
using DiscordBot.Discord.Events;
using DiscordBot.Discord.Music;
using DiscordBot.Discord.TempVoice;
using DiscordBot.Notifications;
using DiscordBot.Notifications.Sinks;
using DiscordBot.Notifications.Sources;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using DSharpPlus.VoiceNext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Load a local .env (if present) into environment variables before configuration is read.
LoadDotEnv();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// --- Options ---
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.Section));
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection(MusicOptions.Section));
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection(YouTubeOptions.Section));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));

var discordOptions = builder.Configuration.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();
var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
var youTubeOptions = builder.Configuration.GetSection(YouTubeOptions.Section).Get<YouTubeOptions>() ?? new YouTubeOptions();

if (string.IsNullOrWhiteSpace(discordOptions.Token))
{
    Console.Error.WriteLine("No Discord token configured. Set Discord:Token in appsettings.json or DISCORD__TOKEN.");
    return 1;
}

// --- Persistence ---
builder.Services.AddDbContext<BotDbContext>(options => options.UseSqlite(databaseOptions.ConnectionString));

// --- Shared application services ---
builder.Services.AddHttpClient();
builder.Services.AddSingleton<BotClientAccessor>();
builder.Services.AddSingleton<TempVoiceManager>();
builder.Services.AddSingleton<TrackResolver>();
builder.Services.AddSingleton<MusicPlaybackService>();
builder.Services.AddSingleton<DiscordEventHandlers>();

builder.Services.AddSingleton<INotificationSink, DiscordNotificationSink>();
if (!string.IsNullOrWhiteSpace(youTubeOptions.ApiKey))
{
    builder.Services.AddSingleton<INotificationSource, YouTubeNotificationSource>();
}

builder.Services.AddHostedService<NotificationDispatcher>();

var host = builder.Build();

// --- Ensure the database schema exists (+ forward-migrate columns/tables on existing DBs) ---
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
    await db.Database.EnsureCreatedAsync();

    (string Table, string Column, string Type)[] addedColumns =
    [
        ("Subscriptions", "PrimedAt", "TEXT"),
        ("GuildConfigs", "SuggestionsChannelId", "INTEGER"),
        ("GuildConfigs", "AutoRoleId", "INTEGER"),
    ];
    foreach (var (table, column, type) in addedColumns)
    {
        try
        {
            var sql = "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + type + " NULL";
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch { /* column already exists */ }
    }

    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"RadioStreams\" (" +
            "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_RadioStreams\" PRIMARY KEY AUTOINCREMENT, " +
            "\"GuildId\" INTEGER NOT NULL, \"Name\" TEXT NOT NULL, \"Url\" TEXT NOT NULL, \"CreatedAt\" TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RadioStreams_GuildId_Name\" ON \"RadioStreams\" (\"GuildId\", \"Name\")");
    }
    catch { /* table/index already exist */ }
}

// --- DSharpPlus v4 client (created after the host so command modules resolve from host.Services) ---
var discord = new DiscordClient(new DiscordConfiguration
{
    Token = discordOptions.Token,
    TokenType = TokenType.Bot,
    Intents = DiscordIntents.AllUnprivileged | DiscordIntents.GuildVoiceStates | DiscordIntents.GuildMembers,
    LoggerFactory = host.Services.GetRequiredService<ILoggerFactory>(),
});

var voiceNext = discord.UseVoiceNext(new VoiceNextConfiguration());

var accessor = host.Services.GetRequiredService<BotClientAccessor>();
accessor.Client = discord;
accessor.VoiceNext = voiceNext;

var slash = discord.UseSlashCommands(new SlashCommandsConfiguration { Services = host.Services });
var debugGuild = discordOptions.DebugGuildId; // null => global registration
slash.RegisterCommands<ConfigCommands>(debugGuild);
slash.RegisterCommands<NotifyCommands>(debugGuild);
slash.RegisterCommands<VoiceCommands>(debugGuild);
slash.RegisterCommands<SuggestCommands>(debugGuild);
slash.RegisterCommands<RadioCommands>(debugGuild);
slash.RegisterCommands<MusicCommands>(debugGuild);
slash.RegisterCommands<HelpCommands>(debugGuild);

var events = host.Services.GetRequiredService<DiscordEventHandlers>();
discord.VoiceStateUpdated += events.OnVoiceStateUpdated;
discord.GuildDownloadCompleted += events.OnGuildDownloadCompleted;
discord.GuildMemberAdded += events.OnGuildMemberAdded;

try
{
    await discord.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to connect to Discord: {ex.Message}");
    return 1;
}

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Host terminated: {ex.Message}");
}
finally
{
    try { await discord.DisconnectAsync(); } catch { /* ignore */ }
}

return 0;

// Minimal .env loader: KEY=VALUE lines; ignores comments/blank lines; does not override existing vars.
static void LoadDotEnv()
{
    foreach (var candidate in new[] { ".env", Path.Combine("..", "..", ".env") })
    {
        var path = Path.GetFullPath(candidate);
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        break;
    }
}
