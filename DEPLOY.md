# Гайд по развёртыванию

Пошагово, для запуска бота с нуля. Два пути: **Docker** (проще, рекомендую) или **вручную** через .NET SDK.

---

## 0. Что понадобится

- **Токен Discord-бота** (обязательно).
- **YouTube Data API v3 ключ** — только если нужен нотификатор про ролики/Shorts/стримы (необязательно).
- Для Docker-пути: установленный **Docker Desktop** (Windows/Mac) или Docker Engine (Linux).
- Для ручного пути: **.NET 9 SDK**, **ffmpeg** и **yt-dlp** в PATH.

---

## 1. Создать бота в Discord и взять токен

1. Открой <https://discord.com/developers/applications> → **New Application** → назови.
2. Слева **Bot** → **Reset Token** → **скопируй токен** (показывается один раз).
3. Токен — это пароль. Никому не показывай, в git не коммить (в проекте он уже в `.gitignore`).

## 2. Пригласить бота на сервер

1. В приложении: **OAuth2** → **URL Generator**.
2. Scopes: отметь **`bot`** и **`applications.commands`**.
3. Bot Permissions отметь: **View Channels, Send Messages, Embed Links, Manage Messages** (для закрепа), **Connect, Speak, Move Members, Manage Channels**.
4. Скопируй получившийся URL внизу, открой в браузере, выбери свой сервер → **Authorize**.

## 3. (Опционально) YouTube API ключ

1. <https://console.cloud.google.com/> → создай проект.
2. **APIs & Services** → **Library** → включи **YouTube Data API v3**.
3. **Credentials** → **Create Credentials** → **API key** → скопируй.
4. Без ключа бот работает, только команды `/notify` скажут, что нотификатор выключен.

## 4. Забрать код

```bash
git clone https://github.com/Er3shk1gal/TwitchDsBot.git
cd TwitchDsBot
```

## 5. Заполнить настройки (`.env`)

```bash
cp .env.example .env
```

Открой `.env` и впиши:

```
DISCORD__TOKEN=токен_из_шага_1
YOUTUBE__APIKEY=ключ_из_шага_3        # можно оставить пустым
# на время настройки удобно указать id своего сервера — команды появятся мгновенно:
DISCORD__DEBUGGUILDID=айди_сервера
```

> **ID сервера**: в Discord включи Settings → Advanced → Developer Mode, потом ПКМ по серверу → **Copy Server ID**. Если оставить `DISCORD__DEBUGGUILDID` пустым — команды регистрируются глобально и появляются в течение ~1 часа.

---

## 6a. Запуск через Docker (рекомендуется)

В образ уже вшиты **ffmpeg** и **yt-dlp** — ставить ничего не надо.

```bash
docker compose up -d --build     # собрать и запустить
docker compose logs -f           # смотреть логи (Ctrl+C — выйти из логов, бот работает)
```

В логах должно появиться `Discord gateway connection established.` — значит подключился.

- **Обновить** после `git pull`: `docker compose up -d --build`
- **Остановить**: `docker compose down`
- **Остановить и стереть базу**: `docker compose down -v`
- База SQLite лежит в томе `bot-data` и переживает перезапуски.

## 6b. Запуск вручную (без Docker)

Поставь инструменты:

```powershell
# Windows (winget)
winget install Microsoft.DotNet.SDK.9
winget install Gyan.FFmpeg
winget install yt-dlp.yt-dlp
```

Затем:

```bash
dotnet run --project src/DiscordBot
```

(`.env` подхватывается автоматически; либо скопируй `src/DiscordBot/appsettings.Example.json` в `appsettings.json` и впиши туда.)

---

## 7. Первичная настройка в Discord

После того как бот онлайн и команды появились, выполни на сервере (нужны права администратора):

1. **Временные войсы (join-to-create):**
   - `/config lobby <голосовой канал>` — канал-лобби: кто зашёл, тому создаётся личный зал.
   - `/config category <категория>` — где создавать залы (необязательно; по умолчанию — рядом с лобби).
   - `/config userlimit 0` — лимит по умолчанию (0 = без предела).
   - Проверка: `/config show`.

2. **Музыка:** сразу работает — зайди в войс, `/play <ссылка или запрос>`. Ограничить ролью: `/config djrole <роль>`.

3. **YouTube-уведомления** (если задан API-ключ):
   - `/notify youtube <канал YouTube> <текстовый канал> [uploads] [shorts] [liveStreams] [pinLive] [mention]`
   - `<канал YouTube>` — ссылка, `@handle` или id (`UC...`).
   - Список: `/notify list`, удалить: `/notify remove <id>`.
   - Старые ролики при добавлении не спамятся — первая синхронизация только запоминает уже вышедшее.

---

## 8. Команды (шпаргалка)

- **Владелец зала:** `/voice limit|name|bitrate|lock|unlock|permit|kick|claim|transfer`
- **Музыка:** `/play /skip /stop /pause /resume /queue /nowplaying /volume /seek /loop /shuffle /leave`
- **Админ:** `/config ...`, `/notify ...`

Бот отвечает в слоге Дон Кихота из Limbus Company (по-русски).

---

## 9. Если что-то не так

| Симптом | Причина / что делать |
| --- | --- |
| В логах `401 Unauthorized` | Неверный `DISCORD__TOKEN`. |
| Слэш-команд нет | Задай `DISCORD__DEBUGGUILDID` (мгновенно) или подожди ~1 час для глобальных. |
| `/play` пишет ошибку резолва | Нет `ffmpeg`/`yt-dlp` в PATH (для Docker неактуально — они внутри). |
| Бот не создаёт временный войс | Не задан `/config lobby`, либо у бота нет прав **Manage Channels / Move Members**. |
| `/notify` говорит, что недоступен | Не задан `YOUTUBE__APIKEY`. |
| Нотификатор молчит | Проверь квоту YouTube API и интервал `YOUTUBE__POLLINTERVALSECONDS` (см. раздел квоты в README). |

Подробности про архитектуру, квоты YouTube и расширение (Twitch/Telegram) — в [README.md](README.md).
