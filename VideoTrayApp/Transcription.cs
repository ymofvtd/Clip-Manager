using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace VideoTrayApp;

internal static class Transcription
{
    private const int SampleRate = 16_000;
    private const int MissingAudioStream = unchecked((int)0xC00D36B3); // MF_E_INVALIDSTREAMNUMBER
    private const int ModelBytes = 147_951_465;
    private const string ModelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";

    internal static async Task<JobOutcome> RunAsync(string source, bool folder, bool recursive, string dataFolder,
        IOperationProgress progress, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        var errors = new List<string>();
        string[] clips;
        if (folder)
        {
            ActionPreset.ValidateFolder(source);
            progress.SetIndeterminate("Finding MP4 clips…");
            clips = ArchivePaths.Videos([source], VideoFiles.CreateExtensions(), errors, ct, recursive: recursive)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (clips.Length == 0 && errors.Count == 0) return JobOutcome.Complete("No MP4 clips found.");
        }
        else
        {
            VideoFiles.RequireMp4(source);
            clips = [source];
        }

        var details = new List<string>();
        int saved = 0, existing = 0, noSpeech = 0, processed = 0;
        WhisperFactory? factory = null;
        JobOutcome Outcome(JobState state) => new(state, string.Join(Environment.NewLine,
            new[] { $"Saved: {saved:N0} | Existing: {existing:N0} | No speech: {noSpeech:N0} | Errors: {errors.Count:N0}",
                $"Processed: {processed:N0} of {clips.Length:N0} clips" }.Concat(errors).Concat(details)));

        try
        {
            for (; processed < clips.Length; processed++)
            {
                ct.ThrowIfCancellationRequested();
                string clip = clips[processed];
                string name = Path.GetFileName(clip);
                string output = Path.ChangeExtension(clip, ".transcript.txt");
                progress.Report(processed, clips.Length, $"Reading audio: {name}");
                try
                {
                    ArchivePaths.RejectLinks(clip);
                    using var input = File.Open(clip, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (Path.Exists(output))
                    {
                        existing++;
                        continue;
                    }

                    var samples = ReadAudio(clip, ct);
                    if (samples.Span.IndexOfAnyExcept(0f) < 0)
                    {
                        noSpeech++;
                        details.Add($"No speech detected: {clip}");
                        continue;
                    }

                    if (factory is null)
                    {
                        try { factory = await OpenModelAsync(dataFolder, progress, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            errors.Add($"Speech model could not start: {ex.Message}");
                            return Outcome(JobState.Failed);
                        }
                    }

                    progress.Report(processed, clips.Length, $"Transcribing: {name}");
                    await using var processor = factory.CreateBuilder().WithLanguage("auto")
                        .WithProgressHandler(percent => progress.Report(processed, clips.Length, $"Transcribing: {name} ({percent}%)"))
                        .Build();
                    var transcript = new StringBuilder();
                    await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
                    {
                        if (!string.IsNullOrWhiteSpace(segment.Text)) transcript.AppendLine(segment.Text.Trim());
                    }
                    ct.ThrowIfCancellationRequested();
                    if (transcript.Length == 0)
                    {
                        noSpeech++;
                        details.Add($"No speech detected: {clip}");
                    }
                    else if (await SaveAsync(output, transcript.ToString(), ct).ConfigureAwait(false))
                    {
                        saved++;
                        details.Add($"Saved: {output}");
                    }
                    else existing++;
                }
                catch (COMException ex) when (ex.HResult == MissingAudioStream)
                {
                    noSpeech++;
                    details.Add($"No audio track: {clip}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{clip}: {ex.Message}");
                }
            }
            return Outcome(errors.Count == 0 ? JobState.Completed
                : saved + existing + noSpeech > 0 ? JobState.CompletedWithIssues : JobState.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Outcome(JobState.Cancelled); }
        finally { factory?.Dispose(); }
    }

    private static ReadOnlyMemory<float> ReadAudio(string clip, CancellationToken ct)
    {
        using var audio = new MediaFoundationReader(clip);
        using var resampler = new MediaFoundationResampler(audio, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        var samples = resampler.ToSampleProvider();
        var pcm = new ArrayBufferWriter<float>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = samples.Read(pcm.GetSpan(SampleRate)[..SampleRate]);
            if (read == 0) return pcm.WrittenMemory;
            pcm.Advance(read);
        }
    }

    private static async Task<WhisperFactory> OpenModelAsync(string dataFolder, IOperationProgress progress, CancellationToken ct)
    {
        string folder = Path.Combine(dataFolder, "models");
        string path = Path.Combine(folder, "ggml-base.bin");
        if (new FileInfo(path) is not { Exists: true, Length: ModelBytes })
        {
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
            try
            {
                progress.SetIndeterminate("Downloading the Whisper speech model (148 MB)…");
                await using (var download = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base, cancellationToken: ct).ConfigureAwait(false))
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
                {
                    await download.CopyToAsync(file, ct).ConfigureAwait(false);
                    file.Position = 0;
                    if (file.Length != ModelBytes || Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != ModelSha256)
                        throw new InvalidDataException("The downloaded model failed verification. Try the transcription again.");
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }

        ct.ThrowIfCancellationRequested();
        progress.SetIndeterminate("Loading the Whisper speech model…");
        try { return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false }); }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException)
        {
            throw new InvalidOperationException("Whisper could not load its native runtime. Check that Microsoft Visual C++ Redistributable (x64) is installed.", ex);
        }
    }

    private static async Task<bool> SaveAsync(string output, string transcript, CancellationToken ct)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(file))
                await writer.WriteAsync(transcript.AsMemory(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            try { File.Move(temporary, output); }
            catch (IOException) when (Path.Exists(output)) { return false; }
            return true;
        }
        finally { File.Delete(temporary); }
    }
}
