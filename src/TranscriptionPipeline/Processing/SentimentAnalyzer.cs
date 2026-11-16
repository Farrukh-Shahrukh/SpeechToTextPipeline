using System.Text.RegularExpressions;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Processing;

/// <summary>
/// Lexicon-based sentiment with simple negation handling ("not good" counts as negative).
/// Score = (positive - negative) / (positive + negative), range -1..1.
/// Replace with ML.NET, Azure AI Language or an LLM behind the same interface if needed.
/// </summary>
public sealed partial class SentimentAnalyzer : ITranscriptProcessor
{
    private const double Threshold = 0.25;

    public string Name => "analyze-sentiment";

    [GeneratedRegex(@"[a-z]+(?:'[a-z]+)?")]
    private static partial Regex Tokens();

    public TranscriptDocument Process(TranscriptDocument document) =>
        document.WithStep(Name) with { Sentiment = Analyze(document.Text) };

    public static SentimentResult Analyze(string text)
    {
        var tokens = Tokens().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToArray();
        int pos = 0, neg = 0;

        for (var i = 0; i < tokens.Length; i++)
        {
            var polarity = Positive.Contains(tokens[i]) ? 1 : Negative.Contains(tokens[i]) ? -1 : 0;
            if (polarity == 0) continue;

            if (IsNegated(tokens, i)) polarity = -polarity;
            if (polarity > 0) pos++; else neg++;
        }

        var total = pos + neg;
        if (total == 0) return SentimentResult.Neutral;

        var score = Math.Round((double)(pos - neg) / total, 2);
        var label = score switch
        {
            >= Threshold => SentimentLabel.Positive,
            <= -Threshold => SentimentLabel.Negative,
            _ when pos > 0 && neg > 0 => SentimentLabel.Mixed,
            _ => SentimentLabel.Neutral
        };

        return new SentimentResult(label, score, pos, neg);
    }

    // Looks up to two tokens back: "not good", "not very good", "didn't work"
    private static bool IsNegated(string[] tokens, int index)
    {
        for (var j = Math.Max(0, index - 2); j < index; j++)
        {
            if (Negators.Contains(tokens[j]) || tokens[j].EndsWith("n't", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "hardly", "barely", "without", "nothing"
    };

    private static readonly HashSet<string> Positive = new(StringComparer.Ordinal)
    {
        "good", "great", "excellent", "awesome", "amazing", "fantastic", "happy", "glad", "pleased", "love",
        "loved", "like", "liked", "thanks", "thank", "appreciate", "appreciated", "success", "successful",
        "successfully", "improved", "improvement", "faster", "smooth", "smoothly", "resolved", "fixed", "stable",
        "win", "won", "helpful", "easy", "perfect", "nice", "positive", "satisfied", "impressive", "progress",
        "ahead", "efficient", "reliable", "congratulations", "well-done"
    };

    private static readonly HashSet<string> Negative = new(StringComparer.Ordinal)
    {
        "bad", "poor", "terrible", "awful", "horrible", "issue", "issues", "problem", "problems", "bug", "bugs",
        "fail", "failed", "failure", "failing", "error", "errors", "broken", "crash", "crashed", "outage",
        "down", "slow", "slower", "delay", "delayed", "late", "lost", "loss", "angry", "upset", "unhappy",
        "frustrated", "frustrating", "complaint", "complaints", "concern", "concerned", "worried", "risk",
        "blocked", "blocker", "difficult", "hard", "wrong", "worse", "worst", "negative", "disappointed",
        "unstable", "missed", "escalation", "escalated", "incident"
    };
}
