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

# ffmpeg (transcode) + native audio deps for VoiceNext + latest yt-dlp (self-contained linux binary).
# VoiceNext P/Invokes LibraryImport("libsodium") / LibraryImport("libopus"), which the .NET loader
# resolves to the UNVERSIONED libsodium.so / libopus.so. The runtime packages (libsodium23/libopus0)
# only ship libsodium.so.23 / libopus.so.0; the -dev packages add the unversioned symlinks the loader
# needs (and pull the runtime libs in as dependencies).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        libsodium-dev \
        libopus-dev \
        ca-certificates \
        curl \
    && curl -fsSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux \
        -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && apt-get purge -y curl \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Persist the SQLite database outside the container (mounted volume at /app/data).
RUN mkdir -p /app/data
ENV DATABASE__CONNECTIONSTRING="Data Source=/app/data/bot.db" \
    MUSIC__FFMPEGPATH=ffmpeg \
    MUSIC__YTDLPPATH=/usr/local/bin/yt-dlp \
    DOTNET_gcServer=0

ENTRYPOINT ["dotnet", "DiscordBot.dll"]
