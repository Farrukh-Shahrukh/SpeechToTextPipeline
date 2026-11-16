using System.Text.RegularExpressions;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Domain;

namespace TranscriptionPipeline.Transcription;

/// <summary>
/// Fake STT engine for demos and tests: no model, no ffmpeg.
/// A .txt input is used as the "spoken" text; any other file returns the built-in sample script.
/// Segment timings are estimated at about 2.5 words per second.
/// </summary>
public sealed partial class MockTranscriber : ITranscriber
{
    public const string SampleScript =
        "Hi team, um, thanks for joining the call today. The new release went really well and customers are " +
        "happy with the faster dashboard. However, the payment service had a serious issue on Friday and we " +
        "lost some orders. John needs to fix the retry logic by Wednesday. Sarah will follow up with the client " +
        "and send the incident report by end of the week. You can reach me at farrukh dot test at example dot com. " +
        "Great work everyone.";

    private const double WordsPerSecond = 2.5;

    public string Name => "mock";

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    public async Task<RawTranscript> TranscribeAsync(NormalizedAudio audio, CancellationToken ct)
    {
        var text = Path.GetExtension(audio.SourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? await File.ReadAllTextAsync(audio.SourcePath, ct)
            : SampleScript;

        text = Regex.Replace(text, @"\s+", " ").Trim();

        var segments = new List<Segment>();
        var cursor = TimeSpan.Zero;
        foreach (var sentence in SentenceSplit().Split(text).Where(s => s.Length > 0))
        {
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var end = cursor + TimeSpan.FromSeconds(Math.Round(words / WordsPerSecond, 2));
            segments.Add(new Segment(cursor, end, sentence));
            cursor = end;
        }

        return new RawTranscript(text, segments, "en", Name);
    }
}
