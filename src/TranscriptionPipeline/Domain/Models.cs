namespace TranscriptionPipeline.Domain;

/// <summary>A timestamped piece of the transcript as produced by the STT engine.</summary>
public sealed record Segment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Raw output of a speech-to-text engine, before any post-processing.</summary>
public sealed record RawTranscript(
    string Text,
    IReadOnlyList<Segment> Segments,
    string Language,
    string Engine);

public enum SentimentLabel
{
    Negative,
    Neutral,
    Mixed,
    Positive
}

public sealed record SentimentResult(SentimentLabel Label, double Score, int PositiveHits, int NegativeHits)
{
    public static SentimentResult Neutral { get; } = new(SentimentLabel.Neutral, 0, 0, 0);
}

public sealed record ActionItem(string Text, string? Owner, string? DueHint);

/// <summary>
/// The document that flows through the processing chain and is handed to downstream consumers.
/// Immutable: each processor returns a new copy via <c>with</c>.
/// </summary>
public sealed record TranscriptDocument
{
    public required string JobId { get; init; }
    public required string SourceFile { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan? AudioDuration { get; init; }
    public required string Engine { get; init; }
    public required string Language { get; init; }

    /// <summary>Exactly what the STT engine returned.</summary>
    public required string RawText { get; init; }

    /// <summary>Cleaned and redacted text, safe for downstream use.</summary>
    public required string Text { get; init; }

    public IReadOnlyList<Segment> Segments { get; init; } = [];
    public int WordCount { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public SentimentResult Sentiment { get; init; } = SentimentResult.Neutral;
    public IReadOnlyList<ActionItem> ActionItems { get; init; } = [];
    public int RedactionCount { get; init; }
    public IReadOnlyList<string> ProcessingSteps { get; init; } = [];
    public long ProcessingTimeMs { get; init; }

    public static TranscriptDocument FromRaw(RawTranscript raw, string sourceFile, TimeSpan? audioDuration) => new()
    {
        JobId = Guid.NewGuid().ToString("N"),
        SourceFile = sourceFile,
        AudioDuration = audioDuration,
        Engine = raw.Engine,
        Language = raw.Language,
        RawText = raw.Text,
        Text = raw.Text,
        Segments = raw.Segments
    };

    public TranscriptDocument WithStep(string step) => this with { ProcessingSteps = [.. ProcessingSteps, step] };
}
