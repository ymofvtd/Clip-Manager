using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using static VideoTrayApp.TranscriptionAudio;

namespace VideoTrayApp;

internal static partial class Transcription
{
    private const int MissingAudioStream = unchecked((int)0xC00D36B3); // MF_E_INVALIDSTREAMNUMBER
    private const string Uncertain = "[Uncertain audio]";

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

        var settings = TranscriptionSettings.Load(dataFolder);
        Directory.CreateDirectory(dataFolder);
        string logPath = Path.Combine(dataFolder, "transcription.log");
        using var logFile = new StreamWriter(logPath) { AutoFlush = true };
        void Log(string message) => logFile.WriteLine($"{DateTimeOffset.Now:O} {message}");
        var watch = Stopwatch.StartNew();
        using var whisper = new TranscriptionWhisper(dataFolder, settings, progress);
        Log($"Started {clips.Length} clip(s); Whisper Base multilingual CPU, {settings.Threads} threads, "
            + $"{settings.SpeechPaddingMs}ms speech padding, repetition limit {settings.RepetitionLimit}, "
            + $"English prompt {(string.IsNullOrWhiteSpace(settings.EmotePrompt) ? "off" : "on")}");

        var details = new List<string>();
        int saved = 0, existing = 0, noSpeech = 0, uncertainClips = 0, processed = 0;
        TimeSpan examined = TimeSpan.Zero;
        int units = Math.Min(1000, int.MaxValue / Math.Max(1, clips.Length));
        JobOutcome Outcome(JobState state)
        {
            Log($"{state} after {watch.Elapsed.TotalSeconds:F2}s wall; {processed}/{clips.Length} files completed");
            return new(state, string.Join(Environment.NewLine,
                new[] { $"Saved: {saved:N0} | Existing: {existing:N0} | No speech: {noSpeech:N0} | Uncertain: {uncertainClips:N0} | Errors: {errors.Count:N0}",
                    $"Processed: {processed:N0} of {clips.Length:N0} clips", $"Diagnostics: {logPath}" }.Concat(errors).Concat(details)));
        }

