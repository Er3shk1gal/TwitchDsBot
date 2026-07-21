# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first (layer-cached while only source changes).
COPY nuget.config ./
COPY src/DiscordBot/DiscordBot.csproj src/DiscordBot/
RUN dotnet restore src/DiscordBot/DiscordBot.csproj

# Build & publish.
COPY src/ src/
RUN dotnet publish src/DiscordBot/DiscordBot.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime

# Pure-.NET bot now — audio (and the Discord voice connection) is handled by the separate Lavalink
# container, so no ffmpeg / yt-dlp / libsodium / libopus are needed here.
WORKDIR /app
COPY --from=build /app ./

# Persist the SQLite database outside the container (mounted volume at /app/data).
RUN mkdir -p /app/data
ENV DATABASE__CONNECTIONSTRING="Data Source=/app/data/bot.db" \
    DOTNET_gcServer=0

ENTRYPOINT ["dotnet", "DiscordBot.dll"]
