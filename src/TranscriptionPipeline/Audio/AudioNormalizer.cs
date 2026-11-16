using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TranscriptionPipeline.Abstractions;
using TranscriptionPipeline.Configuration;

namespace TranscriptionPipeline.Audio;

/// <summary>
/// Converts input audio to 16 kHz mono 16-bit PCM WAV.
/// 1. WAV already in the right format: used as-is.
/// 2. ffmpeg on PATH: converts anything (mp3, m4a, ogg, mp4 ...).
/// 3. No ffmpeg: WAV files are resampled in managed code with NAudio.
/// </summary>
public sealed class AudioNormalizer(IOptions<PipelineOptions> options, ILogger<AudioNormalizer> logger) : IAudioNormalizer
{
    private const int TargetSampleRate = 16_000;
    private readonly PipelineOptions _options = options.Value;

    public async Task<NormalizedAudio> NormalizeAsync(string inputPath, CancellationToken ct)
    {
        var isWav = Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);

        if (isWav && IsWhisperReady(inputPath, out var duration))
        {
            logger.LogInformation("Audio is already 16 kHz mono PCM, no conversion needed");
            return new NormalizedAudio(inputPath, inputPath, duration, isTemporary: false);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"transcribe_{Guid.NewGuid():N}.wav");

        if (await TryConvertWithFfmpegAsync(inputPath, tempPath, ct))
        {
            logger.LogInformation("Converted audio with ffmpeg");
            return new NormalizedAudio(inputPath, tempPath, ReadDuration(tempPath), isTemporary: true);
        }

        if (isWav)
        {
            ResampleWithNAudio(inputPath, tempPath);
            logger.LogInformation("Resampled WAV with NAudio");
            return new NormalizedAudio(inputPath, tempPath, ReadDuration(tempPath), isTemporary: true);
        }

        throw new PipelineException("normalize",
            $"'{Path.GetExtension(inputPath)}' files need ffmpeg, which was not found at '{_options.FfmpegPath}'. " +
            "Install ffmpeg (winget install ffmpeg / apt install ffmpeg / brew install ffmpeg), " +
            "set Pipeline:FfmpegPath, or use a .wav file.");
    }

    private static bool IsWhisperReady(string path, out TimeSpan? duration)
    {
        duration = null;
        try
        {
            using var reader = new WaveFileReader(path);
            var f = reader.WaveFormat;
            duration = reader.TotalTime;
            return f.Encoding == WaveFormatEncoding.Pcm && f.SampleRate == TargetSampleRate &&
                   f.Channels == 1 && f.BitsPerSample == 16;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            return false; // not a readable RIFF/WAV, let ffmpeg try
        }
    }

    private async Task<bool> TryConvertWithFfmpegAsync(string input, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                     "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", output })
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new Win32Exception("Process.Start returned null");
        }
        catch (Win32Exception)
        {
            logger.LogWarning("ffmpeg not found at '{Path}', falling back to NAudio (WAV only)", _options.FfmpegPath);
            return false;
        }

        using (process)
        {
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
            {
                throw new PipelineException("normalize",
                    $"ffmpeg could not decode the file (exit code {process.ExitCode}): {stderr.Trim()}");
            }
        }

        return true;
    }

    private static void ResampleWithNAudio(string input, string output)
    {
        try
        {
            using var reader = new WaveFileReader(input);
            ISampleProvider samples = reader.ToSampleProvider();

            samples = samples.WaveFormat.Channels switch
            {
                1 => samples,
                2 => new StereoToMonoSampleProvider(samples),
                var n => throw new PipelineException("normalize",
                    $"{n}-channel WAV is not supported without ffmpeg. Install ffmpeg or downmix the file.")
            };

            if (samples.WaveFormat.SampleRate != TargetSampleRate)
            {
                samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);
            }

            WaveFileWriter.CreateWaveFile16(output, samples);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
        {
            throw new PipelineException("normalize",
                $"Unsupported WAV encoding ({ex.Message}). Install ffmpeg to convert it.", ex);
        }
    }

    private static TimeSpan? ReadDuration(string wavPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            return reader.TotalTime;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Used in mock mode: no conversion, the transcriber reads the file directly.</summary>
public sealed class PassThroughAudioLoader : IAudioNormalizer
{
    public Task<NormalizedAudio> NormalizeAsync(string inputPath, CancellationToken ct) =>
        Task.FromResult(new NormalizedAudio(inputPath, inputPath, duration: null, isTemporary: false));
}
