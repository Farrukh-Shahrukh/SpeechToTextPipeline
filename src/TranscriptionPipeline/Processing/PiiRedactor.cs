using System.Text.RegularExpressions;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Processing;

/// <summary>
/// Masks personal data before the text leaves the pipeline: written and spoken email addresses
/// ("john dot doe at example dot com"), card numbers and phone numbers.
/// Runs before keyword and action-item extraction so PII never reaches downstream fields.
/// </summary>
public sealed partial class PiiRedactor : ITranscriptProcessor
{
    public const string EmailToken = "[EMAIL]";
    public const string CardToken = "[CARD]";
    public const string PhoneToken = "[PHONE]";

    public string Name => "redact-pii";

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    // How Whisper often writes a dictated address: "name dot surname at domain dot com" / "name at domain.com"
    [GeneratedRegex(
        @"\b[\w.]+(?:\s+dot\s+\w+)*\s+at\s+[\w-]+(?:(?:\.|\s+dot\s+)[\w-]+)*(?:\.|\s+dot\s+)(?:com|net|org|io|edu|gov|pk|co|uk|ai|dev|info|biz)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpokenEmail();

    // 13-19 digits, optionally separated by spaces or dashes
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex CardNumber();

    // 9+ characters of digits/separators, optional leading +
    [GeneratedRegex(@"(?<![\w+])\+?\d[\d\s().-]{7,}\d(?!\w)")]
    private static partial Regex Phone();

    // Order matters: emails first, then cards (longer digit runs) before phones
    private static readonly (Regex Pattern, string Token)[] Rules =
    [
        (Email(), EmailToken),
        (SpokenEmail(), EmailToken),
        (CardNumber(), CardToken),
        (Phone(), PhoneToken)
    ];

    public TranscriptDocument Process(TranscriptDocument document)
    {
        var (text, count) = Redact(document.Text);
        var segments = document.Segments.Select(s => s with { Text = Redact(s.Text).Text }).ToList();

        return document.WithStep(Name) with
        {
            Text = text,
            Segments = segments,
            RedactionCount = document.RedactionCount + count
        };
    }

    public static (string Text, int Count) Redact(string input)
    {
        var count = 0;
        var text = input;
        foreach (var (pattern, token) in Rules)
        {
            text = pattern.Replace(text, _ =>
            {
                count++;
                return token;
            });
        }
        return (text, count);
    }
}
