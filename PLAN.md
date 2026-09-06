# Podcast Transcription App — Architecture & Backlog

Self-hosted podcast transcription on top of `whisper.cpp`. ASP.NET Core on Linux, Docker,
SQLite. No Python anywhere in the stack.

---

## 1. Architecture

### Topology

```
┌─────────────────────────────────────────────┐
│ docker host                                 │
│                                             │
│  ┌───────────────┐      ┌────────────────┐  │
│  │  transcriber  │─────▶│ whisper.cpp    │  │
│  │  (ASP.NET)    │ HTTP │ whisper-server │  │
│  │               │      │  :8080         │  │
│  │  + ffmpeg     │      └────────────────┘  │
│  │  + yt-dlp     │                          │
│  └───────┬───────┘                          │
│          │                                  │
│    ┌─────▼──────┐   ┌──────────────┐        │
│    │ app.db     │   │ /media       │        │
│    │ (SQLite)   │   │ (audio blobs)│        │
│    └────────────┘   └──────────────┘        │
└─────────────────────────────────────────────┘
```

Two containers, one compose file. The whisper endpoint is a config value
(`Whisper:BaseUrl`), so it can point at a sidecar container **or** back at the Windows
box if that's where the NVIDIA card lives. Decide this early — it's the one thing that
changes the compose file.

- **GPU on the Windows server:** run only the ASP.NET container on the docker host and
  point it at `http://windows-host:8080`. Simplest, uses the hardware you already have.
- **GPU (or CPU) on the docker host:** add the whisper container. The whisper.cpp repo
  ships Dockerfiles for CPU and CUDA variants; build from those rather than hunting for
  Linux binaries, since the project doesn't publish them.

### Stack

| Concern | Choice | Why |
|---|---|---|
| Framework | .NET 10, Blazor with `InteractiveServer` | Realtime UI over SignalR with no separate SPA build or API surface to maintain |
| Persistence | EF Core + SQLite (WAL mode) | One file, trivial backup |
| Search | SQLite FTS5 over transcript segments | Built in, no extra service; query via Dapper since EF doesn't model FTS |
| Queue | Jobs table + `BackgroundService` worker | Survives restarts. A `Channel<T>` alone loses the queue on container restart |
| Media | `ffmpeg` in the image, driven via CliWrap | Decode/resample/segment |
| Ingest | `yt-dlp` in the image | Episode URLs and feeds |
| Logging | Serilog to console + rolling file | Container-friendly |

### Why chunking matters

`whisper-server` takes one request at a time and returns nothing until the whole file is
done. A 90-minute episode is a single blocking call with no progress. So:

1. ffmpeg → 16 kHz mono s16le WAV (the only format the server reliably accepts).
2. Split into ~10-minute segments, cut at the nearest silence via `silencedetect` so words
   aren't severed mid-syllable.
3. POST segments sequentially, offset each segment's timestamps by its start position.
4. Emit progress after each segment. Partial transcripts become visible as they land.

Segment boundaries are also the natural resume point after a crash.

### Word-level timestamps

Request them from the first run — retrofitting means re-transcribing the archive.

`whisper-server` gives sentence-ish segments by default. To get per-word timing, pass
`max_len=1` and `split_on_word=true`, which collapses each "segment" down to a single word
with its own start/end. It's a blunt instrument, but it works through the HTTP API with no
custom build.

The tradeoff: you lose the natural sentence grouping, so rebuild it yourself. Walk the word
list and start a new display segment on terminal punctuation, on a gap longer than ~700 ms,
or after ~200 characters — whichever comes first. Store the grouped result as `Segment.Text`
and the underlying words in `WordsJson`.

Worth having even before diarization:

- Word-accurate seek, and karaoke-style highlighting of the current word during playback.
- Per-word probability from the response, which drives low-confidence shading in the UI —
  the fastest way to find the mangled proper nouns that need correcting.
- The alignment layer speaker attribution will need later. Whisper segments routinely
  straddle a speaker turn; words don't.

Expect ±0.5 s drift near boundaries regardless. Don't build anything that assumes frame
accuracy.

### Concurrency

