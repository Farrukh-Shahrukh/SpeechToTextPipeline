using System.Text.RegularExpressions;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Processing;

/// <summary>
/// Cleans spoken-language noise: filler words, stutters ("the the"), spacing before punctuation,
/// sentence capitalisation, and a lowercase standalone "i".
/// </summary>
public sealed partial class TextNormalizer : ITranscriptProcessor
{
    // Repeats that are often grammatical ("I had had enough", "that that")
    private static readonly HashSet<string> AllowedRepeats = new(StringComparer.OrdinalIgnoreCase) { "had", "that" };

    public string Name => "normalize-text";

    [GeneratedRegex(@"\b(?:um+|uh+|erm+|hmm+|ah+)\b[,.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Fillers();

    [GeneratedRegex(@"\b([a-z]+)(?:\s+\1\b)+", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedWords();

    [GeneratedRegex(@"\s+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([,;:])\s*([,.!?;:])")]
    private static partial Regex DoublePunctuation();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"(^|[.!?]\s+)([a-z])")]
    private static partial Regex SentenceStart();

    [GeneratedRegex(@"\bi\b(?=$|[\s',.!?])")]
    private static partial Regex LowercaseI();

    public TranscriptDocument Process(TranscriptDocument document)
    {
        var text = Normalize(document.Text);
        var segments = document.Segments
            .Select(s => s with { Text = Normalize(s.Text) })
            .Where(s => s.Text.Length > 0)
            .ToList();

        return document.WithStep(Name) with
        {
            Text = text,
            Segments = segments,
            WordCount = CountWords(text)
        };
    }

    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var t = Fillers().Replace(input, string.Empty);
        t = RepeatedWords().Replace(t, m =>
            AllowedRepeats.Contains(m.Groups[1].Value) ? m.Value : m.Groups[1].Value);
        t = SpaceBeforePunctuation().Replace(t, "$1");
        t = DoublePunctuation().Replace(t, "$2");
        t = MultipleSpaces().Replace(t, " ").Trim().TrimStart(',', ';', ':', ' ');
        t = LowercaseI().Replace(t, "I");
        t = SentenceStart().Replace(t, m => m.Groups[1].Value + char.ToUpperInvariant(m.Groups[2].Value[0]));
        return t;
    }

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
