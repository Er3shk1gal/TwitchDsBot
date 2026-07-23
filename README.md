# Discord Bot (.NET · DSharpPlus 4.5)

A modular Discord bot with room to grow:

1. **Temporary voice channels** ("join to create") — join a configured lobby voice channel and the bot spins up a personal voice channel you control with `/voice …` (user limit, name, lock, permit, kick, bitrate, claim, transfer).
2. **Music & radio** — `/play` from SoundCloud and other open sources, with a queue, seek, loop, shuffle and volume; `/radio` for admin-curated live streams. Audio (and the Discord voice connection) is handled by a **Lavalink** server via **Lavalink4NET**.
3. **Content notifications** — posts an embed (and can pin live streams) when a watched **YouTube** channel uploads a video/Short/goes live, or a watched **Twitch** channel goes live. Delivers to a Discord channel (always) and optionally also to a **Telegram** chat (`/notify telegram`).
4. **Twitch chat responder** — replies with a canned "my links" message when someone types a configured command (e.g. `!links`) in your Twitch chat.

The notification pipeline is source/sink-agnostic (`INotificationSource` / `INotificationSink`), so adding another platform on either side is additive — no changes to the dispatcher.

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
- Optional, per feature you want:
  - A **YouTube Data API v3 key** (`/notify youtube`) — <https://console.cloud.google.com/>
  - A **Twitch application** (client id + secret, `/notify twitch`) — <https://dev.twitch.tv/console/apps>
  - A **Telegram bot token** (`/notify telegram`) — talk to [@BotFather](https://t.me/BotFather)
  - A **Twitch user OAuth token** (chat responder) — <https://twitchtokengenerator.com> or your own OAuth app, scopes `chat:read chat:edit`

Everything above is independently optional: leave a section's config empty and that feature just doesn't register (with a clear in-Discord message if you try to use its command).

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
  "Discord":    { "Token": "", "DebugGuildId": null },
  "Music":      { "LavalinkAddress": "http://127.0.0.1:2333", "LavalinkPassphrase": "youshallnotpass", "DefaultSearchMode": "scsearch" },
  "YouTube":    { "ApiKey": "", "PollIntervalSeconds": 180 },
  "Twitch":     { "ClientId": "", "ClientSecret": "" },
  "Telegram":   { "BotToken": "" },
  "TwitchChat": { "OAuthToken": "", "BotUsername": "", "Channels": [], "Command": "!links", "Message": "", "CooldownSeconds": 15 },
  "Database":   { "ConnectionString": "Data Source=bot.db" }
}
```

- `Discord:Token` **(required)** — your bot token. Prefer the `DISCORD__TOKEN` env var or `.env` over committing it.
- `Discord:DebugGuildId` — set to a guild id to register slash commands **instantly** in that server (great for development). Leave `null` for **global** registration (can take up to ~1 hour to appear).
- `Music:LavalinkAddress` / `Music:LavalinkPassphrase` — where the Lavalink server lives and its password. Under Docker these are set for you (loopback + `LAVALINK_PASSWORD`).
- `Music:DefaultSearchMode` — search prefix for non-URL `/play` queries: `scsearch` = SoundCloud, `bcsearch` = Bandcamp, `ytsearch` = YouTube (needs the youtube-source plugin on the Lavalink server).
- `YouTube:ApiKey` — omit to disable `/notify youtube` (the command will report it's unavailable). `YouTube:PollIntervalSeconds` — how often each subscription is polled; also sets the shared poll cadence for Twitch subscriptions. Mind the quota (below).
- `Twitch:ClientId` / `Twitch:ClientSecret` — app credentials for `/notify twitch` (Helix API, client-credentials grant — no Twitch login needed). Omit to disable `/notify twitch`.
- `Telegram:BotToken` — omit to disable `/notify telegram`. Give a numeric chat id or a public channel's `@username` when running the command; for a private group/channel, add the bot there first.
- `TwitchChat:*` — the "!links" chat responder. `OAuthToken` is a **user** token (`chat:read chat:edit` scopes) for the account that posts, `BotUsername` its login, `Channels` the channel(s) to join, `Command`/`Message` what triggers and what's sent back, `CooldownSeconds` the per-channel anti-spam floor. Omit `OAuthToken`/`BotUsername`/`Channels` to disable.

Environment-variable form (identical effect; `Channels` is a list, so env vars need index suffixes — see `.env.example`):

```
DISCORD__TOKEN=...            DISCORD__DEBUGGUILDID=123456789012345678
MUSIC__LAVALINKADDRESS=http://127.0.0.1:2333   MUSIC__DEFAULTSEARCHMODE=scsearch
YOUTUBE__APIKEY=...           YOUTUBE__POLLINTERVALSECONDS=180
TWITCH__CLIENTID=...          TWITCH__CLIENTSECRET=...
TELEGRAM__BOTTOKEN=...
TWITCHCHAT__OAUTHTOKEN=...    TWITCHCHAT__BOTUSERNAME=...   TWITCHCHAT__CHANNELS__0=...
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
| `/notify twitch <channel> <target> [pinLive] [mention]` | Watch a Twitch channel (login or URL) for "went live" and post to `<target>`. |
| `/notify telegram <id> [chatId]` | Mirror subscription `<id>` (see `/notify list`) to a Telegram chat id or `@channelusername` too. Omit `chatId` to remove the Telegram target. |
| `/notify template <id> <kind> [text]` | Override the announcement text for one event kind on subscription `<id>`. Placeholders: `{who}` `{title}` `{url}`; a configured role mention is always prepended automatically. Omit `text` to reset to the default persona text. Used by both the Discord and Telegram delivery of that subscription. |
| `/notify list` | List this server's subscriptions (Discord + any Telegram target). |
| `/notify remove <id>` | Remove a subscription. |

