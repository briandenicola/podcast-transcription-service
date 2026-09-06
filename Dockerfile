# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PodcastTranscription.slnx ./
COPY src/PodcastTranscription.Web/PodcastTranscription.Web.csproj src/PodcastTranscription.Web/
COPY tests/PodcastTranscription.Tests/PodcastTranscription.Tests.csproj tests/PodcastTranscription.Tests/
RUN dotnet restore src/PodcastTranscription.Web/PodcastTranscription.Web.csproj

COPY . .
RUN dotnet publish src/PodcastTranscription.Web/PodcastTranscription.Web.csproj \
    -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# yt-dlp is fetched below as a self-contained binary rather than from apt, whose builds go
# stale within weeks.
# ffmpeg decodes and resamples, curl backs the healthcheck, and gosu lets the entrypoint drop
# privileges after fixing volume ownership.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates gosu \
    && rm -rf /var/lib/apt/lists/*

# The Linux release is a PyInstaller bundle: it carries its own interpreter, so nothing
# Python-shaped ends up on the image. Pin YTDLP_VERSION for reproducible builds.
ARG YTDLP_VERSION=latest
RUN set -eux; \
    if [ "$YTDLP_VERSION" = "latest" ]; then \
        url="https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux"; \
    else \
        url="https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux"; \
    fi; \
    curl -fsSL "$url" -o /usr/local/bin/yt-dlp; \
    chmod 0755 /usr/local/bin/yt-dlp; \
    /usr/local/bin/yt-dlp --version

WORKDIR /app
COPY --from=build /app/publish .

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 0755 /usr/local/bin/docker-entrypoint.sh

# Mount points for the SQLite database and the audio library. A bind mount hides this chown, so
# the entrypoint redoes it at runtime, where it can actually see the mount.
RUN mkdir -p /data /media && chown -R app:app /data /media /app

# No USER here on purpose: the entrypoint starts as root only long enough to take ownership of
# the volumes, then execs the app as an unprivileged user via gosu. Set PUID/PGID to have the
# files owned by a host account instead.

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataPath=/data \
    Storage__MediaPath=/media
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "PodcastTranscription.Web.dll"]
