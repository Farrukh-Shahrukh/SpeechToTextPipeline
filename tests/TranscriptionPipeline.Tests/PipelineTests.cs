using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Audio;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Domain;
using TranscriptionPipeline.Output;
using TranscriptionPipeline.Pipeline;
using TranscriptionPipeline.Processing;
using TranscriptionPipeline.Transcription;

namespace TranscriptionPipeline.Tests;

/// <summary>Captures results in memory instead of writing files.</summary>
internal sealed class InMemorySink : IResultSink
{
    public List<TranscriptDocument> Documents { get; } = [];

    public Task<IReadOnlyList<string>> WriteAsync(TranscriptDocument document, CancellationToken ct)
    {
        Documents.Add(document);
        return Task.FromResult<IReadOnlyList<string>>([$"memory://{document.JobId}"]);
    }
}

public sealed class PipelineTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pipeline-tests").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static TranscriptionOrchestrator CreateMockPipeline(InMemorySink sink, PipelineOptions? options = null)
    {
        var opts = Options.Create(options ?? new PipelineOptions { UseMock = true });
        return new TranscriptionOrchestrator(
            new PassThroughAudioLoader(),
            new MockTranscriber(),
            [
                new TextNormalizer(), new PiiRedactor(), new KeywordExtractor(opts),
                new SentimentAnalyzer(), new ActionItemDetector()
            ],
            sink,
            opts,
            NullLogger<TranscriptionOrchestrator>.Instance);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Run_WithMockTextFile_ProducesProcessedDocument()
    {
        var sink = new InMemorySink();
        var input = WriteFile("call.txt",
            "Um, the outage was bad. Ali needs to restart the server by tomorrow. Email ali@corp.com.");

        var result = await CreateMockPipeline(sink).RunAsync(input);

        var doc = Assert.Single(sink.Documents);
        Assert.Same(doc, result.Document);
        Assert.Equal("call.txt", doc.SourceFile);
        Assert.StartsWith("The outage was bad.", doc.Text);
        Assert.Contains("[EMAIL]", doc.Text);
        Assert.Contains("um", doc.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, doc.RedactionCount);
        Assert.Equal(SentimentLabel.Negative, doc.Sentiment.Label);
        Assert.Equal("Ali", Assert.Single(doc.ActionItems).Owner);
        Assert.Equal(3, doc.Segments.Count);
        Assert.Equal(
            new[] { "normalize-text", "redact-pii", "extract-keywords", "analyze-sentiment", "detect-action-items" },
            doc.ProcessingSteps);
    }

    [Fact]
    public async Task Run_WithAudioFileInMockMode_UsesSampleScript()
    {
        var sink = new InMemorySink();
        var input = WriteFile("meeting.wav", "not really audio, the mock never reads it");

        var result = await CreateMockPipeline(sink).RunAsync(input);

        Assert.Equal(2, result.Document.ActionItems.Count);
        Assert.Contains("payment", result.Document.Keywords);
        Assert.DoesNotContain("example dot com", result.Document.Text);
    }

    [Fact]
    public async Task Run_MissingFile_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            CreateMockPipeline(new InMemorySink()).RunAsync(Path.Combine(_tempDir, "nope.wav")));

        Assert.Equal("validate", ex.Stage);
    }

    [Fact]
    public async Task Run_UnsupportedExtension_ThrowsValidationError()
    {
        var input = WriteFile("notes.docx", "x");

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            CreateMockPipeline(new InMemorySink()).RunAsync(input));

        Assert.Equal("validate", ex.Stage);
    }

    [Fact]
    public async Task Run_TextFileOutsideMockMode_IsRejected()
    {
        var input = WriteFile("speech.txt", "hello");
        var pipeline = CreateMockPipeline(new InMemorySink(), new PipelineOptions { UseMock = false });

        await Assert.ThrowsAsync<PipelineException>(() => pipeline.RunAsync(input));
    }

    [Fact]
    public async Task JsonSink_WritesJsonAndTextFiles()
    {
        var options = Options.Create(new PipelineOptions { UseMock = true, OutputDirectory = _tempDir });
        var sink = new JsonFileResultSink(options);
        var doc = new SentimentAnalyzer().Process(TestDoc.Create("Great work everyone."));

        var outputs = await sink.WriteAsync(doc, CancellationToken.None);

        Assert.Equal(2, outputs.Count);
        var json = await File.ReadAllTextAsync(outputs[0]);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("Positive", parsed.RootElement.GetProperty("sentiment").GetProperty("label").GetString());
        Assert.Equal("00:00:05.000", parsed.RootElement.GetProperty("audioDuration").GetString());
        Assert.Contains("[00:00:00.000 -> 00:00:05.000]", await File.ReadAllTextAsync(outputs[1]));
    }

    [Fact]
    public void ServiceRegistration_MockMode_ResolvesOrchestrator()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddTranscriptionPipeline(new PipelineOptions { UseMock = true });
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<TranscriptionOrchestrator>());
        Assert.IsType<MockTranscriber>(provider.GetRequiredService<ITranscriber>());
        Assert.Equal(5, provider.GetServices<ITranscriptProcessor>().Count());
    }
}

public class CliArgsTests
{
    [Fact]
    public void Parse_ReadsPathAndOptions()
    {
        var cli = CliArgs.Parse(["\"C:\\audio\\call.mp3\"", "--mock", "--model-type", "SmallEn", "--out", "results"]);

        Assert.Equal("C:\\audio\\call.mp3", cli.InputPath);
        Assert.True(cli.Mock);

        var options = new PipelineOptions();
        cli.ApplyTo(options);
        Assert.Equal("SmallEn", options.ModelType);
        Assert.Equal("models/ggml-smallen.bin", options.ModelPath);
        Assert.Equal("results", options.OutputDirectory);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--threads")]
    [InlineData("--threads", "abc")]
    public void Parse_InvalidArguments_Throw(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CliArgs.Parse(args));

    [Theory]
    [InlineData("& 'C:\\My Files\\a.wav'", "C:\\My Files\\a.wav")]
    [InlineData("  \"/tmp/a b.wav\"  ", "/tmp/a b.wav")]
    public void CleanPath_HandlesPastedPaths(string input, string expected) =>
        Assert.Equal(expected, CliArgs.CleanPath(input));
}
