using System.Diagnostics;
using System.Text.RegularExpressions;
using VideoTrayApp;
using Whisper.net;

internal static class TranscriptionChecks
{
    private const int Rate = TranscriptionAudio.SampleRate;
    private const string Hint = "KEKW, PogChamp, Pog, LUL, monkaS, Sadge, Copium, Kappa, PepeLaugh";

    internal static async Task RunAsync(string root)
    {
        var settings = new TranscriptionSettings { EmotePrompt = Hint };
        Check(TranscriptionWhisper.ChooseLanguage("en", 0.99f, 12, null) == ("en", true), "English Twitch speech");
        Check(TranscriptionWhisper.ChooseLanguage("fr", 0.95f, 4, "en") == ("fr", true), "French between English utterances");
        Check(TranscriptionWhisper.ChooseLanguage("fr", 0.56f, 1, "fr") == ("fr", false), "neighboring French stabilizes a short reply");
        foreach (string language in new[] { "ja", "ko", "es" })
            Check(TranscriptionWhisper.ChooseLanguage(language, 0.91f, 15, "en") == (language, true), $"foreign playback in {language}");
        Check(TranscriptionWhisper.ChooseLanguage("de", 0.43f, 1, "en") == ("en", false), "ambiguous emotes fall back to English");

        int pad = Rate / 4;
        var empty = TranscriptionChunks.Plan([], 30 * Rate, 0, pad);
        Check(empty.Chunks.Count == 0 && empty.Cut == 25 * Rate, "silence and music classified as non-speech skip inference");
        var plan = TranscriptionChunks.Plan([Vad(1, 7), Vad(7.4, 22), Vad(24, 30)], 30 * Rate, 0, pad);
        Check(plan.Cut == 24 * Rate && plan.Chunks is [{ Start: Rate, End: 22 * Rate }], "short breaks merge and a natural gap owns the split");
        var briefPauses = TranscriptionChunks.Plan([Vad(1, 5), Vad(6, 10), Vad(11.05, 19), Vad(19.8, 22)], 24 * Rate, 0, pad);
        Check(briefPauses.Chunks is [{ Start: Rate, End: 19 * Rate }, { End: 22 * Rate }],
            "short phrases merge toward a 15-second decode and longer chunks break on natural pauses");
        var continuous = TranscriptionChunks.Plan([Vad(0, 30)], 30 * Rate, pad, pad);
        Check(continuous.Chunks is [{ Start: Rate / 4, End: 25 * Rate, ReadStart: 0, ReadEnd: 25 * Rate + Rate / 4 }],
            "continuous speech gets padding on both sides of the cut");
        Check(TranscriptionChunks.TrimOverlap("that was PogChamp!", "was PogChamp. Next game.").Text == "Next game.",
            "stitcher trims shared boundary words");
        Check(TranscriptionChunks.InChunk([Line("old", end: 0.2), Line("new", start: 0.3)], continuous.Chunks[0]).Single().Text == "new",
            "speech wholly in the previous chunk's padding is excluded");

        var chunk = new SpeechChunk(0, 15 * Rate, 0, 15 * Rate, 15 * Rate);
        List<SegmentData> captionLoop = Enumerable.Range(0, 100).Select(_ => Line("[Musik]")).ToList();
        Check(TranscriptionChunks.Review(captionLoop, chunk, 5) is not null && captionLoop.Count == 0, "repeated [Musik] is discarded");
        List<SegmentData> realRepeat = [Line("Pog! Pog! Pog! " + string.Concat(Enumerable.Repeat("Go go go! ", 10)))];
        Check(TranscriptionChunks.Review(realRepeat, chunk, 5) is null, "legitimate high-confidence repetitions and emotes survive");
        List<SegmentData> fakeRepeat = [Line(string.Join(" ", Enumerable.Repeat("well here we are", 6)), probability: 0.4f)];
        Check(TranscriptionChunks.Review(fakeRepeat, chunk, 5) is not null, "low-confidence phrase loop is caught");
        Check(TranscriptionChunks.Review(fakeRepeat, chunk, 10) is null, "repetition threshold is configurable");
        Check(TranscriptionChunks.Review([Line("faint nonsense", probability: 0.31f, noSpeech: 0.9f)], chunk, 5) is not null,
            "high no-speech probability and low confidence are flagged together");
        Check(TranscriptionChunks.Review([Line(new string('a', 1000))], chunk, 5) is not null, "implausible text density is caught");

        var calls = new List<(string Language, string Prompt, bool Retry)>();
        Task<List<SegmentData>> Decode(string language, string prompt, bool retry)
        {
            calls.Add((language, prompt, retry));
            return Task.FromResult<List<SegmentData>>(retry ? [Line("That was PogChamp!")] : [Line("[Musik] [Musik]")]);
        }
        var repaired = await TranscriptionWhisper.ReviewAsync(chunk, "de", false, settings, Decode, _ => { });
        Check(calls.SequenceEqual([("de", "", false), ("en", "", true)]) && repaired.Segments?.Single().Text == "That was PogChamp!",
            "ambiguous suspicious output retries once in English without hints");
        calls.Clear();
        await TranscriptionWhisper.ReviewAsync(chunk, "en", true, settings, Decode, _ => { });
        Check(calls.SequenceEqual([("en", Hint, false), ("en", "", true)]), "Twitch vocabulary is an optional first-pass English hint");
        var echo = await TranscriptionWhisper.ReviewAsync(chunk, "en", true, settings,
            (_, _, retry) => Task.FromResult<List<SegmentData>>([Line(retry ? "Welcome to the stream." : Hint)]), _ => { });
        Check(echo.Retries == 1 && echo.Segments?.Single().Text == "Welcome to the stream.", "a verbatim hint echo triggers an unprompted retry");
        var uncertain = await TranscriptionWhisper.ReviewAsync(chunk, "fr", true, settings, (language, _, _) =>
        {
            Check(language == "fr", "strong French evidence survives retry");
            return Task.FromResult<List<SegmentData>>([Line("[Musik]")]);
        }, _ => { });
        Check(uncertain.Segments is null && uncertain.Retries == 1, "failed retry leaves the speech uncertain");

        string folder = Directory.CreateDirectory(Path.Combine(root, "Transcription fixtures")).FullName;
        string corrupt = Path.Combine(folder, "corrupt.mp4");
        await File.WriteAllTextAsync(corrupt, "invalid MP4");
        var result = await Transcription.RunAsync(corrupt, false, false, folder, new TestProgress(), CancellationToken.None);
        Check(result.State == JobState.Failed && !File.Exists(Output(corrupt)) && File.ReadAllText(Path.Combine(folder, "transcription.log")).Contains("Failed"),
            "decoder errors are logged and produce no transcript");

        const string original = "Original plain-text transcript.";
        await File.WriteAllTextAsync(Output(corrupt), original);
        var failedUpdate = await Transcription.RunAsync(corrupt, false, false, folder, new TestProgress(), CancellationToken.None);
        Check(failedUpdate.State == JobState.Failed && await File.ReadAllTextAsync(Output(corrupt)) == original
            && !File.Exists(TimestampedCopy(corrupt)), "a failed timestamp upgrade preserves the original");

        const string timed = "[123:00:00.000 - 123:00:02.500] Already timestamped.";
        await File.WriteAllTextAsync(Output(corrupt), timed);
        var skipped = await Transcription.RunAsync(corrupt, false, false, folder, new TestProgress(), CancellationToken.None);
        Check(skipped.State == JobState.Completed && skipped.Details.Contains("Existing: 1")
            && await File.ReadAllTextAsync(Output(corrupt)) == timed && !File.Exists(TimestampedCopy(corrupt)),
            "an existing timestamped transcript is skipped without reading audio");
        Console.WriteLine("Transcription checks passed.");
    }

