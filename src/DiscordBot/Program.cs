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
using DSharpPlus.Commands;
using DSharpPlus.VoiceNext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Load a local .env (if present) into environment variables before configuration is read.
LoadDotEnv();

// Anchor the content root to the binary's folder so appsettings.json is found no matter which
// directory the bot is launched from.
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
builder.Services.AddSingleton<TempVoiceManager>();
builder.Services.AddSingleton<TrackResolver>();
builder.Services.AddSingleton<MusicPlaybackService>();

// Notification pipeline: Discord sink always; YouTube source only when an API key is present.
builder.Services.AddSingleton<INotificationSink, DiscordNotificationSink>();
if (!string.IsNullOrWhiteSpace(youTubeOptions.ApiKey))
{
    builder.Services.AddSingleton<INotificationSource, YouTubeNotificationSource>();
}

// --- Discord client (integrated with the generic host's service collection) ---
var intents = DiscordIntents.AllUnprivileged | DiscordIntents.GuildVoiceStates;

DiscordClientBuilder.CreateDefault(discordOptions.Token, intents, builder.Services)
    .UseCommands(
        (_, extension) => extension.AddCommands(
        [
            typeof(ConfigCommands),
            typeof(NotifyCommands),
            typeof(VoiceCommands),
            // Music commands are temporarily disabled: VoiceNext voice playback is unstable on the
            // current DSharpPlus 5 nightly. Re-add typeof(MusicCommands) to bring them back.
            typeof(HelpCommands),
        ]),
        new CommandsConfiguration
        {
            RegisterDefaultCommandProcessors = true,
            DebugGuildId = discordOptions.DebugGuildId ?? 0,
        })
    .UseVoiceNext(new VoiceNextConfiguration())
    .ConfigureEventHandlers(events => events.AddEventHandlers<VoiceStateHandler>());

// --- Hosted services (order matters: connect Discord before the notifier polls) ---
builder.Services.AddHostedService<DiscordStartupService>();
builder.Services.AddHostedService<NotificationDispatcher>();

var host = builder.Build();

// Ensure the database schema exists.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
    await db.Database.EnsureCreatedAsync();
}

try
{
    await host.RunAsync();
}
catch (DSharpPlus.Exceptions.UnauthorizedException)
{
    // Already logged a clear message in DiscordStartupService; exit non-zero without a stack dump.
    return 1;
}
catch (Exception ex)
{
    // Swallow shutdown/disposal hiccups (e.g. disposing a client that never finished connecting)
    // so they don't surface as an unhandled crash.
    Console.Error.WriteLine($"Host terminated: {ex.Message}");
    return 1;
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