One worker by default — a second concurrent request to `whisper-server` just queues inside
it and muddies your timing data. Make worker count configurable so you can scale out to
multiple whisper containers later behind round-robin.

---

## 2. Data model

```
Episode      Id, Title, Show, SourceUrl, PublishedAt, DurationSec,
             AudioPath, AudioSha256, CreatedAt

Job          Id, EpisodeId, State, Model, Language, Progress,
             Attempts, LastError, StartedAt, CompletedAt

Transcript   Id, EpisodeId, Model, Language, RawJson, CreatedAt

Segment      Id, TranscriptId, Ordinal, StartMs, EndMs, Text, AvgLogProb,
             WordsJson, IsEdited, SpeakerId?

Feed         Id, Title, RssUrl, LastPolledAt, AutoTranscribe
```

`Job.State`: `Queued | Downloading | Preparing | Transcribing | Completed | Failed | Cancelled`

Two things worth doing now rather than retrofitting:

- **Keep `RawJson`.** The full whisper response, unmodified. Lets you re-derive segments,
  change export formats, or debug accuracy without re-running inference.
- **Hash the audio.** `AudioSha256` catches "did I already do this one" across renamed
  files and re-downloads.
- **Store words as JSON on the segment, not as rows.** `WordsJson` holds
  `[{ t0, t1, text, p }]`. A 90-minute episode is ~13,000 words; as rows that's a table you
  have to index, join, and page. As a JSON column it's one read alongside the text you were
  already fetching, and FTS still indexes `Segment.Text` normally.

`Transcript` is deliberately one-to-many with `Episode` — re-running an episode on a
better model shouldn't destroy the old result.

---

## 3. Things worth building that aren't on your list

Ordered roughly by value-per-hour:

1. **Click-to-seek transcript player.** Audio element + segment list; clicking a line seeks,
   the current line highlights. Cheap to build, and it's the feature that makes the app feel
   finished rather than functional.
2. **Custom vocabulary via initial prompt.** whisper.cpp accepts a `prompt` parameter that
   biases decoding. Feeding it host names, guest names, and recurring jargon per show is the
   single biggest accuracy win available, and it costs nothing at inference time.
3. **Per-show defaults.** Model, language, and prompt attached to a Feed, inherited by its
   episodes. Podcasts are consistent; configure once.
4. **Re-transcribe action.** Model choice recorded per transcript, so you can reprocess the
   back catalogue when a better model ships and compare against the old one.
5. **Inline correction.** Editable segment text with an `IsEdited` flag. Whisper will mangle
   proper nouns; fixing them in place beats exporting and editing elsewhere.
6. **Exports:** SRT, VTT, plain text, JSON, and Markdown with timestamp links.
7. **Auth.** Even a single-admin cookie login. `whisper-server` has none, your app will hold
   a media library, and "it's only on the LAN" ages badly.
8. **Retention policy.** Optionally delete source audio after successful transcription —
   transcripts are kilobytes, podcast audio is not.
9. **Submission API.** `POST /api/episodes` with a URL or file, so cron jobs and other tools
   can feed it without the UI.

Explicitly **out of scope for v1**: speaker diarization — see §7 for the deferred design.
Word timestamps are in scope from M2, which is what keeps that door open.

---

## 4. Backlog

### M1 — Walking skeleton
> *Done when: upload an MP3, get a transcript, see it in the browser.*

- [x] **1.1** Solution scaffold: Blazor Server app, Serilog, options binding for `Whisper:BaseUrl`
- [x] **1.2** Dockerfile with ffmpeg + yt-dlp; compose file with app + whisper.cpp; volumes for `/data` and `/media`
- [x] **1.3** EF Core + SQLite, WAL mode, migrations run on startup
- [x] **1.4** `WhisperClient`: typed `HttpClient` posting multipart to `/inference`, `response_format=json`, generous timeout, deserialize to a result record
- [x] **1.5** `AudioProcessor`: ffmpeg probe for duration; transcode to 16 kHz mono WAV
- [x] **1.6** Upload page → Episode row + audio saved to `/media`
- [x] **1.7** Synchronous end-to-end transcribe (no queue yet), persist Transcript + Segments
- [x] **1.8** Transcript view: timestamped segment list

