using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Audio;
using TranscriptionPipeline.Configuration;
using TranscriptionPipeline.Output;
using TranscriptionPipeline.Pipeline;
using TranscriptionPipeline.Processing;
using TranscriptionPipeline.Transcription;

namespace TranscriptionPipeline;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTranscriptionPipeline(this IServiceCollection services, PipelineOptions options)
    {
        services.AddSingleton(Options.Create(options));

        if (options.UseMock)
        {
            services.AddSingleton<IAudioNormalizer, PassThroughAudioLoader>();
            services.AddSingleton<ITranscriber, MockTranscriber>();
        }
        else
        {
            services.AddSingleton<IAudioNormalizer, AudioNormalizer>();
            services.AddSingleton<ITranscriber, WhisperTranscriber>();
        }

        // Processing chain, run in this order. Redaction comes before extraction so PII never reaches
        // keywords or action items. Add, remove or reorder steps here.
        services.AddSingleton<ITranscriptProcessor, TextNormalizer>();
        services.AddSingleton<ITranscriptProcessor, PiiRedactor>();
        services.AddSingleton<ITranscriptProcessor, KeywordExtractor>();
        services.AddSingleton<ITranscriptProcessor, SentimentAnalyzer>();
        services.AddSingleton<ITranscriptProcessor, ActionItemDetector>();

        services.AddSingleton<IResultSink, JsonFileResultSink>();
        services.AddSingleton<TranscriptionOrchestrator>();
        return services;
    }
}
