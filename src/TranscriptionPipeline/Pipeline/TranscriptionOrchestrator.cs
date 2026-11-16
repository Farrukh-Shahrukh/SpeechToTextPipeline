using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Pipeline;

public sealed record PipelineResult(TranscriptDocument Document, IReadOnlyList<string> Outputs);

/// <summary>
/// Runs the stages in order: validate, normalize, transcribe, process (chain), write.
/// Any stage failure is wrapped in a <see cref="PipelineException"/> naming the stage.
/// </summary>
public sealed class TranscriptionOrchestrator(
    IAudioNormalizer normalizer,
    ITranscriber transcriber,
    IEnumerable<ITranscriptProcessor> processors,
    IResultSink sink,
    IOptions<PipelineOptions> options,
    ILogger<TranscriptionOrchestrator> logger)
{
    private readonly IReadOnlyList<ITranscriptProcessor> _processors = processors.ToList();

    public async Task<PipelineResult> RunAsync(string inputPath, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var path = Validate(inputPath);
        logger.LogInformation("Processing {File} with {Engine}", Path.GetFileName(path), transcriber.Name);

        await using var audio = await RunStageAsync("normalize", () => normalizer.NormalizeAsync(path, ct));

        var raw = await RunStageAsync("transcribe", () => transcriber.TranscribeAsync(audio, ct));
        logger.LogInformation("Transcribed {Segments} segments in {Elapsed:F1}s",
            raw.Segments.Count, stopwatch.Elapsed.TotalSeconds);

        if (string.IsNullOrWhiteSpace(raw.Text))
        {
            logger.LogWarning("No speech detected in {File}", Path.GetFileName(path));
        }

        var document = TranscriptDocument.FromRaw(raw, Path.GetFileName(path), audio.Duration);
        foreach (var processor in _processors)
        {
            ct.ThrowIfCancellationRequested();
            document = await RunStageAsync(processor.Name, () => Task.FromResult(processor.Process(document)));
        }

        document = document with { ProcessingTimeMs = stopwatch.ElapsedMilliseconds };
        var outputs = await RunStageAsync("write", () => sink.WriteAsync(document, ct));

        logger.LogInformation("Done in {Elapsed:F1}s", stopwatch.Elapsed.TotalSeconds);
        return new PipelineResult(document, outputs);
    }

    private string Validate(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new PipelineException("validate", "No file path was provided.");

        var path = Path.GetFullPath(inputPath);
        if (!File.Exists(path))
            throw new PipelineException("validate", $"File not found: {path}");

        if (new FileInfo(path).Length == 0)
            throw new PipelineException("validate", $"File is empty: {path}");

        var ext = Path.GetExtension(path);
        var isAudio = PipelineOptions.AudioExtensions.Contains(ext);
        var isMockText = options.Value.UseMock && ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
        if (!isAudio && !isMockText)
        {
            throw new PipelineException("validate",
                $"Unsupported file type '{ext}'. Supported: {string.Join(", ", PipelineOptions.AudioExtensions)}" +
                (options.Value.UseMock ? ", .txt" : string.Empty));
        }

        return path;
    }

    private static async Task<T> RunStageAsync<T>(string stage, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not PipelineException and not OperationCanceledException)
        {
            throw new PipelineException(stage, $"Stage '{stage}' failed: {ex.Message}", ex);
        }
    }
}
