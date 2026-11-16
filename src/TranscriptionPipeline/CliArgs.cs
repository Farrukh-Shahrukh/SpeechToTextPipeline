using TranscriptionPipeline.Configuration;

namespace TranscriptionPipeline;

/// <summary>Small hand-rolled parser: one optional positional file path plus a few flags.</summary>
public sealed record CliArgs
{
    public string? InputPath { get; init; }
    public bool ShowHelp { get; init; }
    public bool? Mock { get; init; }
    public string? ModelType { get; init; }
    public string? ModelPath { get; init; }
    public string? OutputDirectory { get; init; }
    public string? FfmpegPath { get; init; }
    public int? Threads { get; init; }

    public const string Usage = """
        Usage: TranscriptionPipeline [file] [options]

          file                 Audio file to transcribe. If omitted, you will be asked for a path.

        Options:
          --mock               Use the mock transcriber (no model/ffmpeg). A .txt file is used as the speech.
          --model-type <type>  Whisper model: TinyEn, BaseEn, SmallEn, MediumEn, LargeV3Turbo ...
          --model <path>       Path to a ggml model file.
          --out <dir>          Output folder (default: ./output).
          --ffmpeg <path>      Path to ffmpeg if it's not on PATH.
          --threads <n>        CPU threads for Whisper.
          -h, --help           Show this help.

        Examples:
          TranscriptionPipeline samples/meeting-sample.wav
          TranscriptionPipeline "C:\Recordings\call.mp3" --model-type SmallEn --out results
          TranscriptionPipeline samples/mock-transcript.txt --mock
        """;

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value for {arg}");

            result = arg switch
            {
                "-h" or "--help" or "/?" => result with { ShowHelp = true },
                "--mock" => result with { Mock = true },
                "--model-type" => result with { ModelType = Next() },
                "--model" => result with { ModelPath = Next() },
                "--out" => result with { OutputDirectory = Next() },
                "--ffmpeg" => result with { FfmpegPath = Next() },
                "--threads" => result with { Threads = ParseThreads(Next()) },
                _ when arg.StartsWith("--", StringComparison.Ordinal) => throw new ArgumentException($"Unknown option {arg}"),
                _ when result.InputPath is null => result with { InputPath = CleanPath(arg) },
                _ => throw new ArgumentException($"Unexpected argument '{arg}'. Quote paths that contain spaces.")
            };
        }
        return result;
    }

    private static int ParseThreads(string value) =>
        int.TryParse(value, out var n) && n >= 0
            ? n
            : throw new ArgumentException("--threads expects a non-negative number");

    public void ApplyTo(PipelineOptions options)
    {
        if (Mock is { } mock) options.UseMock = mock;
        if (ModelType is not null)
        {
            options.ModelType = ModelType;
            // Keep the default file name in step with the chosen model unless --model was also given
            if (ModelPath is null) options.ModelPath = $"models/ggml-{ModelType.ToLowerInvariant()}.bin";
        }
        if (ModelPath is not null) options.ModelPath = ModelPath;
        if (OutputDirectory is not null) options.OutputDirectory = OutputDirectory;
        if (FfmpegPath is not null) options.FfmpegPath = FfmpegPath;
        if (Threads is { } threads) options.Threads = threads;
    }

    /// <summary>Handles pasted or drag-and-dropped paths: surrounding quotes, PowerShell's "&amp; '...'", and ~.</summary>
    public static string CleanPath(string input)
    {
        var p = input.Trim();
        if (p.StartsWith("& ", StringComparison.Ordinal)) p = p[2..].Trim();
        p = p.Trim('"', '\'');

        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
        {
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.TrimStart('~', '/', '\\'));
        }
        return p;
    }
}
