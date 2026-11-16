namespace TranscriptionPipeline.Configuration;

public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Use the mock transcriber (no model or ffmpeg needed). Good for demos and tests.</summary>
    public bool UseMock { get; set; }

    /// <summary>Whisper.net GgmlType name: TinyEn, BaseEn, SmallEn, MediumEn, LargeV3Turbo ...</summary>
    public string ModelType { get; set; } = "BaseEn";

    /// <summary>Model file path. Relative paths are resolved against the app's folder.</summary>
    public string ModelPath { get; set; } = "models/ggml-base.en.bin";

    public bool AutoDownloadModel { get; set; } = true;
    public string Language { get; set; } = "en";

    /// <summary>0 = let Whisper decide.</summary>
    public int Threads { get; set; }

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Relative paths are resolved against the current working directory.</summary>
    public string OutputDirectory { get; set; } = "output";

    public int MaxKeywords { get; set; } = 10;

    public static IReadOnlySet<string> AudioExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wma", ".webm", ".mp4", ".mkv", ".mov"
    };

    public string ResolveModelPath() =>
        Path.IsPathRooted(ModelPath) ? ModelPath : Path.Combine(AppContext.BaseDirectory, ModelPath);

    public string ResolveOutputDirectory() => Path.GetFullPath(OutputDirectory);
}
