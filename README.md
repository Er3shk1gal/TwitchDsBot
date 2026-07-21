# Discord Bot (.NET · DSharpPlus 4.5)

A modular Discord bot with three features and room to grow:

1. **Temporary voice channels** ("join to create") — join a configured lobby voice channel and the bot spins up a personal voice channel you control with `/voice …` (user limit, name, lock, permit, kick, bitrate, claim, transfer).
2. **Music & radio** — `/play` from SoundCloud and other open sources, with a queue, seek, loop, shuffle and volume; `/radio` for admin-curated live streams. Audio (and the Discord voice connection) is handled by a **Lavalink** server via **Lavalink4NET**.
3. **YouTube notifier** — posts an embed (and can pin live streams) to a chosen text channel when a watched YouTube channel uploads a video, posts a Short, or goes live.

Built to extend: adding **Twitch** notifications is a new `INotificationSource`; sending notifications to **Telegram** is a new `INotificationSink`. Neither touches the core pipeline.

---

## Tech stack

| Concern | Choice |
| --- | --- |
| Language / runtime | C# / .NET 9 |
| Discord library | DSharpPlus `4.5.2` + `DSharpPlus.SlashCommands` |
| Voice / audio | **Lavalink** server + `Lavalink4NET.DSharpPlus` |
| Storage | SQLite via EF Core 9 |
| YouTube | `Google.Apis.YouTube.v3` (Data API v3, API-key auth) |
| Host | `Microsoft.Extensions.Hosting` generic host |

> **Why Lavalink?** Discord retired the old voice-gateway protocol that `DSharpPlus.VoiceNext` speaks (voice gateway `v4`), so VoiceNext connections now fail with close code `4006`. Lavalink is a dedicated audio server that owns the Discord voice connection with a current protocol and is kept up to date; the bot forwards voice state to it and it does the streaming. `Lavalink4NET` targets DSharpPlus **4.x**, which is why the bot is pinned to 4.5.2. `docker compose up` runs Lavalink for you.

---

## Prerequisites

- **Docker** (recommended) — `docker compose up` runs the bot **and** Lavalink together; nothing else to install.
- A **Discord bot application + token** — <https://discord.com/developers/applications>
- A **YouTube Data API v3 key** (only for the notifier) — <https://console.cloud.google.com/>

For running the bot **outside** Docker you also need:

- **.NET 9 SDK** — <https://dotnet.microsoft.com/download>
- A reachable **Lavalink v4 server** — <https://lavalink.dev/> (point `Music:LavalinkAddress` / `Music:LavalinkPassphrase` at it).

### Discord application setup

1. Create an application → **Bot** → copy the **token**.
2. Invite the bot with an OAuth2 URL using the `bot` and `applications.commands` scopes and at least these permissions: **View Channels, Send Messages, Embed Links, Manage Messages** (to pin), **Connect, Speak, Move Members, Manage Channels** (temp voice + music).
3. No privileged intents are required by default (the bot uses `AllUnprivileged | GuildVoiceStates`).

---

## Configuration

Settings come from `appsettings.json` and can be overridden by environment variables (double underscore = nested key), or a local `.env` file (see `.env.example`).

`src/DiscordBot/appsettings.json`:

```json
{
  "Discord":  { "Token": "", "DebugGuildId": null },
  "Music":    { "LavalinkAddress": "http://127.0.0.1:2333", "LavalinkPassphrase": "youshallnotpass", "DefaultSearchMode": "scsearch" },
  "YouTube":  { "ApiKey": "", "PollIntervalSeconds": 180 },
  "Database": { "ConnectionString": "Data Source=bot.db" }
}
```

