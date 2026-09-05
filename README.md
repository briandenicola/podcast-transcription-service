# Podcast Transcription Service

Self-hosted podcast transcription on top of [whisper.cpp](https://github.com/ggml-org/whisper.cpp).
ASP.NET Core 10 (Blazor Server), SQLite, Docker. No Python in the stack.

The app does the ingest, chunking, storage and UI. Inference happens in `whisper-server`,
which runs wherever the GPU is — for this deployment, the Windows box. The app reaches it
over HTTP at `Whisper:BaseUrl`.

See [PLAN.md](PLAN.md) for the architecture and full backlog.

## Status

**M1 — walking skeleton.** Upload an MP3, transcribe it, read the transcript in the browser.
The transcribe call is synchronous and un-chunked; the job queue, silence-aware chunking,
word timestamps, search and feed ingest are M2 onwards.

## Configuration

Every setting can come from `appsettings.json` or from the environment, where `:` becomes `__`
(so `Whisper:BaseUrl` is `Whisper__BaseUrl`). The container sets them via `docker-compose.yml`
and `.env`.

| Setting | Default | Notes |
|---|---|---|
| `Whisper:BaseUrl` | `http://localhost:8080` | **The one that has to be right.** Where `whisper-server` is listening. |
| `Whisper:Model` | `large-v3-turbo-q5_0` | Must match what `whisper-server` was started with. Recorded against each transcript so the back catalogue can be reprocessed and compared. |
| `Whisper:Language` | `auto` | ISO code, or `auto` to detect. |
| `Whisper:Prompt` | _(none)_ | Biases decoding toward names and jargon. The cheapest accuracy win available. |
| `Whisper:RequestTimeoutMinutes` | `120` | The `HttpClient` default of 100 seconds fails on any real episode. |
| `Storage:DataPath` | `var/data` | Holds `app.db` and rolling logs. `/data` in the container. |
| `Storage:MediaPath` | `var/media` | Source audio and prepared 16 kHz WAVs. `/media` in the container. |
| `Storage:MaxUploadMb` | `2048` | Upload ceiling. |
| `MediaTools:FfmpegPath` | `ffmpeg` | Also `FfprobePath`, `YtDlpPath`. Baked into the image. |

`GET /healthz` reports whether `whisper-server` is reachable; it answers 503 when it is not.

## Running with Docker

```bash
cp .env.example .env    # then set WHISPER_BASE_URL to the GPU box
docker compose up -d --build
```

The UI is on `http://<docker-host>:8080`. `./data` and `./media` are bind-mounted, so the
database and audio library survive `docker compose down`.

On the Windows machine, start whisper-server and let the firewall through on that port:

```
whisper-server.exe -m models\ggml-large-v3-turbo-q5_0.bin --host 0.0.0.0 --port 8080
```

## Running locally

Needs the .NET 10 SDK, plus `ffmpeg` and `ffprobe` on `PATH` (`brew install ffmpeg`).

```bash
dotnet run --project src/PodcastTranscription.Web
```

Set the whisper endpoint for a local run without editing the file:

```bash
Whisper__BaseUrl=http://192.168.1.10:8080 dotnet run --project src/PodcastTranscription.Web
```

Tests — they stub `whisper-server` over HTTP and use a real in-memory SQLite database, so
neither ffmpeg nor a GPU is needed:

```bash
dotnet test
```

## Layout

```
src/PodcastTranscription.Web
  Domain/         Episode, Job, Transcript, Segment, Feed
  Data/           AppDbContext, migrations, startup bootstrap
  Services/       WhisperClient, AudioProcessor, EpisodeImporter, TranscriptionService
  Components/     Blazor pages and layout
tests/PodcastTranscription.Tests
```

Migrations are applied on startup, and SQLite is put into WAL mode so the UI can read while
the worker writes.

## Two places the code departs from PLAN.md

- **`response_format=verbose_json`, not `json`.** whisper-server's plain `json` returns a
  single flat `text` field with no segments, timings or `avg_logprob` — nothing to build a
  transcript from. `verbose_json` is the OpenAI-shaped response that carries them.
- **Timestamps are stored as Unix milliseconds.** SQLite refuses to `ORDER BY` a
  `DateTimeOffset`, so a value converter maps them to sortable integers. The domain model
  still speaks `DateTimeOffset`.

Runtime directories default to `var/data` and `var/media` rather than `data/` and `media/`,
because macOS volumes are typically case-insensitive and `data/` would collide with the
source folder `Data/`.