### M2 — Queue and realtime status
> *Done when: queue five episodes, watch them progress live, restart the container mid-run and have it recover.*

- [x] **2.1** Job table + state machine; enqueue on upload
- [x] **2.2** `TranscriptionWorker : BackgroundService` — claim oldest queued job, honour configurable concurrency
- [x] **2.3** Silence-aware segmentation; per-segment POST with timestamp offsetting
- [x] **2.3a** Request word timestamps (`max_len=1`, `split_on_word=true`); offset every word by chunk start
- [x] **2.3b** Regroup words into display segments on punctuation / ~700 ms gap / ~200 chars; persist `WordsJson` + `AvgLogProb`
- [x] **2.4** Progress reporting per segment; persist partial segments as they complete
- [x] **2.5** SignalR/Blazor push of job state to any connected client
- [x] **2.6** Job list page: state, progress bar, elapsed, realtime-factor, cancel button
- [x] **2.7** Retry with exponential backoff; `Attempts` cap; `LastError` surfaced in UI
- [x] **2.8** Startup recovery — reset jobs orphaned in a running state
- [x] **2.9** Resume from last completed segment rather than restarting the episode

### M3 — Library and search
> *Done when: search across the whole archive returns highlighted hits that jump to the right timestamp.*

- [x] **3.1** FTS5 virtual table over segments, kept in sync by trigger
- [x] **3.2** Search page: query box, `snippet()` highlighting, grouped by episode
- [x] **3.3** Result click → transcript at that timestamp
- [x] **3.4** Library page: filter by show/date/state, sort, paginate
- [x] **3.5** Audio player with click-to-seek and active-line highlight
- [x] **3.5a** Word-level highlight during playback; click any word to seek to it
- [x] **3.5b** Low-confidence shading from per-word probability, with a toggle
- [x] **3.6** Exports: SRT, VTT, TXT, JSON, Markdown

### M4 — Ingest
> *Done when: subscribe to a feed and new episodes transcribe themselves overnight.*

- [x] **4.1** URL ingest via yt-dlp (single episode)
- [x] **4.2** RSS feed parsing and subscription management
- [x] **4.3** Scheduled poller for new episodes; `AutoTranscribe` per feed
- [x] **4.4** SHA-256 dedup on ingest
- [x] **4.5** Per-show defaults: model, language, custom prompt
- [x] **4.6** Backfill: enqueue the last N episodes of a feed

### M5 — Polish
- [x] **5.1** Cookie auth, single admin, config-supplied credentials
- [x] **5.2** Inline segment editing with `IsEdited` flag
- [x] **5.3** Re-transcribe with a different model; side-by-side compare
- [x] **5.4** Settings page: models, worker count, retention, whisper endpoint health
- [x] **5.5** `/healthz` including whisper reachability
- [x] **5.6** Retention job for source audio
- [x] **5.7** `POST /api/episodes` with API-key auth
- [x] **5.8** Nightly SQLite backup (`VACUUM INTO`)

### Later
- Speaker diarization and enrollment — deferred, designed in §7
- LLM-generated summaries and chapter markers
- Multiple whisper backends with round-robin dispatch

---

## 5. Decisions made

1. **Where does whisper run** — the Windows box with the GPU. Compose runs the app container
   only; `Whisper:BaseUrl` (env `Whisper__BaseUrl`) points at `whisper-server` there. Adding a
   sidecar later is a compose change and nothing else.
2. **Model default** — `large-v3-turbo-q5_0`. It has to match whatever `whisper-server` was
   started with, since the model is loaded at server startup, not per request.
3. **Segment length** — 10 minutes, to be tuned once M2 reports realtime factors.
4. **Audio retention** — keep both the source file and the prepared 16 kHz WAV. Re-transcribes
   and the deferred diarization pass both read the WAV rather than decoding again. M5.6 can
   prune later if the library outgrows the disk.

5. **Feed polling takes new episodes only.** Subscribing records the existing catalogue
   without queueing it — subscribing to a show with ten years of history should not enqueue ten
   years of audio. Backfill (4.6) is the deliberate way to pull history.

