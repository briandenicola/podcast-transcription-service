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

# yt-dlp and ffmpeg are both fetched below as self-contained static binaries rather than from
# apt. apt's ffmpeg pulls in ~180 packages (fontconfig, libsdl2, X11 libraries, audio backends)
# that a headless service never touches, and reinstalling that whole dependency tree from
# scratch - which happens whenever this base image's digest moves and evicts the layer cache -
# can take twenty-plus minutes on a slow mirror. xz-utils is the one apt package still needed,
# to unpack the static ffmpeg tarball; curl backs the healthcheck and the downloads themselves;
# gosu lets the entrypoint drop privileges after fixing volume ownership.
RUN apt-get update \
    && apt-get install -y --no-install-recommends xz-utils curl ca-certificates gosu \
    && rm -rf /var/lib/apt/lists/*

# johnvansickle.com's "release" build is the latest stable static build, not a specific pinned
# version - there is no per-version URL to pin to, so integrity is checked against the vendor's
# own md5 instead. ffprobe ships in the same tarball and is used to measure episode duration.
RUN set -eux; \
    curl -fsSL https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz -o /tmp/ffmpeg-release-amd64-static.tar.xz; \
    curl -fsSL https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz.md5 -o /tmp/ffmpeg-release-amd64-static.tar.xz.md5; \
    (cd /tmp && md5sum -c ffmpeg-release-amd64-static.tar.xz.md5); \
    mkdir -p /tmp/ffmpeg-extract; \
    tar -xf /tmp/ffmpeg-release-amd64-static.tar.xz -C /tmp/ffmpeg-extract --strip-components=1; \
    mv /tmp/ffmpeg-extract/ffmpeg /tmp/ffmpeg-extract/ffprobe /usr/local/bin/; \
    chmod 0755 /usr/local/bin/ffmpeg /usr/local/bin/ffprobe; \
    rm -rf /tmp/ffmpeg-release-amd64-static.tar.xz /tmp/ffmpeg-release-amd64-static.tar.xz.md5 /tmp/ffmpeg-extract; \
    /usr/local/bin/ffmpeg -version; \
    /usr/local/bin/ffprobe -version

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
