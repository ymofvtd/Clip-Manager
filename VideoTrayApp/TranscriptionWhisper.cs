using System.Diagnostics;
using System.Security.Cryptography;
using Whisper.net;
using Whisper.net.Ggml;

namespace VideoTrayApp;

internal sealed class TranscriptionWhisper(string dataFolder, TranscriptionSettings settings, IOperationProgress progress) : IDisposable
{
    private WhisperFactory? factory;
    private WhisperVadProcessor? vad;

    internal async Task<WhisperVadProcessor> GetVadAsync(CancellationToken ct)
    {
        if (vad is not null) return vad;
        try
        {
            string path = await EnsureModelAsync("ggml-silero-v6.2.0.bin", 885_098,
                "2AA269B785EEB53A82983A20501DDF7C1D9C48E33AB63A41391AC6C9F7FB6987",
                token => WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(cancellationToken: token), ct).ConfigureAwait(false);
            using var vadFactory = WhisperVadFactory.FromPath(path);
            return vad = vadFactory.CreateBuilder().WithUseGpu(false).WithThreads(1)
                .WithThreshold(0.35f).WithMinSpeechDuration(TimeSpan.FromMilliseconds(120))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(200)).WithSpeechPadding(TimeSpan.Zero).Build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new TranscriptionModelException($"Speech detection model could not start. {ModelError(ex)}", ex);
        }
    }

    internal async Task<WhisperFactory> GetRecognizerAsync(CancellationToken ct)
    {
        if (factory is not null) return factory;
        try
        {
            string path = await EnsureModelAsync("ggml-base.bin", 147_951_465,
                "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE",
                token => WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base, cancellationToken: token), ct).ConfigureAwait(false);
            progress.SetIndeterminate("Loading the Whisper speech model…");
            return factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new TranscriptionModelException($"Whisper Base could not start. {ModelError(ex)}", ex);
        }
    }

    internal static (string Language, bool Confident) ChooseLanguage(string? detected, float probability,
        double speechSeconds, string? previous)
    {
        if (detected is null) return ("en", false);
        float threshold = speechSeconds < 2.5 ? 0.83f : 0.62f;
        if (probability >= threshold) return (detected, true);
        if (detected == previous && probability >= 0.52f) return (detected, false);
        return ("en", false);
    }

    internal async Task<List<SegmentData>> DecodeAsync(WhisperFactory model, ReadOnlyMemory<float> samples, string language,
        string prompt, bool retry, Action<int> report, CancellationToken ct)
    {
        var builder = model.CreateBuilder().WithThreads(settings.Threads).WithNoContext().WithLanguage(language)
            .WithProbabilities().WithProgressHandler(report.Invoke);
        if (prompt.Length != 0) builder.WithPrompt(prompt);
        if (retry) builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(3)).WithTemperatureInc(0);
        await using var processor = builder.Build();
        var segments = new List<SegmentData>();
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            segments.Add(segment);
        return segments;
    }

    internal static async Task<(List<SegmentData>? Segments, int Retries)> ReviewAsync(SpeechChunk chunk,
        string language, bool confident, TranscriptionSettings settings,
        Func<string, string, bool, Task<List<SegmentData>>> decode, Action<string> log)
    {
        string prompt = language == "en" ? settings.EmotePrompt.Trim() : "";
        var watch = Stopwatch.StartNew();
        var segments = TranscriptionChunks.InChunk(await decode(language, prompt, false).ConfigureAwait(false), chunk);
        string? reason = TranscriptionChunks.Review(segments, chunk, settings.RepetitionLimit);
        if (reason is null && prompt.Length > 0 && string.Join(" ", segments.Select(s => s.Text.Trim()))
            .Contains(prompt, StringComparison.OrdinalIgnoreCase))
            reason = "the recognizer echoed the vocabulary prompt";
        log($"Decode {language}: {watch.Elapsed.TotalSeconds:F2}s wall, {segments.Count} lines"
            + (reason is null ? "" : $"; suspicious: {reason}"));
        if (reason is null) return (segments, 0);

        // Keep strong foreign-language evidence when retrying. Weak decisions get the English fallback.
        string retryLanguage = confident ? language : "en";
        log($"Retry 1/1 in {retryLanguage}; {reason}");
        watch.Restart();
        segments = TranscriptionChunks.InChunk(await decode(retryLanguage, "", true).ConfigureAwait(false), chunk);
        reason = TranscriptionChunks.Review(segments, chunk, settings.RepetitionLimit);
        log($"Retry decoded in {watch.Elapsed.TotalSeconds:F2}s wall, {segments.Count} lines"
            + (reason is null ? "" : $"; marked uncertain: {reason}"));
        return (reason is null ? segments : null, 1);
    }

    private async Task<string> EnsureModelAsync(string name, int bytes, string sha256,
        Func<CancellationToken, Task<Stream>> download, CancellationToken ct)
    {
        string folder = Path.Combine(dataFolder, "models");
        string path = Path.Combine(folder, name);
        if (new FileInfo(path) is { Exists: true } existing && existing.Length == bytes)
            return path;

        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            progress.SetIndeterminate($"Downloading {name} ({bytes / 1_000_000d:F1} MB)…");
            await using (var source = await download(ct).ConfigureAwait(false))
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(file, ct).ConfigureAwait(false);
                file.Position = 0;
                if (file.Length != bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != sha256)
                    throw new InvalidDataException($"The downloaded model failed verification: {name}.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally { File.Delete(temporary); }
    }

    private static string ModelError(Exception ex) => ex is DllNotFoundException
        ? "Check that the Microsoft Visual C++ Redistributable (x64) is installed."
        : ex.Message;

    public void Dispose()
    {
        vad?.Dispose();
        factory?.Dispose();
    }
}

internal sealed class TranscriptionModelException(string message, Exception innerException) : Exception(message, innerException);