6. **Authentication is on by default and fails closed.** The app refuses to start with auth
   enabled and no password configured, rather than either locking the operator out or quietly
   serving the library to anyone who can reach it.

Two corrections that surfaced while building M1:

- `response_format=json` returns only a flat `text` field. Segment timings and `avg_logprob`
  need `verbose_json`, which is what `WhisperClient` asks for.
- SQLite will not `ORDER BY` a `DateTimeOffset`. A value converter stores them as Unix
  milliseconds; the domain model is unchanged.

And one from M2:

- **`RawJson` moved off `Transcript` onto a new `TranscriptChunk` row.** Chunking means there
  is no single whisper response for an episode. Keeping one raw body per posted chunk preserves
  the intent — segments can still be re-derived without re-running inference — and means a job
  that dies halfway does not lose the raw output of the chunks that already succeeded.

---

## 6. Notes for implementation

- Set `HttpClient.Timeout` explicitly and high. The default 100 s will bite you on the first
  long segment.
- SQLite has a single writer. Keep the worker's writes short and batched; don't hold a
  transaction open across an inference call.
- Run ffmpeg with `-nostdin`, and always read stdout/stderr or the process will deadlock on a
  full pipe.
- Store timestamps as integer milliseconds, not floats or strings. Every export format and
  seek operation gets easier.
- Log realtime factor (audio seconds ÷ wall seconds) per job. It's the number you'll want when
  deciding about model size or hardware.

---

## 7. Deferred: speakers

Not in v1. Recorded here so the choices that block it don't get made by accident.

### What whisper.cpp can't do

- `--diarize` compares stereo channels and attributes speech to the louder one. Fine for a
  two-mic interview on separate channels, useless for a downloaded mixdown.
- `--tinydiarize` (`-tdrz`) needs the `ggml-small.en-tdrz.bin` checkpoint — English-only,
  small-model accuracy — and emits `[SPEAKER_TURN]` markers. It detects *that* the speaker
  changed, never *who*. No clustering, so the same person can't be tracked across an episode.
  Landed in 2023 and effectively unmaintained.

Neither is a foundation worth building on.

### The approach when we do it

**sherpa-onnx** offline speaker diarization: pyannote segmentation model + speaker embedding
extractor + clustering, all ONNX Runtime, CPU-viable, no Python. C API with a C# binding; if
the NuGet doesn't surface the diarization types, the C surface is small enough to P/Invoke.

It slots into the existing pipeline without disturbing it:

1. Feed the diarizer the same 16 kHz mono WAV the transcription pipeline already produces.
   Full episode, not chunked — clustering needs to see the whole file to be consistent.
2. It returns speaker-labeled time ranges.
3. Assign each **word** to the range it overlaps most, then group runs of same-speaker words
   into speaker blocks. This is the step that needs `WordsJson`.

Set `numClusters` when the count is known (two hosts plus a guest covers most shows),
otherwise use threshold-based auto-detection.

### Speaker enrollment

The feature that makes it worth doing. sherpa-onnx also does speaker identification from
stored embeddings, so enrolling regular hosts once resolves them by name across the whole
back catalogue, leaving only guests as anonymous clusters.

```
Speaker      Id, Name, EmbeddingBlob, FeedId?, CreatedAt
```

Then "every episode where Kara talks about pricing" becomes a real query.

### Backlog sketch

- [ ] **7.1** sherpa-onnx interop wrapper; model files baked into the image or fetched on first run
- [ ] **7.2** Diarization pass over the full WAV, persisted as speaker ranges
- [ ] **7.3** Word→speaker overlap assignment; group into speaker blocks
- [ ] **7.4** Speaker blocks in the transcript view: colour per speaker, gutter labels
- [ ] **7.5** `Speaker` table + enrollment from a selected transcript block
- [ ] **7.6** Auto-match new episodes against enrolled embeddings; manual override
- [ ] **7.7** Speaker names in SRT/VTT/Markdown exports; filter search by speaker

### Don't foreclose it

- Word timestamps in M2 — the one genuinely hard-to-retrofit dependency.
- Keep the prepared 16 kHz WAV, or make the retention policy able to regenerate it.
- `Segment.SpeakerId` nullable in the schema now, unused until 7.3.
