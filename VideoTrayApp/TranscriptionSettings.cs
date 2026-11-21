namespace VideoTrayApp;

internal sealed record TranscriptionSettings
{
    public int Threads { get; init; } = Math.Min(8, Environment.ProcessorCount);
    public int SpeechPaddingMs { get; init; } = 320;
    public int RepetitionLimit { get; init; } = 5;
    public string EmotePrompt { get; init; } = "";

    internal static TranscriptionSettings Load(string dataFolder)
    {
        var settings = AtomicJson.Load<TranscriptionSettings>(Path.Combine(dataFolder, "transcription_settings.json")) ?? new();
        if (settings.Threads < 1 || settings.Threads > Environment.ProcessorCount ||
            settings.SpeechPaddingMs is < 0 or > 1000 || settings.RepetitionLimit is < 3 or > 20 ||
            settings.EmotePrompt is null || settings.EmotePrompt.Length > 256 || settings.EmotePrompt.Contains('\0'))
            throw new InvalidDataException("Invalid transcription settings. Check Threads, SpeechPaddingMs, RepetitionLimit and EmotePrompt.");
        return settings;
    }
}
