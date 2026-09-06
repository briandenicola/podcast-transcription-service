# Podcast Transcription Service

Self-hosted podcast transcription on top of [whisper.cpp](https://github.com/ggml-org/whisper.cpp).
ASP.NET Core 10 (Blazor Server), SQLite, Docker. No Python in the stack.

The app does the ingest, chunking, storage and UI. Inference happens in `whisper-server`,
which runs wherever the GPU is — for this deployment, the Windows box. The app reaches it
over HTTP at `Whisper:BaseUrl`.

See [PLAN.md](PLAN.md) for the architecture and full backlog.

## Status

**M3 — library and search.** The archive is searchable, transcripts play back against the audio,
and they export in five formats. Feed ingest is M4; auth, inline editing and the submission API
are M5.

How a job runs:

1. ffmpeg transcodes the source to 16 kHz mono WAV — the only format whisper-server reliably
   accepts — and that WAV is kept for re-transcribes and, later, diarization.
2. `silencedetect` finds the pauses, and the episode is cut into ~10-minute chunks at the
   silence nearest each boundary, so words are not severed mid-syllable.
3. Each chunk is posted on its own with `max_len=1` and `split_on_word=true`, which collapses
   every segment down to a single word with its own timing. Each word is offset by its chunk's
   start position, then the words are regrouped into readable segments on terminal punctuation,
   a pause over 700 ms, or roughly 200 characters.
4. Segments are persisted as each chunk lands, so a transcript is readable — and marked
   partial — long before the episode finishes.

That last point is the reason for chunking at all: whisper-server returns nothing until a whole
request completes, so a 90-minute episode posted whole is one blocking call with no progress and
nothing to show for it if the container stops.

Restarting mid-episode is safe. Jobs left in a working state are re-queued on startup and resume
from the last completed chunk against the stored chunk plan, so the cuts land in exactly the same
places. Failures retry with exponential backoff up to the attempt cap, then surface the error on
the jobs page with a Retry button.

### Search

SQLite FTS5 indexes segment text through an external-content table, kept in step by triggers, so
the index cannot drift from the transcripts no matter what writes them. Results are ranked by
bm25, grouped by episode, and each hit links straight to that moment in the audio.

Search input is never passed to FTS5 as syntax. Every term is quoted, which turns operators like
`OR` and `NEAR` into literals and stops a stray bracket being a syntax error rather than a search.
The last word matches as a prefix, and a quoted phrase stays together.

### Playback

The transcript is a player: clicking a line seeks to it and the current line highlights as the
audio moves. Two toggles go further — **Words** renders each word separately so the current word
highlights and any word can be clicked to seek, and **Confidence** shades words by the probability
whisper reported, which is the fastest way to find mangled proper nouns.

Words are off by default because an hour of audio is roughly 13,000 spans, and the highlighting
itself runs in the browser rather than over the Blazor circuit: `timeupdate` fires several times a
second, and a network round trip per tick would lag the audio for no benefit.

### Exports

`SRT`, `WebVTT`, plain text, JSON and Markdown, from the Export menu on any transcript or directly
from `/episodes/{id}/transcripts/{transcriptId}/export/{format}`. The JSON export carries the word
timings and per-word probabilities.

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
| `Transcription:WorkerCount` | `1` | A second concurrent request to one whisper-server only queues inside it and makes the realtime numbers meaningless. Raise it when there are several backends. |
| `Transcription:ChunkSeconds` | `600` | Target chunk length. Tune against the realtime factor the jobs page reports. |
| `Transcription:SilenceSearchWindowSeconds` | `90` | How far from a boundary to look for a pause to cut on. |
| `Transcription:SilenceNoiseDb` | `-30` | Anything quieter counts as silence. |
| `Transcription:WordTimestamps` | `true` | Word timing is the one thing that is painful to retrofit. |
| `Transcription:WordGapMs` | `700` | A longer pause starts a new display segment. |
| `Transcription:MaxSegmentChars` | `200` | A segment is closed once it runs this long. |
| `Transcription:MaxAttempts` | `3` | Then the job is left Failed with its error shown. |
| `Transcription:RetryBaseSeconds` | `30` | First retry delay; doubles each attempt. |

`GET /media/episodes/{id}/audio` serves the source audio with range requests enabled, which is
what lets the player seek without downloading the whole episode.

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
  Services/       WhisperClient, AudioProcessor, EpisodeImporter, JobQueue,
                  TranscriptionPipeline, TranscriptionWorker
  Services/Chunking/  Silence parsing and the chunk planner
  Services/Search/    FTS5 query building, ranking and highlighting
  Services/Export/    SRT, VTT, TXT, JSON and Markdown rendering
  Endpoints/      Audio streaming and transcript export
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

- **The raw whisper responses live on `TranscriptChunk`, not on `Transcript`.** Chunking means
  there is no single response per episode. One raw body per posted chunk keeps the intent —
  segments can be re-derived without re-running inference — and a job that dies halfway keeps
  the raw output of the chunks that already succeeded.

Runtime directories default to `var/data` and `var/media` rather than `data/` and `media/`,
because macOS volumes are typically case-insensitive and `data/` would collide with the
source folder `Data/`.
