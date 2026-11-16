using Microsoft.Extensions.Options;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Domain;
using TranscriptionPipeline.Processing;

namespace TranscriptionPipeline.Tests;

internal static class TestDoc
{
    public static TranscriptDocument Create(string text) =>
        TranscriptDocument.FromRaw(
            new RawTranscript(text, [new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(5), text)], "en", "test"),
            "test.wav",
            TimeSpan.FromSeconds(5));
}

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Hi team, um, thanks for joining.", "Hi team, thanks for joining.")]
    [InlineData("um so we shipped it.", "So we shipped it.")]
    [InlineData("We need the the report.", "We need the report.")]
    [InlineData("i think it works.   uh it does .", "I think it works. It does.")]
    [InlineData("I had had enough.", "I had had enough.")]
    public void Normalize_CleansSpokenNoise(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Process_UpdatesTextSegmentsAndWordCount()
    {
        var doc = new TextNormalizer().Process(TestDoc.Create("uh hello hello world"));

        Assert.Equal("Hello world", doc.Text);
        Assert.Equal("Hello world", doc.Segments.Single().Text);
        Assert.Equal(2, doc.WordCount);
        Assert.Equal("uh hello hello world", doc.RawText); // raw is never modified
        Assert.Contains("normalize-text", doc.ProcessingSteps);
    }
}

public class PiiRedactorTests
{
    [Theory]
    [InlineData("Mail john.doe@example.com today.", "Mail [EMAIL] today.")]
    [InlineData("Reach me at farrukh dot test at example dot com.", "Reach me at [EMAIL].")]
    [InlineData("Write to sarah at contoso.com please.", "Write to [EMAIL] please.")]
    [InlineData("Call +92 334 1234567 now.", "Call [PHONE] now.")]
    [InlineData("Card 4111 1111 1111 1111 was charged.", "Card [CARD] was charged.")]
    [InlineData("We met at the office at noon.", "We met at the office at noon.")]
    [InlineData("We have 2026 users.", "We have 2026 users.")]
    public void Redact_MasksPersonalData(string input, string expected) =>
        Assert.Equal(expected, PiiRedactor.Redact(input).Text);

    [Fact]
    public void Process_CountsRedactionsAndCleansSegments()
    {
        var doc = new PiiRedactor().Process(TestDoc.Create("a@b.com and 0300-1234567"));

        Assert.Equal(2, doc.RedactionCount);
        Assert.DoesNotContain("@", doc.Segments.Single().Text);
    }
}

public class KeywordExtractorTests
{
    [Fact]
    public void Extract_RanksByFrequencyAndSkipsStopWords()
    {
        var keywords = KeywordExtractor.Extract(
            "The payment service failed. The payment retry logic and the payment service need work.", 3);

        Assert.Equal(new[] { "payment", "service", "failed" }, keywords);
    }

    [Fact]
    public void Extract_IgnoresRedactionTokens()
    {
        var keywords = KeywordExtractor.Extract("Contact [EMAIL] or [PHONE] about billing.", 5);

        Assert.DoesNotContain("email", keywords);
        Assert.DoesNotContain("phone", keywords);
        Assert.Contains("billing", keywords);
    }

    [Fact]
    public void Process_RespectsMaxKeywordsOption()
    {
        var extractor = new KeywordExtractor(Options.Create(new PipelineOptions { MaxKeywords = 2 }));
        var doc = extractor.Process(TestDoc.Create("alpha beta gamma delta alpha beta"));

        Assert.Equal(new[] { "alpha", "beta" }, doc.Keywords);
    }
}

public class SentimentAnalyzerTests
{
    [Theory]
    [InlineData("Great release, customers are happy and it is faster.", SentimentLabel.Positive)]
    [InlineData("The service failed and we lost orders. Customers are angry.", SentimentLabel.Negative)]
    [InlineData("The meeting is at three in room four.", SentimentLabel.Neutral)]
    [InlineData("The demo was not good.", SentimentLabel.Negative)]
    [InlineData("It didn't fail this time.", SentimentLabel.Positive)]
    [InlineData("Good progress but one bug and one delay.", SentimentLabel.Mixed)]
    public void Analyze_DetectsSentiment(string text, SentimentLabel expected) =>
        Assert.Equal(expected, SentimentAnalyzer.Analyze(text).Label);

    [Fact]
    public void Analyze_ScoreIsBetweenMinusOneAndOne()
    {
        var result = SentimentAnalyzer.Analyze("great great great");
        Assert.Equal(1.0, result.Score);
        Assert.Equal(3, result.PositiveHits);
    }
}

public class ActionItemDetectorTests
{
    [Fact]
    public void Detect_FindsTasksWithOwnerAndDueDate()
    {
        var items = ActionItemDetector.Detect(
            "The release went well. John needs to fix the retry logic by Wednesday. " +
            "Sarah will send the report by end of the week. We should review costs.");

        Assert.Equal(3, items.Count);
        Assert.Equal(new ActionItem("John needs to fix the retry logic by Wednesday.", "John", "by Wednesday"), items[0]);
        Assert.Equal("Sarah", items[1].Owner);
        Assert.Equal("by end of the week", items[1].DueHint);
        Assert.Null(items[2].Owner); // "We" is not a person
        Assert.Null(items[2].DueHint);
    }

    [Fact]
    public void Detect_IgnoresQuestionsAndStatements()
    {
        var items = ActionItemDetector.Detect("Will this work? The dashboard is faster now.");
        Assert.Empty(items);
    }
}