        try
        {
            for (; processed < clips.Length; processed++)
            {
                examined = TimeSpan.Zero;
                ct.ThrowIfCancellationRequested();
                string clip = clips[processed];
                string name = Path.GetFileName(clip);
                string output = Path.ChangeExtension(clip, ".transcript.txt");
                progress.Report(processed * units, clips.Length * units, $"Reading audio: {name}");
                try
                {
                    ArchivePaths.RejectLinks(clip);
                    using var input = File.Open(clip, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (Path.Exists(output))
                    {
                        if (!File.Exists(output) || HasTimestamps(output, ct))
                        {
                            existing++;
                            continue;
                        }

                        output = Path.ChangeExtension(clip, ".transcript.timestamped.txt");
                        if (Path.Exists(output))
                        {
                            existing++;
                            details.Add($"Existing timestamped copy: {output}");
                            continue;
                        }
                    }

                    Log($"Opening {clip}");
                    using var audio = new TranscriptionAudio(clip, input, Log, ct);
                    var audioEnd = audio.AudioEnd ?? audio.MediaDuration;
                    int clipProgress = 0;
                    void Report(long sample, string stage)
                    {
                        if (ct.IsCancellationRequested) return;
                        examined = TimeSpan.FromTicks(sample * TicksPerSample);
                        if (audioEnd <= TimeSpan.Zero)
                        {
                            progress.SetIndeterminate($"{stage}: {name} ({examined:c})");
                            return;
                        }

                        int fraction = (int)Math.Clamp(examined.TotalSeconds / audioEnd.TotalSeconds * units, 0, units - 1);
                        clipProgress = Math.Max(clipProgress, fraction);
                        progress.Report(processed * units + clipProgress, clips.Length * units,
                            $"{stage}: {name} ({examined:c} / {audioEnd:c})");
                    }

                    var (transcript, uncertain) = await ProcessClipAsync(audio, audioEnd, whisper, settings, Report, Log, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (transcript.Length == 0)
                    {
                        noSpeech++;
                        details.Add($"No speech detected: {clip}");
                    }
                    else if (await SaveAsync(output, transcript, ct).ConfigureAwait(false))
                    {
                        saved++;
                        if (uncertain > 0)
                        {
                            uncertainClips++;
                            details.Add($"Saved with {uncertain:N0} uncertain speech chunks: {output}");
                        }
                        else details.Add($"Saved: {output}");
                    }
                    else existing++;
                }
                catch (COMException ex) when (ex.HResult == MissingAudioStream)
                {
                    noSpeech++;
                    details.Add($"No audio track: {clip}");
                    Log($"No audio track: {clip}");
                }
                catch (TranscriptionModelException ex)
                {
                    errors.Add(ex.Message);
                    Log(ex.ToString());
                    return Outcome(JobState.Failed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    errors.Add($"{clip}: {ex.Message}");
                    Log($"Failed {clip}: {ex}");
                }
            }
            progress.Report(clips.Length * units, clips.Length * units, "Transcription finished.");
            return Outcome(errors.Count == 0 && uncertainClips == 0 ? JobState.Completed
                : saved + existing + noSpeech > 0 ? JobState.CompletedWithIssues : JobState.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (processed < clips.Length && examined > TimeSpan.Zero)
                details.Add($"Stopped around {examined:c} in {Path.GetFileName(clips[processed])}. Completed transcripts were kept.");
            return Outcome(JobState.Cancelled);
        }
    }

    private static async Task<(string Transcript, int Uncertain)> ProcessClipAsync(TranscriptionAudio audio, TimeSpan audioEnd,
        TranscriptionWhisper whisper, TranscriptionSettings settings, Action<long, string> report, Action<string> log, CancellationToken ct)
    {
        var time = Stopwatch.StartNew();
        var transcript = new StringBuilder();
        var buffer = new float[TranscriptionChunks.WindowSamples];
        int padding = settings.SpeechPaddingMs * SampleRate / 1000;
        int length = 0, prefix = 0, speechChunks = 0, uncertain = 0, retries = 0;
        long offset = audio.StartSample, previousSpeechEnd = -1;
        string? previousLanguage = null;
        (TimeSpan End, string Text)? previousLine = null;
        TimeSpan writtenThrough = TimeSpan.Zero;
        try
        {
            while (true)
            {
                length += audio.Read(buffer.AsSpan(length), ct);
                if (length <= prefix || audio.SamplesRead < SampleRate / 20) break;
                var window = buffer.AsMemory(0, length);
                report(offset + prefix, "Finding speech");
                IReadOnlyList<VadSegmentData> speech = [];
                if (window.Span.IndexOfAnyExcept(0f) >= 0)
                {
                    var vad = await whisper.GetVadAsync(ct).ConfigureAwait(false);
                    speech = await vad.DetectSpeechAsync(window, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                var (cut, chunks) = TranscriptionChunks.Plan(speech, length, prefix, padding);
                foreach (var chunk in chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    speechChunks++;
                    var start = TimeSpan.FromTicks((offset + chunk.Start) * TicksPerSample);
                    var end = TimeSpan.FromTicks((offset + chunk.End) * TicksPerSample);
                    var readStart = TimeSpan.FromTicks((offset + chunk.ReadStart) * TicksPerSample);
                    var samples = window[chunk.ReadStart..chunk.ReadEnd];
                    report(offset + chunk.Start, "Detecting language");
                    var model = await whisper.GetRecognizerAsync(ct).ConfigureAwait(false);
                    using var detector = model.CreateBuilder().WithThreads(settings.Threads).Build();
                    var (detected, probability) = detector.DetectLanguageWithProbability(window.Span[chunk.Start..chunk.End]);
                    ct.ThrowIfCancellationRequested();
                    string? neighbor = offset + chunk.Start - previousSpeechEnd <= 3 * SampleRate ? previousLanguage : null;
                    var (language, confident) = TranscriptionWhisper.ChooseLanguage(detected, probability,
                        chunk.SpeechSamples / (double)SampleRate, neighbor);
                    log($"Speech {speechChunks} {start:c} to {end:c}; detected {detected ?? "unknown"} ({probability:P0}), decoding {language}");
                    int furthest = 0;
                    var (segments, attemptedRetries) = await TranscriptionWhisper.ReviewAsync(chunk, language, confident, settings,
                        (selected, prompt, retry) => whisper.DecodeAsync(model, samples, selected, prompt, retry, percent =>
                        {
                            // Whisper progress restarts on retry; hold the furthest audio position already reported.
                            furthest = Math.Max(furthest, Math.Clamp(percent, 0, 99));
                            report(offset + chunk.Start + (chunk.End - chunk.Start) * furthest / 100,
                                retry ? "Retrying speech" : "Transcribing");
                        }, ct), log).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    retries += attemptedRetries;
                    if (segments is null)
                    {
                        uncertain++;
                        Append(start, end, Uncertain);
                        previousLanguage = null;
                        previousLine = null;
                    }
                    else
                    {
                        previousLanguage = confident ? language : null;
                        previousSpeechEnd = offset + chunk.End;
                        var precedingChunk = previousLine;
                        foreach (var segment in segments)
                        {
                            string text = segment.Text.ReplaceLineEndings(" ").Trim();
                            var rawStart = readStart + segment.Start;
                            var rawEnd = readStart + segment.End;
                            if (precedingChunk is { } last && rawStart < last.End && rawStart < start)
                            {
                                var (trimmed, removed) = TranscriptionChunks.TrimOverlap(last.Text, text);
                                if (removed >= 2 || (removed == 1 && rawEnd <= start + TimeSpan.FromMilliseconds(settings.SpeechPaddingMs)))
                                    text = trimmed;
                            }
                            if (text.Length == 0) continue;
                            Append(rawStart < start ? start : rawStart, rawEnd > end ? end : rawEnd, text);
                            previousLine = (rawEnd, text);
                            precedingChunk = null;
                        }
                    }

                    report(offset + chunk.End, "Transcribing");
                }

                report(offset + cut, "Audio processed");
                if (length < buffer.Length) break;
                int keepFrom = cut - padding;
                buffer.AsSpan(keepFrom, length - keepFrom).CopyTo(buffer);
                length -= keepFrom;
                offset += keepFrom;
                prefix = padding;
            }

            audio.Validate();
            return (transcript.ToString(), uncertain);
        }
        finally
        {
            double seconds = audio.SamplesRead / (double)SampleRate;
            log($"Decoded {seconds:F2}s; {speechChunks} speech chunks, {retries} retries, {uncertain} uncertain; "
                + $"{time.Elapsed.TotalSeconds:F2}s wall, {seconds / Math.Max(0.001, time.Elapsed.TotalSeconds):F2}x real time");
        }

        void Append(TimeSpan start, TimeSpan end, string text)
        {
            if (start < writtenThrough) start = writtenThrough;
            // The decoder may return a little padding after the declared audio track.
            if (audioEnd > TimeSpan.Zero)
            {
                if (start >= audioEnd) return;
                if (end > audioEnd) end = audioEnd;
            }
            if (end < start) end = start;
            transcript.AppendLine($"[{Timestamp(start)} - {Timestamp(end)}] {text}");
            writtenThrough = end;
        }
    }

    private static string Timestamp(TimeSpan time) =>
        $"{(long)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";

    private static bool HasTimestamps(string path, CancellationToken ct)
    {
        ArchivePaths.RejectLinks(path);
        bool hasTimedLine = false;
        foreach (string line in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!TimestampPrefix().IsMatch(line)) return false;
            hasTimedLine = true;
        }
        return hasTimedLine;
    }

    [GeneratedRegex(@"^\[[0-9]{2,}:[0-5][0-9]:[0-5][0-9]\.[0-9]{3} - [0-9]{2,}:[0-5][0-9]:[0-5][0-9]\.[0-9]{3}\] ")]
    private static partial Regex TimestampPrefix();

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
