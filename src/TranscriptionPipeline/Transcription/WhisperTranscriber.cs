using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Domain;
using Whisper.net;
using Whisper.net.Ggml;

namespace TranscriptionPipeline.Transcription;

/// <summary>
/// Local, offline speech-to-text using Whisper.net (whisper.cpp bindings).
/// The model is downloaded from Hugging Face on first use and then loaded once per process.
/// </summary>
public sealed partial class WhisperTranscriber(IOptions<PipelineOptions> options, ILogger<WhisperTranscriber> logger)
    : ITranscriber, IDisposable
{
    private readonly PipelineOptions _options = options.Value;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;

    public string Name => $"whisper.net/{_options.ModelType}";

    // Whisper emits markers such as [BLANK_AUDIO], (music), [Applause] for non-speech
    [GeneratedRegex(@"^\s*[\[\(][^\]\)]*[\]\)]\s*$")]
    private static partial Regex NonSpeechRegex();

    public async Task<RawTranscript> TranscribeAsync(NormalizedAudio audio, CancellationToken ct)
    {
        var factory = await GetFactoryAsync(ct);

        var builder = factory.CreateBuilder().WithLanguage(_options.Language);
        if (_options.Threads > 0)
        {
            builder = builder.WithThreads(_options.Threads);
        }

        using var processor = builder.Build();
        await using var stream = File.OpenRead(audio.AudioPath);

        var segments = new List<Segment>();
        await foreach (var s in processor.ProcessAsync(stream, ct))
        {
            var text = s.Text.Trim();
            if (text.Length == 0 || NonSpeechRegex().IsMatch(text))
            {
                continue;
            }

            segments.Add(new Segment(s.Start, s.End, text));
            logger.LogDebug("{Start} -> {End}: {Text}", s.Start, s.End, text);
        }

        return new RawTranscript(string.Join(' ', segments.Select(x => x.Text)), segments, _options.Language, Name);
    }

    private async Task<WhisperFactory> GetFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null) return _factory;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_factory is not null) return _factory;

            var modelPath = _options.ResolveModelPath();
            if (!File.Exists(modelPath))
            {
                await DownloadModelAsync(modelPath, ct);
            }

            logger.LogInformation("Loading Whisper model {Model}", modelPath);
            _factory = WhisperFactory.FromPath(modelPath);
            return _factory;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task DownloadModelAsync(string modelPath, CancellationToken ct)
    {
        if (!_options.AutoDownloadModel)
        {
            throw new PipelineException("transcribe",
                $"Whisper model not found at '{modelPath}' and AutoDownloadModel is false.");
        }

        if (!Enum.TryParse<GgmlType>(_options.ModelType, ignoreCase: true, out var type))
        {
            throw new PipelineException("transcribe",
                $"Unknown ModelType '{_options.ModelType}'. Valid values: {string.Join(", ", Enum.GetNames<GgmlType>())}");
        }

        logger.LogInformation("Downloading Whisper model {Type} (one-time) to {Path} ...", type, modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

        var partialPath = modelPath + ".partial";
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type))
        await using (var target = File.Create(partialPath))
        {
            await source.CopyToAsync(target, ct);
        }

        File.Move(partialPath, modelPath, overwrite: true);
        logger.LogInformation("Model downloaded");
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _initLock.Dispose();
    }
}
