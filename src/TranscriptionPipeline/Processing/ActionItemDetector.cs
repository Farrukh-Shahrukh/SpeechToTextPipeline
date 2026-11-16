using System.Text.RegularExpressions;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Processing;

/// <summary>
/// Finds sentences that look like commitments or tasks, plus an optional owner
/// ("John needs to ...") and due-date hint ("by Wednesday", "by end of the week").
/// </summary>
public sealed partial class ActionItemDetector : ITranscriptProcessor
{
    private static readonly HashSet<string> NotOwners = new(StringComparer.Ordinal)
    {
        "We", "You", "They", "He", "She", "It", "This", "That", "There", "Everyone", "Someone", "Somebody",
        "Customers", "Please", "Then", "So", "And", "But", "Also", "Maybe"
    };

    public string Name => "detect-action-items";

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(
        @"\b(?:needs? to|ha(?:ve|s) to|must|should|will|going to|follows? up|follow-up|action item|to-?do|let's|please|make sure|assigned to)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Trigger();

    [GeneratedRegex(
        @"\b(?:by|before|until|due)\s+(?:(?:the\s+)?end\s+of\s+(?:the\s+)?(?:day|week|month|quarter|sprint)|tomorrow|today|tonight|next\s+\w+|monday|tuesday|wednesday|thursday|friday|saturday|sunday|eod|eow)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DueDate();

    [GeneratedRegex(@"^(?<owner>[A-Z][a-z]+)\s+(?:needs? to|will|should|must|has to|is going to)\b")]
    private static partial Regex Owner();

    public TranscriptDocument Process(TranscriptDocument document) =>
        document.WithStep(Name) with { ActionItems = Detect(document.Text) };

    public static IReadOnlyList<ActionItem> Detect(string text)
    {
        var items = new List<ActionItem>();

        foreach (var raw in SentenceSplit().Split(text))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0 || sentence.EndsWith('?') || !Trigger().IsMatch(sentence)) continue;

            var ownerMatch = Owner().Match(sentence);
            var owner = ownerMatch.Success && !NotOwners.Contains(ownerMatch.Groups["owner"].Value)
                ? ownerMatch.Groups["owner"].Value
                : null;

            var due = DueDate().Match(sentence);
            items.Add(new ActionItem(sentence, owner, due.Success ? due.Value : null));
        }

        return items;
    }
}