Existing videos/live streams are **not** re-announced when you add a subscription — the first poll only records what's already there. A Telegram target needs the bot added to that chat (as admin, for pinning to work) before it can post.

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
  INotificationSource           produces events (YouTube, Twitch)
  INotificationSink             delivers events (Discord, Telegram)
  NotificationDispatcher        hosted service: poll -> dedup -> fan out to sinks
  Sources/YouTubeNotificationSource
  Sources/TwitchNotificationSource
  Sinks/DiscordNotificationSink
  Sinks/TelegramNotificationSink
Twitch/
  TwitchChatBot                 hand-rolled IRC-over-WebSocket "!links" responder
```

The notifier is deliberately source- and sink-agnostic so new platforms are additive.

---

## Extending

The notification pipeline already has two sources (YouTube, Twitch) and two sinks (Discord, Telegram) — adding another of either is the same shape:

- **A source** (e.g. Kick, RSS): implement `INotificationSource` (`SourceType`, `ResolveAsync`, `PollAsync` yielding `ContentEvent`s), register it conditionally in `Program.cs` when its config is present, add a `/notify <platform>` command mirroring `/notify twitch`.
- **A sink** (e.g. a Discord DM, Matrix, Slack): implement `INotificationSink` (`Name`, `AppliesTo`, `DeliverAsync`), add whatever per-subscription target it needs to `NotificationSubscription` (+ an idempotent `ALTER TABLE` in `Program.cs`, following the `TelegramChatId` example), register it conditionally, wire a `/notify <platform>` attach command mirroring `/notify telegram`.

The dispatcher already routes by `SourceType`, fans every event out to **every** applicable sink, and each sink independently decides `PinLiveStreams` — no core changes needed either way.

---

## Notes & limitations

- The database uses `EnsureCreated` (no migrations). If you change the entity model, delete `bot.db` or switch to EF Core migrations.
- One temp channel is created per lobby join and deleted when it empties; a startup sweep cleans up any left over after a crash.
- Music/radio playback needs the Lavalink server reachable; `/play` reports a clear error if a track can't be loaded. Lavalink4NET reconnects automatically if Lavalink restarts.
- **Shorts detection** treats videos ≤ 60 s as Shorts (no official API flag exists); some longer Shorts are announced as regular uploads.
- **YouTube Premieres** that carry live-stream metadata are announced via the live/upcoming path, not the plain-upload path — the API can't distinguish a finished Premiere from a finished live stream.
- **Live pinning** pins each new live announcement; it does not unpin older ones. Watch Discord's 50-pin-per-channel limit on very active channels.
- **Notifier delivery** marks an item "seen" only after every applicable sink succeeds, so a transient outage (Discord or Telegram) is retried next cycle — a failure in one sink re-delivers to **both** next cycle, there's no per-sink dedup.
- **Twitch "went live"** dedups by Twitch's per-broadcast stream id, so ending and restarting a stream correctly re-announces; there's no "went offline" notification (out of scope — only `LiveStarted` is modeled for Twitch).
- **Telegram pinning** needs the bot to be an **admin** of the target chat; a failed pin (missing admin rights) doesn't fail delivery, same as Discord's pin-is-best-effort behavior.
- **Twitch chat responder** is a separate process from the notification pipeline (raw IRC-over-WebSocket, not Helix) and needs a **user** OAuth token for the posting account — different credentials from `Twitch:ClientId`/`ClientSecret`, which can only read the API, not send chat messages. On a bad/expired token it logs the failure and backs off 5 minutes between retries rather than hammering Twitch.