    internal static async Task SmokeAsync(string root, string media, string models)
    {
        CopyModels(root, models);
        async Task<(JobOutcome Outcome, string Transcript)> Run(string name)
        {
            string path = CopyClip(root, Path.Combine(media, name + ".mp4"), name);
            var outcome = await Transcription.RunAsync(path, false, false, root, new TestProgress(), CancellationToken.None);
            string transcript = File.Exists(Output(path)) ? await File.ReadAllTextAsync(Output(path)) : "";
            Console.WriteLine($"{name}: {outcome.State}\n{transcript}{File.ReadAllText(Path.Combine(root, "transcription.log"))}");
            return (outcome, transcript);
        }

        var english = await Run("english");
        Check(english.Outcome.State == JobState.Completed && english.Transcript.Contains("landing a man on the moon", StringComparison.OrdinalIgnoreCase),
            "English public speech is recognizable through MP4 decoding");
        var mixed = await Run("mixed");
        Check(mixed.Outcome.State == JobState.Completed &&
            mixed.Transcript.Contains("moon", StringComparison.OrdinalIgnoreCase) &&
            mixed.Transcript.Contains("livre premier", StringComparison.OrdinalIgnoreCase) &&
            mixed.Transcript.Contains("[00:01:03", StringComparison.Ordinal),
            "English with backing music, a French interlude, and speech after a long gap have original video timestamps");
        var starts = Regex.Matches(mixed.Transcript, @"^\[(\d\d:\d\d:\d\d\.\d{3})", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Check(starts.SequenceEqual(starts.Order(StringComparer.Ordinal)), "timestamps increase monotonically across windows");
        var foreign = await Run("foreign");
        Check(foreign.Outcome.State == JobState.Completed && foreign.Transcript.Contains("libro primero", StringComparison.OrdinalIgnoreCase),
            "Spanish playback stays in Spanish");
        foreach (string name in new[] { "music", "silent", "no-audio" })
        {
            var nonSpeech = await Run(name);
            Check(nonSpeech.Outcome.State == JobState.Completed && nonSpeech.Transcript.Length == 0, $"{name} yields no fabricated speech");
        }
        var tooShort = await Run("short-audio");
        Check(tooShort.Outcome.State == JobState.Completed && tooShort.Transcript.Length > 0,
            "audio that intentionally ends before the video still transcribes");
        var shortAudioEnds = Regex.Matches(tooShort.Transcript, @" - (\d\d:\d\d:\d\d\.\d{3})\]")
            .Select(m => m.Groups[1].Value).ToArray();
        Check(shortAudioEnds.Length > 0 && shortAudioEnds.All(end => string.CompareOrdinal(end, "00:00:02.000") <= 0),
            "decoder padding does not extend timestamps past the declared audio track");
        var truncated = await Run("truncated-audio");
        Check(truncated.Outcome.State == JobState.Failed && truncated.Transcript.Length == 0 &&
            truncated.Outcome.Details.Contains("expected end of the audio track"), "unexpectedly short audio fails visibly");
        var delayed = await Run("delayed");
        Check(delayed.Outcome.State == JobState.Completed && delayed.Transcript.StartsWith("[00:00:06", StringComparison.Ordinal),
            "delayed audio starts at the original MP4 presentation time");

        string legacyClip = Path.Combine(root, "delayed.mp4");
        const string legacyText = "Original plain-text transcript with manual edits.";
        await File.WriteAllTextAsync(Output(legacyClip), legacyText);
        var upgraded = await Transcription.RunAsync(legacyClip, false, false, root, new TestProgress(), CancellationToken.None);
        string timestampedCopy = TimestampedCopy(legacyClip);
        Check(upgraded.State == JobState.Completed && upgraded.Details.Contains(timestampedCopy)
            && (await File.ReadAllTextAsync(timestampedCopy)).StartsWith("[00:00:06", StringComparison.Ordinal)
            && await File.ReadAllTextAsync(Output(legacyClip)) == legacyText,
            "a legacy transcript gets a timestamped copy at the original video time and keeps manual edits");
        var skippedUpgrade = await Transcription.RunAsync(legacyClip, false, false, root, new TestProgress(), CancellationToken.None);
        Check(skippedUpgrade.State == JobState.Completed && skippedUpgrade.Details.Contains("Existing: 1")
            && skippedUpgrade.Details.Contains(timestampedCopy), "re-running a legacy clip finds its timestamped copy");

        AtomicJson.Save(Path.Combine(root, "transcription_settings.json"), new TranscriptionSettings { EmotePrompt = Hint });
        var emotes = await Run("emotes");
        Check(emotes.Outcome.State == JobState.Completed &&
            Regex.IsMatch(emotes.Transcript, @"pog\s*champ", RegexOptions.IgnoreCase) &&
            emotes.Transcript.Contains("Kappa", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(emotes.Transcript, @"Pog[!, ]+Pog[!, ]+Pog\b", RegexOptions.IgnoreCase),
            "hinted emotes are recognized and a spoken repetition is preserved");
        string control = CopyClip(root, Path.Combine(media, "english.mp4"), "hint-control");
        var controlOutcome = await Transcription.RunAsync(control, false, false, root, new TestProgress(), CancellationToken.None);
        Check(controlOutcome.State == JobState.Completed && !Regex.IsMatch(await File.ReadAllTextAsync(Output(control)),
            @"\b(?:KEKW|PogChamp|Pog|LUL|monkaS|Sadge|Copium|Kappa|PepeLaugh)\b", RegexOptions.IgnoreCase),
            "hints do not add emote words to the English control");
        File.Delete(Path.Combine(root, "transcription_settings.json"));

        string batch = Directory.CreateDirectory(Path.Combine(root, "cancel")).FullName;
        string first = CopyClip(batch, Path.Combine(media, "english.mp4"), "01");
        string second = CopyClip(batch, Path.Combine(media, "mixed.mp4"), "02");
        using var cancellation = new CancellationTokenSource();
        var progress = new TestProgress { OnMessage = message =>
        {
            if (message.StartsWith("Transcribing: 02.mp4", StringComparison.Ordinal)) cancellation.Cancel();
        } };
        var cancelled = await Transcription.RunAsync(batch, true, false, root, progress, cancellation.Token);
        Check(cancelled.State == JobState.Cancelled && cancelled.Details.Contains("Stopped around") && File.Exists(Output(first))
            && !File.Exists(Output(second)) && !Directory.EnumerateFiles(batch, "*.tmp").Any(),
            "cancellation reports partial audio progress and keeps completed transcripts");
        Console.WriteLine("Real-audio transcription smoke tests passed.");
    }

    internal static async Task BenchmarkAsync(string root, string source, string models)
    {
        CopyModels(root, models);
        var threadCounts = new[] { 4, 8, 12, 16, 24, 32 }.Where(threads => threads <= Environment.ProcessorCount).ToArray();
        foreach (int threads in threadCounts.Concat(threadCounts.Reverse()))
        {
            AtomicJson.Save(Path.Combine(root, "transcription_settings.json"), new TranscriptionSettings { Threads = threads });
            string clip = CopyClip(root, source, Guid.NewGuid().ToString("N"));
            var watch = Stopwatch.StartNew();
            var result = await Transcription.RunAsync(clip, false, false, root, new TestProgress(), CancellationToken.None);
            Check(result.State == JobState.Completed && File.Exists(Output(clip)), "benchmark run transcribes successfully");
            Console.WriteLine($"{threads} threads: {watch.Elapsed.TotalSeconds:F2}s");
        }
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"Peak benchmark process working set: {process.PeakWorkingSet64 / (1024d * 1024):F0} MiB");
    }

    private static void CopyModels(string root, string source)
    {
        string destination = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
        File.Copy(Path.Combine(source, "ggml-base.bin"), Path.Combine(destination, "ggml-base.bin"));
        const string vad = "ggml-silero-v6.2.0.bin";
        if (File.Exists(Path.Combine(source, vad)))
            File.Copy(Path.Combine(source, vad), Path.Combine(destination, vad));
    }

    private static string CopyClip(string dest, string source, string name)
    {
        string path = Path.Combine(dest, name + ".mp4");
        File.Copy(source, path);
        return path;
    }

    private static string Output(string path) => Path.ChangeExtension(path, ".transcript.txt");

    private static string TimestampedCopy(string path) => Path.ChangeExtension(path, ".transcript.timestamped.txt");

    private static VadSegmentData Vad(double start, double end) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    private static SegmentData Line(string text, float probability = 0.9f, float noSpeech = 0.1f, double start = 0, double end = 2) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), probability, probability, probability, noSpeech, "en", []);

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Transcription check failed: " + description);
    }
}