- `Discord:Token` **(required)** — your bot token. Prefer the `DISCORD__TOKEN` env var or `.env` over committing it.
- `Discord:DebugGuildId` — set to a guild id to register slash commands **instantly** in that server (great for development). Leave `null` for **global** registration (can take up to ~1 hour to appear).
- `Music:LavalinkAddress` / `Music:LavalinkPassphrase` — where the Lavalink server lives and its password. Under Docker these are set for you (loopback + `LAVALINK_PASSWORD`).
- `Music:DefaultSearchMode` — search prefix for non-URL `/play` queries: `scsearch` = SoundCloud, `bcsearch` = Bandcamp, `ytsearch` = YouTube (needs the youtube-source plugin on the Lavalink server).
- `YouTube:ApiKey` — omit to disable the notifier entirely (the `/notify youtube` command will report it's unavailable).
- `YouTube:PollIntervalSeconds` — how often each subscription is polled. Mind the quota (below).

Environment-variable form (identical effect):

```
DISCORD__TOKEN=...            DISCORD__DEBUGGUILDID=123456789012345678
MUSIC__LAVALINKADDRESS=http://127.0.0.1:2333   MUSIC__DEFAULTSEARCHMODE=scsearch
YOUTUBE__APIKEY=...           YOUTUBE__POLLINTERVALSECONDS=180
DATABASE__CONNECTIONSTRING=Data Source=bot.db
```

---

## Run

```bash
# from the repo root
cp src/DiscordBot/appsettings.Example.json src/DiscordBot/appsettings.json   # then fill in the token
# ...or: cp .env.example .env  and fill it in

dotnet run --project src/DiscordBot
```

On start the bot creates `bot.db`, connects to Discord, registers slash commands, and begins polling any YouTube subscriptions.

### Run with Docker (recommended)

`docker compose` starts two containers: the **bot** and a **Lavalink** audio server. Only Docker is required.

```bash
cp .env.example .env          # fill in DISCORD__TOKEN (and YOUTUBE__APIKEY for the notifier)
docker compose up -d          # build the bot image, pull Lavalink, start both
docker compose logs -f        # watch it connect
```

- Settings come from `.env` (see `.env.example`). The compose file wires the bot to Lavalink on the loopback address and forces the DB path, so you don't touch those in `.env`.
- Both containers use **host networking** (Linux) so Discord voice UDP takes the host's network path. Lavalink binds its API to `127.0.0.1` only, so port `2333` is not exposed off the machine.
- The SQLite database persists in the named volume **`bot-data`** across restarts and rebuilds.
- Update the bot: `docker compose up -d --build`. Stop it: `docker compose down` (add `-v` to also wipe the database volume).

---

## Commands

### Configuration — `/config` (admin only)

| Command | Description |
| --- | --- |
| `/config lobby <voice channel>` | Set the "join to create" lobby channel. |
| `/config category <category>` | Category new temp channels are created under. |
| `/config userlimit <0-99>` | Default user limit for new temp channels (0 = unlimited). |
| `/config nametemplate <text>` | Temp channel name template; `{user}` = owner's name. |
| `/config djrole [role]` | Restrict music commands to a role (omit to allow everyone). |
| `/config show` | Show the current configuration. |

### Temp voice — `/voice` (channel owner)

`/voice limit`, `/voice name`, `/voice bitrate`, `/voice lock`, `/voice unlock`, `/voice permit <user>`, `/voice kick <user>`, `/voice claim` (take over if the owner left), `/voice transfer <user>`.

### Music

`/play <url|search>`, `/skip`, `/stop`, `/pause`, `/resume`, `/queue`, `/nowplaying`, `/volume <0-200>`, `/seek <sec|m:ss>`, `/loop`, `/shuffle`, `/leave`.

### Notifications — `/notify` (admin only)

| Command | Description |
| --- | --- |
| `/notify youtube <channel> <target> [uploads] [shorts] [liveStreams] [pinLive] [mention]` | Watch a YouTube channel (URL, `@handle`, or `UC…` id) and post to `<target>`. |
| `/notify list` | List this server's subscriptions. |
| `/notify remove <id>` | Remove a subscription. |

Existing videos are **not** re-announced when you add a subscription — the first poll only records what's already there.

---

## YouTube quota

Default quota is **10,000 units/day**. The notifier uses the cheap flow: `playlistItems.list` (1 unit) + `videos.list` (1 unit) per channel per poll — about **2 units/cycle/channel**.

| Poll interval | Units/channel/day | Channels that fit in 10k |
| --- | --- | --- |
| 5 min | ~576 | ~17 |
| 3 min (default) | ~960 | ~10 |
| 1 min | ~2880 | ~3 |

Live detection rides on the same uploads-playlist poll, so there may be a short propagation delay (a few minutes) before a stream is announced. Sub-minute live detection would require the 100-unit `search.list eventType=live` call, which is intentionally **not** used.

**Shorts** have no official API flag; the bot treats videos ≤ 60 s as Shorts. Some longer Shorts (YouTube now allows up to 3 min) will be announced as regular uploads.

---

## Architecture

```
Program.cs                      composition root (generic host + DI)
Configuration/                  strongly-typed options
Data/                           EF Core context + entities (SQLite)
Discord/
  DiscordStartupService         connects/disconnects the gateway
  Events/VoiceStateHandler      -> TempVoiceManager (join-to-create + startup sweep)
  TempVoice/TempVoiceManager    create / track ownership / clean up temp channels
  Music/
    MusicPlaybackService        wraps Lavalink4NET IAudioService (retrieve player, load, play)
  Commands/                     /config /voice /music /radio /notify slash commands
Notifications/
  ContentEvent                  source-agnostic "something new" event
  INotificationSource           produces events (YouTube today; Twitch later)
  INotificationSink             delivers events (Discord today; Telegram later)
  NotificationDispatcher        hosted service: poll -> dedup -> fan out to sinks
  Sources/YouTubeNotificationSource
  Sinks/DiscordNotificationSink
```

The notifier is deliberately source- and sink-agnostic so new platforms are additive.

---

## Extending

### Add a Twitch source (stream-started notifications)

1. Implement `INotificationSource` with `SourceType => ContentSourceType.Twitch`:
   - `ResolveAsync` turns a Twitch login/URL into a channel id + display name.
   - `PollAsync` (or a webhook) yields a `ContentEvent { Kind = LiveStarted }` when a stream goes live.
2. Register it in `Program.cs`: `builder.Services.AddSingleton<INotificationSource, TwitchNotificationSource>();`
3. Add a `/notify twitch` command mirroring `/notify youtube`.

The dispatcher already routes by `SourceType` and the Discord sink already pins live streams (`PinLiveStreams`) — no core changes needed.

### Add a Telegram sink (post to a TG channel)

1. Implement `INotificationSink` (`Name => "telegram"`), delivering the `ContentEvent` via the Telegram Bot API.
2. Make `AppliesTo(subscription)` true only for subscriptions that carry Telegram delivery config (extend `NotificationSubscription` and the `/notify` command with a Telegram target).
3. Register it: `builder.Services.AddSingleton<INotificationSink, TelegramNotificationSink>();`

Every event is already fanned out to **all** applicable sinks, so YouTube→Discord and YouTube→Telegram work side by side.

---

## Notes & limitations

- The database uses `EnsureCreated` (no migrations). If you change the entity model, delete `bot.db` or switch to EF Core migrations.
- One temp channel is created per lobby join and deleted when it empties; a startup sweep cleans up any left over after a crash.
- Music/radio playback needs the Lavalink server reachable; `/play` reports a clear error if a track can't be loaded. Lavalink4NET reconnects automatically if Lavalink restarts.
- **Shorts detection** treats videos ≤ 60 s as Shorts (no official API flag exists); some longer Shorts are announced as regular uploads.
- **YouTube Premieres** that carry live-stream metadata are announced via the live/upcoming path, not the plain-upload path — the API can't distinguish a finished Premiere from a finished live stream.
- **Live pinning** pins each new live announcement; it does not unpin older ones. Watch Discord's 50-pin-per-channel limit on very active channels.
- **Notifier delivery** marks an item "seen" only after it's delivered, so a transient outage is retried. With multiple sinks configured for one subscription, a failure in one sink re-delivers to all next cycle (only the Discord sink ships today).
