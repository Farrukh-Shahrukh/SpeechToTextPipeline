using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TranscriptionPipeline;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Output;
using TranscriptionPipeline.Pipeline;

Console.OutputEncoding = Encoding.UTF8;

CliArgs cli;
try
{
    cli = CliArgs.Parse(args);
}
catch (ArgumentException ex)
{
    WriteColored(ex.Message, ConsoleColor.Red);
    Console.WriteLine(CliArgs.Usage);
    return 2;
}

if (cli.ShowHelp)
{
    Console.WriteLine(CliArgs.Usage);
    return 0;
}

// Host gives us appsettings.json, environment variables (e.g. Pipeline__ModelType=SmallEn), logging and DI.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var options = builder.Configuration.GetSection(PipelineOptions.SectionName).Get<PipelineOptions>() ?? new PipelineOptions();
cli.ApplyTo(options);
builder.Services.AddTranscriptionPipeline(options);

using var host = builder.Build();
var orchestrator = host.Services.GetRequiredService<TranscriptionOrchestrator>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    WriteColored("Cancelling...", ConsoleColor.Yellow);
};

PrintBanner(options);

// Single file from the command line
if (cli.InputPath is not null)
{
    return await ProcessAsync(cli.InputPath) ? 0 : 1;
}

// Interactive mode: keep asking for file paths
while (!cts.IsCancellationRequested)
{
    Console.WriteLine();
    Console.Write("Enter audio file path (or press Enter to quit): ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) break;

    await ProcessAsync(CliArgs.CleanPath(line));
}

return 0;

async Task<bool> ProcessAsync(string path)
{
    try
    {
        var result = await orchestrator.RunAsync(path, cts.Token);
        PrintSummary(result);
        return true;
    }
    catch (PipelineException ex)
    {
        WriteColored($"[{ex.Stage}] {ex.Message}", ConsoleColor.Red);
        if (ex.InnerException is not null)
        {
            WriteColored($"  ({ex.InnerException.GetType().Name})", ConsoleColor.DarkGray);
        }
    }
    catch (OperationCanceledException)
    {
        WriteColored("Cancelled.", ConsoleColor.Yellow);
    }
    return false;
}

static void PrintBanner(PipelineOptions o)
{
    WriteColored("=== Transcription Pipeline ===", ConsoleColor.Cyan);
    Console.WriteLine(o.UseMock
        ? "Engine: mock (no model). A .txt file is used as the speech; other files return a sample script."
        : $"Engine: Whisper.net {o.ModelType} (model: {o.ResolveModelPath()})");
    Console.WriteLine($"Output: {o.ResolveOutputDirectory()}");
}

static void PrintSummary(PipelineResult result)
{
    var d = result.Document;
    Console.WriteLine();
    WriteColored($"Transcript for {d.SourceFile}", ConsoleColor.Green);
    Console.WriteLine($"  Duration:   {(d.AudioDuration is { } t ? TimeSpanJsonConverter.Format(t) : "n/a")}" +
                      $" | Words: {d.WordCount} | Took: {d.ProcessingTimeMs / 1000.0:F1}s | Engine: {d.Engine}");
    Console.WriteLine();
    Console.WriteLine(Indent(d.Text.Length > 0 ? d.Text : "(no speech detected)"));
    Console.WriteLine();
    Console.WriteLine($"  Keywords:   {(d.Keywords.Count > 0 ? string.Join(", ", d.Keywords) : "-")}");
    Console.WriteLine($"  Sentiment:  {d.Sentiment.Label} (score {d.Sentiment.Score:+0.00;-0.00;0.00}, " +
                      $"+{d.Sentiment.PositiveHits}/-{d.Sentiment.NegativeHits})");
    Console.WriteLine($"  Redactions: {d.RedactionCount}");
    Console.WriteLine($"  Action items: {(d.ActionItems.Count == 0 ? "none" : string.Empty)}");
    foreach (var item in d.ActionItems)
    {
        var meta = string.Join(", ", new[]
        {
            item.Owner is null ? null : $"owner: {item.Owner}",
            item.DueHint is null ? null : $"due: {item.DueHint}"
        }.Where(x => x is not null));
        Console.WriteLine($"    - {item.Text}{(meta.Length > 0 ? $"  [{meta}]" : string.Empty)}");
    }
    Console.WriteLine();
    foreach (var output in result.Outputs)
    {
        WriteColored($"  Saved: {output}", ConsoleColor.DarkGray);
    }
}

static string Indent(string text) => "  " + text;

static void WriteColored(string message, ConsoleColor color)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = previous;
}
