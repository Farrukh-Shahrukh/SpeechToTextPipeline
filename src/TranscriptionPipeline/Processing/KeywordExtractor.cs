using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Processing;

/// <summary>
/// Frequency-based keyword extraction with a stop-word list.
/// Ties are broken by word length, then first appearance, so results are deterministic.
/// </summary>
public sealed partial class KeywordExtractor(IOptions<PipelineOptions> options) : ITranscriptProcessor
{
    private readonly int _max = Math.Max(1, options.Value.MaxKeywords);

    public string Name => "extract-keywords";

    [GeneratedRegex(@"\[[A-Z]+\]")]
    private static partial Regex RedactionTokens();

    [GeneratedRegex(@"[a-z][a-z'-]{2,}")]
    private static partial Regex Words();

    public TranscriptDocument Process(TranscriptDocument document) =>
        document.WithStep(Name) with { Keywords = Extract(document.Text, _max) };

    public static IReadOnlyList<string> Extract(string text, int max)
    {
        var clean = RedactionTokens().Replace(text, " ").ToLowerInvariant();

        return Words().Matches(clean)
            .Select((m, index) => (Word: Stem(m.Value), Index: index))
            .Where(w => w.Word.Length > 2 && !StopWords.Contains(w.Word))
            .GroupBy(w => w.Word)
            .Select(g => (Word: g.Key, Count: g.Count(), First: g.Min(x => x.Index)))
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Word.Length) // longer words tend to be more specific
            .ThenBy(x => x.First)
            .Take(max)
            .Select(x => x.Word)
            .ToList();
    }

    private static string Stem(string word) =>
        word.EndsWith("'s", StringComparison.Ordinal) ? word[..^2] : word.Trim('\'', '-');

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "yours", "all", "any", "can", "had", "has", "have",
        "her", "hers", "him", "his", "how", "its", "our", "ours", "out", "she", "they", "them", "their", "then",
        "there", "these", "this", "that", "those", "was", "were", "what", "when", "where", "which", "who", "whom",
        "why", "will", "with", "would", "could", "should", "shall", "may", "might", "must", "about", "above",
        "after", "again", "against", "also", "because", "been", "before", "being", "below", "between", "both",
        "does", "did", "doing", "done", "down", "during", "each", "few", "from", "further", "here", "into",
        "just", "more", "most", "much", "only", "other", "over", "own", "same", "some", "such", "than", "too",
        "under", "until", "very", "while", "now", "yes", "yeah", "okay", "really", "like", "get", "got", "going",
        "gonna", "know", "think", "thing", "things", "let", "let's", "lets", "one", "two", "well", "say", "said",
        "see", "need", "needs", "make", "made", "want", "way", "also", "however", "today", "everyone", "team",
        "thanks", "thank", "hi", "hello", "able", "reach", "i'm", "it's", "we're", "they're", "you're", "don't",
        "didn't", "isn't", "wasn't", "won't", "can't", "i've", "we've", "i'll", "we'll", "lot", "lots", "went",
        "new", "joining", "end"
    };
}
