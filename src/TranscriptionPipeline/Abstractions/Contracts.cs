using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Abstractions;

/// <summary>Stage 1: turns any input file into 16 kHz, mono, 16-bit PCM WAV (what Whisper expects).</summary>
public interface IAudioNormalizer
{
    Task<NormalizedAudio> NormalizeAsync(string inputPath, CancellationToken ct);
}

/// <summary>Stage 2: speech-to-text engine. Swap Whisper for Azure Speech, sherpa-onnx or a mock.</summary>
public interface ITranscriber
{
    string Name { get; }
    Task<RawTranscript> TranscribeAsync(NormalizedAudio audio, CancellationToken ct);
}

/// <summary>Stage 3: one step in the post-processing chain. Processors run in registration order.</summary>
public interface ITranscriptProcessor
{
    string Name { get; }
    TranscriptDocument Process(TranscriptDocument document);
}

/// <summary>Stage 4: hands the result to downstream consumers (files, DB, queue, search index...).</summary>
public interface IResultSink
{
    /// <returns>Locations of what was written (file paths, message IDs, etc.).</returns>
    Task<IReadOnlyList<string>> WriteAsync(TranscriptDocument document, CancellationToken ct);
}

/// <summary>A normalized audio file. Deletes its temporary file when disposed.</summary>
public sealed class NormalizedAudio(string sourcePath, string audioPath, TimeSpan? duration, bool isTemporary)
    : IAsyncDisposable
{
    public string SourcePath { get; } = sourcePath;
    public string AudioPath { get; } = audioPath;
    public TimeSpan? Duration { get; } = duration;
    public bool IsTemporary { get; } = isTemporary;

    public ValueTask DisposeAsync()
    {
        if (IsTemporary)
        {
            try { File.Delete(AudioPath); } catch (IOException) { /* best effort */ }
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class PipelineException(string stage, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Stage { get; } = stage;
}
