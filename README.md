# SpeechToTextPipeline (.NET 8)

A console application that takes an audio or video file, converts the speech to text **locally and for free** with
[Whisper.net](https://github.com/sandrohanea/whisper.net) (OpenAI Whisper running on whisper.cpp), and processes the
transcript into a structured JSON document that downstream systems can consume.

No API keys and no cloud costs. It works offline after a one-time model download.

```
 Input file ─► Validate ─► Normalize ─► Transcribe ─► Process (chain) ───────────────────────► Sink
 (.wav .mp3    type,        16 kHz mono   Whisper.net   1. normalize-text   fillers, stutters     JSON + TXT
  .m4a .mp4)   size         PCM WAV       (or Mock)     2. redact-pii       email, phone, card    (replaceable with
                            ffmpeg or                   3. extract-keywords                       Service Bus,
                            NAudio                      4. analyze-sentiment                      Cosmos DB, ...)
                                                        5. detect-action-items  owner, due date
```

---

## Contents

1. [Quick start](#quick-start)
2. [Usage and configuration](#usage-and-configuration)
3. [Output contract](#output-contract)
4. [Design decisions](#design-decisions)
5. [Handling concurrent uploads and scaling](#handling-concurrent-uploads-and-scaling)
6. [Trade-offs and known limitations](#trade-offs-and-known-limitations)
7. [Project structure](#project-structure)
8. [Testing](#testing)
9. [Troubleshooting (Windows)](#troubleshooting-windows)
10. [Next steps for production](#next-steps-for-production)

---

## Quick start

**Prerequisites**
- [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer (the app rolls forward to .NET 9/10).
- **ffmpeg** on PATH for anything other than `.wav` (`winget install Gyan.FFmpeg`, `brew install ffmpeg`,
  `apt install ffmpeg`). Restart your terminal or IDE afterwards so it sees the new PATH.
- Windows only: the [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe),
  which Whisper.net's native library needs.

```bash
# 1. Run the tests (no model, no ffmpeg needed)
dotnet test

# 2. Try the whole pipeline instantly with the mock engine
dotnet run --project src/TranscriptionPipeline -- samples/mock-transcript.txt --mock

# 3. Real transcription (downloads ggml-base.en, about 140 MB, on the first run)
dotnet run --project src/TranscriptionPipeline -- samples/meeting-sample.wav

# 4. Interactive mode: run with no file, then paste or drag-and-drop paths one at a time
dotnet run --project src/TranscriptionPipeline
```

Each run writes two files to `./output/`:
- `<name>_<timestamp>_<id>.json`: the downstream contract (see [Output contract](#output-contract))
- `<name>_<timestamp>_<id>.txt`: a readable transcript with timestamps

---

## Usage and configuration

| Option | Description |
|---|---|
| `[file]` | File to process. Omit it for interactive mode. |
| `--mock` | Mock engine: a `.txt` file is treated as the speech, any other file returns a built-in sample script. |
| `--model-type <type>` | `TinyEn`, `BaseEn` (default), `SmallEn`, `MediumEn`, `LargeV3Turbo` ... |
| `--model <path>` | Use an existing ggml model file. |
| `--out <dir>` | Output folder (default `./output`). |
| `--ffmpeg <path>` | Full path to ffmpeg if it isn't on PATH. |
| `--threads <n>` | CPU threads per Whisper run. |

The same settings live in `appsettings.json` (section `Pipeline`) and can be overridden with environment variables,
for example `Pipeline__ModelType=SmallEn`. Precedence: command line > environment variables > `appsettings.json`.

**Choosing a model.** The English-only `.en` models are faster and more accurate for English than the multilingual
ones of the same size.

| Model | Size | Speed on CPU | Accuracy |
|---|---|---|---|
| `TinyEn` | 75 MB | fastest | fine for clear audio |
| `BaseEn` (default) | 142 MB | fast | good |
| `SmallEn` | 466 MB | medium | very good |
| `MediumEn` | 1.5 GB | slow | excellent |
| `LargeV3Turbo` | 1.6 GB | slow on CPU, fast on GPU | best, multilingual |

For an NVIDIA GPU, replace `Whisper.net.Runtime` with `Whisper.net.Runtime.Cuda` in the `.csproj`.

---

## Output contract

The JSON document is the interface to downstream systems. The shape is shown below; the values come from the
sample recording and are illustrative.

```json
{
  "jobId": "3f9c0e5d6a1b4c2e8f7a9b0c1d2e3f4a",
  "sourceFile": "meeting-sample.wav",
  "createdAtUtc": "2026-11-15T09:10:11.123+00:00",
  "audioDuration": "00:00:25.250",
  "engine": "whisper.net/BaseEn",
  "language": "en",
  "rawText": "Hi team, um, thanks for joining the call today. ...",
  "text": "Hi team, thanks for joining the call today. ... You can reach me at [EMAIL]. Great work everyone.",
  "segments": [
    { "start": "00:00:00.000", "end": "00:00:03.200", "text": "Hi team, thanks for joining the call today." }
  ],
  "wordCount": 72,
  "keywords": ["customers", "dashboard", "wednesday", "incident", "release", "payment", "service"],
  "sentiment": { "label": "Mixed", "score": 0.14, "positiveHits": 4, "negativeHits": 3 },
  "actionItems": [
    { "text": "John needs to fix the retry logic by Wednesday.", "owner": "John", "dueHint": "by Wednesday" },
    { "text": "Sarah will follow up with the client and send the incident report by end of the week.",
      "owner": "Sarah", "dueHint": "by end of the week" }
  ],
  "redactionCount": 1,
  "processingSteps": ["normalize-text", "redact-pii", "extract-keywords", "analyze-sentiment", "detect-action-items"],
  "processingTimeMs": 3120
}
```

| Field | Notes |
|---|---|
| `rawText` | Exactly what the speech engine produced. Kept for audit and debugging; it **may contain personal data**. |
| `text`, `segments` | Cleaned and redacted. These are the fields downstream systems should use. |
| `segments` | Timestamped pieces of the transcript, for subtitles, search hits that jump to a time, or speaker views. |
| `processingSteps` | Which processors ran, in order. Makes the result traceable. |
| `jobId` | Unique per run. Used in output file names and suitable as a correlation ID in logs and messages. |

Times use `hh:mm:ss.fff`, enums are strings, and null fields are omitted, so the JSON is easy to read and stable to
parse.

---

## Design decisions

### 1. Speech-to-text engine: Whisper.net running locally

| Option considered | Why it was or wasn't chosen |
|---|---|
| **Whisper.net (chosen)** | Free and open source (MIT). Native .NET API via NuGet, runs offline, cross-platform, CPU or GPU. Whisper is accurate across accents and noisy audio and adds punctuation and casing itself. |
| Cloud APIs (Azure AI Speech, Deepgram, AssemblyAI) | Excellent, but they need an account and key and cost money per minute. Against the "free" requirement, and they make the solution harder for a reviewer to run. |
| Vosk | Lightweight and supports streaming, but noticeably less accurate and produces no punctuation. |
| sherpa-onnx / NVIDIA Parakeet | Very fast and accurate, with true streaming. More setup in .NET; the better choice for live audio (see [Trade-offs](#trade-offs-and-known-limitations)). |
| Python models (faster-whisper, Qwen3-ASR) | Strong accuracy, but they would need a Python sidecar service. Too much machinery for this scope. |

**Default model: `base.en`.** It's small enough to download quickly and runs faster than real time on an ordinary
CPU, with good English accuracy. Bigger models are one setting away.

**Model handling.** The model downloads automatically on first use, to a `.partial` file that is renamed only when
the download completes, so an interrupted download never leaves a corrupt model behind. The model loads once per
process, behind a lock, and is shared by every job.

### 2. Architecture: ports and adapters (Clean Architecture)

Every stage sits behind an interface in `Abstractions/Contracts.cs`:

| Interface | Implementations | Possible replacements |
|---|---|---|
| `IAudioNormalizer` | `AudioNormalizer`, `PassThroughAudioLoader` | Cloud media service |
| `ITranscriber` | `WhisperTranscriber`, `MockTranscriber` | Azure AI Speech, sherpa-onnx (streaming), any paid API |
| `ITranscriptProcessor` | The five processors | ML.NET, Azure AI Language, an LLM |
| `IResultSink` | `JsonFileResultSink` | Service Bus topic, Cosmos DB, search index, webhook |

**Why:** the brief allows "any library or API", so the engine is the part most likely to change. Swapping it is one
new class plus one line in `ServiceCollectionExtensions.cs`, with no change to the orchestrator or the processors.
The same seams let the tests run the full pipeline with a mock engine and an in-memory sink.

The `TranscriptionOrchestrator` only coordinates: validate → normalize → transcribe → process → write. It has no
knowledge of Whisper, ffmpeg or JSON.

### 3. Audio normalization: three strategies, cheapest first

Whisper needs 16 kHz, mono, 16-bit PCM WAV. The normalizer picks the first strategy that works:

1. **The file is already in that format** → use it as-is (no copy, no conversion).
2. **ffmpeg is available** → convert anything: mp3, m4a, ogg, flac, or the audio track of mp4/mov/webm video.
3. **No ffmpeg, but the input is WAV** → resample and downmix in managed code with NAudio.

**Why:** WAV input works with zero external tools, and ffmpeg is only needed for compressed formats. ffmpeg runs
as a separate process with arguments passed through `ArgumentList` (no shell, so file names can't inject commands).
If the run is cancelled, the ffmpeg process tree is killed. Converted audio goes to a temporary file with a unique
name, and that file is deleted when the job finishes, even if it fails.

### 4. Processing chain: small, ordered, single-purpose steps

Each processor does one job, is registered in DI, and runs in registration order (chain of responsibility):

| Order | Processor | What it does | Why it is in this position |
|---|---|---|---|
| 1 | `TextNormalizer` | Removes filler words (um, uh, erm), stutters ("the the"), stray spaces; fixes capitalisation and lowercase "i". | Every later step works better on clean text. |
| 2 | `PiiRedactor` | Masks emails (including dictated ones, "john dot doe at example dot com"), card numbers and phone numbers. | Runs **before** extraction so personal data can never leak into keywords or action items. |
| 3 | `KeywordExtractor` | Word frequency with a stop-word list; ties broken by word length, then first appearance. | Needs clean, redacted text. |
| 4 | `SentimentAnalyzer` | Word-list scoring with negation handling ("not good" counts as negative). Labels: Positive, Negative, Mixed, Neutral. | Independent; any order after redaction. |
| 5 | `ActionItemDetector` | Finds commitments ("needs to", "will", "follow up"...), plus the owner ("John needs to...") and due date ("by Wednesday"). Skips questions and pronouns such as "We". | Needs clean sentence boundaries. |

Normalization and redaction are applied to both the full text and each timestamped segment, so subtitles and
search results are cleaned and redacted too. Keywords, sentiment and action items are computed from the full text.

**Why rule-based processing?** It's free, deterministic, fast, explainable and easy to unit-test, which suits a
take-home brief. Each rule set can be replaced by an ML model or an LLM behind the same interface when accuracy
matters more than cost.

### 5. An immutable document flowing through the chain

`TranscriptDocument` is an immutable record. Each processor returns a new copy (`with` expressions) and adds its
name to `processingSteps`.

**Why:** no shared mutable state, so processors are safe to run as singletons and from concurrent jobs. Each step is
a pure function of its input, which makes it trivial to test, and the original `rawText` can never be overwritten
by mistake.

### 6. Error handling: failures name their stage

Every stage is wrapped so that any failure becomes a `PipelineException` carrying the stage name (`validate`,
`normalize`, `transcribe`, `redact-pii`, `write`...). The console prints one clear line, for example
`[normalize] '.mp4' files need ffmpeg ...`, instead of a stack trace, and interactive mode keeps running so the next
file can be tried.

Inputs are validated before any expensive work: the file must exist, must not be empty, and must have a supported
extension. Ctrl+C cancels the current job cleanly, including the external ffmpeg process.

### 7. Configuration and dependency injection

The app uses the Generic Host (`Host.CreateApplicationBuilder`) for configuration, logging and DI, the same
building blocks as an ASP.NET Core API or a Worker Service. Turning this console app into an API or a queue worker
means reusing `AddTranscriptionPipeline(...)` unchanged and only replacing `Program.cs`.

### 8. Built for the reviewer's machine

- **Mock mode** (`--mock`) runs the full pipeline with no model, no ffmpeg and no native libraries.
- **Sample data:** a short synthetic meeting recording (`.wav` and `.mp3`) and a mock transcript with fillers,
  personal data and action items to exercise every processor.
- **`UseAppHost=false`:** the build does not produce an unsigned `.exe` launcher; the app runs through the
  Microsoft-signed `dotnet` host. This avoids Windows Smart App Control / App Control policies blocking the program.
- **`RollForward=Major`:** targets .NET 8 (LTS) but still runs if only a newer runtime is installed.

---

## Handling concurrent uploads and scaling

This version processes one file per run (or one at a time in interactive mode). The pieces that matter for
concurrency are already in place: GUID job IDs, unique temp and output file names, a shared model loaded once behind
a lock, and stateless processors. Two people transcribing `Intro.mp4` at the same moment will never overwrite each
other's files.

The guiding rule is **never transcribe inside the upload request**. Transcription is CPU/GPU-heavy and can take
minutes, so uploads are accepted immediately, queued as jobs, and processed by a limited number of workers.

### Step 1: one server (API + background workers)

```
POST /transcriptions ─► stream file to disk ─► bounded Channel<Job> ─► N workers ─► TranscriptionOrchestrator
        │                                         │
        └─ 202 Accepted + jobId                    └─ queue full → 429 Too Many Requests + Retry-After
GET /transcriptions/{id} ─► Queued | Processing | Completed | Failed
```

- **Bounded `Channel<TranscriptionJob>`** with a fixed number of `BackgroundService` workers. The bound gives
  backpressure: when the queue is full the API returns `429` instead of running out of memory.
- **Concurrency sized to the hardware:** about `workers = CPU cores / threads per job`. Each Whisper run is already
  multi-threaded, so 2–4 parallel jobs on a CPU usually beat 16 jobs competing for the same cores; on a GPU, 1–2.
- **One model, many jobs:** the `WhisperFactory` (model) is shared; each job creates its own `WhisperProcessor`,
  because a processor is not thread-safe.
- **Duplicate detection:** hash each upload (SHA-256). If the same file arrives again, return the existing job ID
  instead of transcribing twice. This also makes client retries harmless.
- **Uploads streamed to disk** with size and duration limits checked before a job is queued.

Limitation: jobs held in memory are lost if the process restarts. That is acceptable on one machine but not beyond.

### Step 2: at scale on Azure

```
Client ─► SAS URL ─► Blob Storage ─► Event Grid ─► Service Bus queue ─► Container Apps workers (KEDA autoscale)
                                                                     │
                       Cosmos DB (job status, results) ◄─────────────┤
                       Service Bus topic "transcript-completed" ◄────┘ ─► downstream consumers / webhook / SignalR
```

| Concern | Approach |
|---|---|
| Large files | Clients upload directly to Blob Storage with a short-lived SAS URL; files never pass through the API. |
| Durable queue | Event Grid turns each upload into a Service Bus message, so a job survives restarts and deploys. |
| Many workers, each job once | Competing consumers on the queue; each worker keeps its own concurrency limit from Step 1. |
| Scaling | KEDA scales Container Apps workers on queue length, down to zero when idle. |
| Long jobs | Transcription can outlast the message lock, so `MaxAutoLockRenewalDuration` keeps the lock alive and stops the message reappearing mid-job. |
| Duplicate delivery | Service Bus delivers at least once, so results are upserted by job ID or file hash: a second run overwrites the same record instead of creating another. |
| Failures | Temporary errors retried with Polly; after `MaxDeliveryCount` attempts the message goes to the dead-letter queue with an alert. Bad audio (`normalize` failures) fails fast without retries. |
| Long recordings | Split by voice-activity detection, transcribe chunks in parallel, then merge segments with their time offsets. |
| Fairness | Per-tenant limits or separate queues, so one customer's 500 uploads don't block everyone else. |
| Notifying clients | Publish to a topic, call a webhook or push via SignalR, instead of clients polling. |

**In short:** separate upload from processing with a durable queue, limit concurrency per worker to the hardware,
scale out by adding workers, and make every step safe to repeat, because retries and duplicate messages will happen.

---

## Trade-offs and known limitations

| Area | Limitation | Better option when needed |
|---|---|---|
| Live audio | Whisper processes complete files; it isn't designed for real-time streaming. | sherpa-onnx with a streaming model behind the same `ITranscriber`. |
| Speed on CPU | Larger models are slow without a GPU. | `Whisper.net.Runtime.Cuda`, or a smaller model. |
| Speakers | No speaker separation (who said what). | Add a diarization step (e.g. pyannote, Azure Speech). |
| Language | Configured for English. | A multilingual model and `Language = "auto"`. |
| Keywords and sentiment | Rule-based: misses sarcasm and context, and single mentions all tie on frequency. | TF-IDF across many transcripts, ML.NET, Azure AI Language, or an LLM. |
| Action items | Pattern-based: catches common phrasings only; owners must be capitalised first names at the start of a sentence. | An LLM with a structured-output schema. |
| PII redaction | Covers emails, phone and card numbers, not names or addresses. `rawText` still holds the original. | Azure AI Language PII detection or Microsoft Presidio; drop or encrypt `rawText` if not needed. |
| Whisper quirks | Can occasionally invent text on long silences or music. | Voice-activity detection before transcription. Non-speech markers such as `[BLANK_AUDIO]` are already filtered out. |

---

## Project structure

```
src/TranscriptionPipeline/
  Abstractions/Contracts.cs              IAudioNormalizer, ITranscriber, ITranscriptProcessor, IResultSink,
                                         NormalizedAudio, PipelineException
  Domain/Models.cs                       Segment, RawTranscript, TranscriptDocument and related records
  Configuration/PipelineOptions.cs       settings bound from appsettings.json
  Audio/AudioNormalizer.cs               pass-through, ffmpeg or NAudio; temp-file cleanup
  Transcription/WhisperTranscriber.cs    Whisper.net engine with model auto-download
  Transcription/MockTranscriber.cs       fake engine for demos and tests
  Processing/                            the five processors
  Output/JsonFileResultSink.cs           JSON + TXT writer, time format converter
  Pipeline/TranscriptionOrchestrator.cs  runs the stages, wraps failures with the stage name
  ServiceCollectionExtensions.cs         DI registration and processor order
  CliArgs.cs                             command-line parsing and path clean-up
  Program.cs                             console UI (single-file and interactive modes)
tests/TranscriptionPipeline.Tests/       xUnit tests
samples/                                 meeting-sample.wav / .mp3 (synthetic speech), mock-transcript.txt
```

---

## Testing

```bash
dotnet test
```

The tests need no model, ffmpeg or network, so they run in seconds on any machine or CI agent.

| Area | What is covered |
|---|---|
| Each processor | Filler and stutter removal, capitalisation; email (written and spoken), phone and card redaction, plus false-positive checks ("met at the office", "2026 users"); keyword ranking and stop words; sentiment including negation and mixed; action items with owner and due date, ignoring questions. |
| Pipeline end to end | Mock engine + in-memory sink: correct processing order, raw text preserved, redaction counted, segments kept. |
| Validation | Missing file, unsupported type, `.txt` rejected outside mock mode, each reporting the `validate` stage. |
| Output | JSON and TXT written; enum and time formats checked. |
| Wiring and CLI | DI resolves the pipeline; option parsing, invalid arguments and pasted/drag-and-dropped paths. |

The Whisper engine itself isn't unit-tested (it would need the model). It is exercised manually with
`samples/meeting-sample.wav`.

---

## Troubleshooting (Windows)

| Message | Cause and fix |
|---|---|
| `An Application Control policy has blocked this file` | Smart App Control or a company policy blocks unsigned `.exe` files. The projects already set `UseAppHost=false`; do **Clean Solution**, delete `bin`/`obj`, and rebuild. |
| `[normalize] '.mp4' files need ffmpeg ...` | ffmpeg isn't installed or isn't on PATH. Run `winget install Gyan.FFmpeg`, then restart Visual Studio and the terminal. Or set `Pipeline:FfmpegPath` to the full path of `ffmpeg.exe`. If `ffmpeg -version` itself is blocked by the policy above, use `.wav` input. |
| Native library failed to load during `transcribe` | Install the Visual C++ Redistributable (x64). If the native `whisper.dll` is blocked by App Control, check the rest of the pipeline with `--mock` and ask IT to allow the folder, or run under WSL or Docker. |
| Output or model "missing" | Run from Visual Studio, the working folder is `bin\Debug\net8.0`, so `output\` and `models\` are created there. The banner prints both paths. |

---

## Next steps for production

1. Add an ASP.NET Core minimal API (`POST /transcriptions`, `GET /transcriptions/{id}`) with the bounded-channel
   workers described above, reusing `AddTranscriptionPipeline`.
2. Move to Blob Storage + Service Bus + Container Apps with KEDA for durable, autoscaling processing.
3. Add Polly retries, dead-letter handling, and OpenTelemetry tracing with `jobId` as the correlation ID.
4. Add voice-activity detection and chunking for long recordings, and speaker diarization.
5. Replace rule-based processors with ML or LLM-based ones where accuracy justifies the cost.
6. Protect `rawText` (encrypt or drop it) and add retention rules for uploaded audio.
