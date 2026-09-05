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

# ffmpeg decodes and resamples; curl backs the healthcheck. yt-dlp is fetched below as a
# self-contained binary rather than from apt, whose builds go stale within weeks.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates \
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

# Mount points for the SQLite database and the audio library.
RUN mkdir -p /data /media && chown -R app:app /data /media /app
USER app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataPath=/data \
    Storage__MediaPath=/media
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "PodcastTranscription.Web.dll"]
