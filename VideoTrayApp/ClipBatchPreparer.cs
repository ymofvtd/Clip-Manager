namespace VideoTrayApp;

internal static class ClipBatchPreparer
{
    internal static string Prepare(string sourceFolder, string destinationFolder, TimeSpan durationLimit,
        ISet<string> videoExtensions, IOperationProgress progress, CancellationToken ct,
        string numberedExtension = ".mp4", Random? shuffleRandom = null,
        IReadOnlyList<string>? candidates = null)
    {
        VideoFiles.RequireMp4("clip" + numberedExtension);
        sourceFolder = Path.GetFullPath(sourceFolder);
        destinationFolder = Path.GetFullPath(destinationFolder);
        ActionPreset.ValidateFolder(sourceFolder);
        ActionPreset.ValidateFolder(destinationFolder);
        var paths = candidates ?? Directory.EnumerateFiles(sourceFolder)
            .Where(path => VideoFiles.IsMp4(path)
                && Path.GetFileNameWithoutExtension(path).Length > 0
                && Path.GetFileNameWithoutExtension(path).All(char.IsAsciiDigit)).ToList();
        var plan = PrepareBatchPlan.AnalyzeCandidates(sourceFolder, Path.GetDirectoryName(destinationFolder) ?? destinationFolder,
            new(destinationFolder, false), durationLimit, paths, videoExtensions, progress, ct, shuffleRandom,
            candidates is null ? BatchSelection.NumberedMp4 : null);
        return plan.Execute(progress, ct);
    }
}
