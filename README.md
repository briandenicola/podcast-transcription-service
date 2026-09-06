# Podcast Transcription Service

Self-hosted podcast transcription on top of [whisper.cpp](https://github.com/ggml-org/whisper.cpp).
ASP.NET Core 10 (Blazor Server), SQLite, Docker. No Python in the stack.

The app does the ingest, chunking, storage and UI. Inference happens in `whisper-server`,
which runs wherever the GPU is — for this deployment, the Windows box. The app reaches it
over HTTP at `Whisper:BaseUrl`.

See [PLAN.md](PLAN.md) for the architecture and full backlog.

## Status

**M5 — complete.** All five milestones in [PLAN.md](PLAN.md) are done: upload or subscribe,
transcribe on a queue that survives restarts, search the archive, play it back, correct it, and
export it. Speaker diarization is deliberately out of scope — see §7 of the plan for the design
that the word timestamps keep possible.

### Authentication

A single admin account, on by default. whisper-server has no authentication of its own and this
app holds a media library, so the app **refuses to start** with auth enabled and no password
configured — better than either locking you out or quietly serving the library to the network.

Set `ADMIN_PASSWORD` to get going, then generate a PBKDF2 hash on the settings page and move it
to `ADMIN_PASSWORD_HASH`, so the password itself is not readable in the environment. Everything
is protected: pages, audio streaming and exports alike, via a fallback authorization policy
rather than page-by-page opt-in. Only `/healthz`, the login page and static assets are open.

### Submission API

`POST /api/episodes` with an `X-API-Key` header, so cron jobs and other tools can feed it without
the UI. Disabled entirely until `API_KEY` is set — an API with no key is not an API worth
exposing.

```bash
curl -X POST http://localhost:8080/api/episodes \
  -H "X-API-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/episode.mp3","show":"The Build Log"}'
```

`GET /api/episodes/{id}` returns the transcripts and the latest job, which is enough to poll for
completion.

### Corrections and comparison

Whisper mangles proper nouns. The **Edit** toggle turns each line into a text box; a corrected
segment is flagged and the FTS index follows it inside SQLite, so search reflects the fix
immediately. Word timings are dropped on edit, since they no longer describe the text.

Re-transcribing records the model against each transcript, so the back catalogue can be
reprocessed when a better model ships. **Compare** puts two runs side by side.

### Retention and backups

A maintenance worker runs nightly. Retention is **off by default** — deleting the original is not
reversible, and re-transcribing on a better model needs it. When enabled it only ever prunes
source audio, only for episodes with a finished transcript past a grace period, and never when
that would leave an episode with no audio at all. The prepared 16 kHz WAV is kept, so playback
falls back to it and diarization stays possible.

Backups use `VACUUM INTO`, which writes a consistent, compacted copy while the app keeps
running — unlike copying the file, which can catch it mid-write.

### Feeds

A poller checks each subscribed feed on a timer and takes **new episodes only**. Subscribing
records the existing catalogue without queueing it — subscribing to a show with ten years of
history should not enqueue ten years of audio. **Backfill** on a feed is the deliberate way to
pull history: it queues the most recent recorded episodes that have never been transcribed.

You can paste an **Apple Podcasts link** rather than hunting for the RSS URL — the show id is
exchanged for the real feed through Apple's public lookup API. Anything else is treated as a feed
URL directly.

Items are matched on the feed's own `<guid>`, falling back to the enclosure URL for feeds that
omit one, so re-polling never re-adds an episode. Once audio is downloaded it is hashed, and a
byte-identical match against something already in the library fails the job immediately rather
than transcribing the same audio twice.

Downloads happen inside the job rather than at ingest time, so they queue, retry and report
progress like everything else instead of blocking whoever pasted the link.

### Per-show defaults

Model, language and prompt attach to a feed and are inherited by every episode it produces.
The prompt is the cheapest accuracy win available — host and regular guest names plus recurring
jargon cost nothing at inference time and stop whisper mangling them. The prompt used is recorded
on each job, so two runs can be compared knowing what biased each.

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

### Uploads

Uploading is an ordinary form post rather than a Blazor `InputFile`. Streaming a 2 GB episode
over the SignalR circuit is slow, and it fails silently whenever the circuit has not connected —
the page looks fine and the button does nothing. A plain multipart POST is faster for large files
and works with no JavaScript running at all. `Storage:MaxUploadMb` raises both the Kestrel and
form-parser body limits to match.

### Interface

The UI is themed as Windows 95 — beveled surfaces, a title bar, a status bar and a taskbar. It is
layered over Bootstrap rather than replacing it, so Bootstrap still handles the grid and spacing
and every page's markup is unchanged. The whole look comes from four greys: a raised edge is
light on the top-left and dark on the bottom-right, and a sunken one is that inverted.

The status bar's right-hand panel reports whether the browser could reach the Blazor circuit.
Interactive controls need one, and when it fails to connect the page still renders while buttons
do nothing — indistinguishable from a bug unless something says so.

Uploading shows a progress bar and an hourglass while the file transfers. That is progressive
enhancement over the plain form post: a full episode is tens or hundreds of megabytes, and
without it the browser sits on the form with no sign anything is happening — indistinguishable
from a broken button. If the script does not run, the form still posts.

Destructive and one-shot actions deliberately do not need that circuit. Uploading, subscribing to
a feed and every delete are ordinary form posts, so they work even where the websocket does not.

### Deleting things

Three levels, because they mean different things. Deleting a **job** removes only its history
row, leaving the transcript it produced. Deleting a **transcript** throws away one run — the
episode and its audio stay, so it can be transcribed again. Deleting an **episode** removes the
transcripts, the job history and the audio, and cannot be undone.

Every delete goes through a confirmation page that spells out what will go — how many
transcripts, how many segments, whether the audio is included. That page is statically rendered
and the delete itself is a form post, so a destructive action never depends on a websocket having
connected. Anything with a job still running is refused with a note to cancel it first, rather
than deleting rows out from under the worker.

Segments are always removed with an explicit statement rather than left to a foreign-key
cascade: the search index is kept in step by a trigger on that table, and whether SQLite fires
triggers for cascaded rows depends on a pragma. Relying on it would leave an index still
returning hits for episodes that are gone.

### When whisper gets stuck

Whisper sometimes latches onto a phrase and emits it over and over to the end of a chunk. It is a
decoder failure, not a fault in the audio, and it produces a transcript that looks plausible
line by line while being useless in aggregate.

The decoding parameters above are sent explicitly on every request rather than left to whatever
the server was started with, because those are exactly the settings that prevent it — temperature
fallback, the entropy and log-probability thresholds that trigger it, and carrying no context
between windows so a loop cannot feed itself.

When one happens anyway, the app notices: a finished transcript with a long run of identical
consecutive lines is flagged on the episode page with a Re-transcribe button. The transcript is
still kept — the audio may genuinely repeat, and discarding one on a heuristic would be worse
than flagging it.

If it recurs on a particular episode, try a different model. Loops are model-specific often
enough that switching is the fastest fix, and the model is recorded per transcript so the two
runs can be compared.

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
| `Whisper:TemperatureIncrement` | `0.2` | Whisper's own defence against repetition loops: how much to raise the temperature when a decode looks degenerate. Zero disables the fallback. |
| `Whisper:EntropyThreshold` | `2.4` | Entropy above which a decode is retried. Repetitive text compresses well, so a loop shows up here first. Lower is more aggressive. |
| `Whisper:LogProbThreshold` | `-1.0` | Average log probability below which a decode is retried. |
| `Whisper:NoSpeechThreshold` | `0.6` | Above this, a window is treated as silence. |
| `Whisper:MaxContext` | `0` | Tokens carried into the next window. Carried text is what lets a loop feed itself. |
| `Whisper:SuppressNonSpeechTokens` | `true` | Music and applause are where hallucinations usually start. |
| `Whisper:BeamSize` / `BestOf` | `5` | Beam search is slower and markedly less prone to looping. |
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

| `Ingest:PollFeeds` | `true` | Set false to stop the poller; feeds can still be polled by hand. |
| `Ingest:PollIntervalMinutes` | `60` | How often each feed is checked. |
| `Ingest:QueueExistingItemsOnSubscribe` | `false` | Leave off unless subscribing really should enqueue the whole back catalogue. |
| `Ingest:MaxItemsPerPoll` | `50` | Cap on what one poll accepts from a single feed. |
| `Ingest:DownloadTimeoutMinutes` | `60` | Gives up on a download that hangs. |
| `Auth:Enabled` | `true` | Turn off only when something in front of the app already authenticates. |
| `Auth:Username` | `admin` | |
| `Auth:PasswordHash` | _(none)_ | Preferred. Generate it on the settings page. |
| `Auth:Password` | _(none)_ | Plaintext fallback; the app logs a warning when it is used. |
| `Auth:ApiKey` | _(none)_ | Unset leaves `POST /api/episodes` disabled rather than unauthenticated. |
| `Maintenance:DeleteSourceAfterTranscription` | `false` | Prune source audio once an episode has a finished transcript. |
| `Maintenance:DeleteSourceAfterDays` | `30` | Grace period before source audio is eligible. |
| `Maintenance:BackupsToKeep` | `7` | Nightly `VACUUM INTO` backups retained. |

`GET /media/episodes/{id}/audio` serves the source audio with range requests enabled, which is
what lets the player seek without downloading the whole episode.

### Swapping models

`whisper-server` loads its model at startup and exposes no way to ask which one, so restarting it
with a different `-m` is the only way to change model, and `WHISPER_MODEL` must be updated to
match — it is recorded against every transcript, and a wrong value silently mislabels a
comparison between two runs.

Loading a multi-gigabyte model takes a while, and the server reports `loading model` on its
`/health` route in the meantime. The worker holds the queue while that is true rather than
claiming a job that would fail and spend one of its retries, `/healthz` answers `starting`
rather than `degraded`, and the library banner says so.

`GET /healthz` reports whether `whisper-server` and the database are reachable, and how many jobs
are queued; it answers 503 when anything is down. It is deliberately anonymous — the container
healthcheck and any external monitor cannot log in — and reports reachability, never configuration.

## Running with Docker

```bash
cp .env.example .env    # then set WHISPER_BASE_URL to the GPU box
docker compose up -d --build
```

The UI is on `http://<docker-host>:8080`. `./data` and `./media` are bind-mounted, so the
database and audio library survive `docker compose down`.

The container starts as root only long enough to take ownership of those two directories, then
drops to an unprivileged user for the rest of its life. A bind mount replaces whatever the image
had at that path, so the ownership set at build time is masked the moment `./data` is mounted —
fixing it at runtime is the only place that can see the mount. Set `PUID`/`PGID` to your own
account (`id -u` / `id -g`) if you want to read and edit those files directly on the host.

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
  Services/Ingest/    yt-dlp downloads, RSS parsing, feed polling
  Services/Security/  Password hashing and the single admin account
  Services/Maintenance/ Retention and database backups
  Endpoints/      Audio streaming and transcript export
  Components/     Blazor pages and layout
tests/PodcastTranscription.Tests
```

Migrations are applied on startup, and SQLite is put into WAL mode so the UI can read while
the worker writes.

## CI

`Quality` builds and tests on every push and pull request, and fails on high or critical NuGet
advisories including transitive ones. `CodeQL` scans C# on push, on pull request, and weekly.
`Build and Push to Docker Hub` needs `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository
secrets before it will succeed.

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

Feed XML is parsed with DTD processing prohibited and no external resolver. Feeds are fetched
from wherever a user pointed us, so entity expansion and external entity attacks are closed off
rather than trusted. Ingest URLs are restricted to http and https: arguments reach yt-dlp as an
array rather than through a shell, so there is no command injection, but yt-dlp itself understands
schemes like `file:` that would otherwise read the server's own disk.

Runtime directories default to `var/data` and `var/media` rather than `data/` and `media/`,
because macOS volumes are typically case-insensitive and `data/` would collide with the
source folder `Data/`.
