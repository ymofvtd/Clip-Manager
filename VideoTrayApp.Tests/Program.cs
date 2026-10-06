using VideoTrayApp;

// Integration checks use generated AVI headers and isolated, disposable files.
string root = Path.Combine(Path.GetTempPath(), "ClipIdentifierTests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
    string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
    string reference = Path.Combine(source, "reference.avi");
    WriteAvi(reference, 2, 11);
    string duplicate = Path.Combine(nested, "renamed.AVI");
    File.Copy(reference, duplicate);
    string differentContent = Path.Combine(source, "different-content.avi");
    WriteAvi(differentContent, 2, 12);
    WriteAvi(Path.Combine(source, "different-duration.avi"), 3, 11);
    File.WriteAllBytes(Path.Combine(source, "unreadable.avi"), new byte[new FileInfo(reference).Length]);
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi" };
    var progress = new TestProgress();

    var found = ClipIdentifier.Find(reference, source, extensions, progress, CancellationToken.None);
    Check(found.Matches.SequenceEqual([duplicate]), "Recursive matching excludes reference and rejects same-size, same-duration content with a different hash");
    Check(found.Errors.Count == 1, "Unreadable clips are reported without aborting the search");

    string existing = Path.Combine(destination, "renamed.AVI");
    File.WriteAllText(existing, "keep this destination file");
    string moved = ClipIdentifier.Apply(found, reference, destination, progress, CancellationToken.None);
    Check(!File.Exists(duplicate) && File.Exists(Path.Combine(destination, "renamed__1.AVI")), "Move handles a filename collision");
    Check(File.ReadAllText(existing) == "keep this destination file" && File.Exists(reference), "Existing destination and reference are preserved");
    Check(moved.Contains("Moved: 1"), "Move summary counts completed actions");

    File.Copy(reference, duplicate);
    var changed = ClipIdentifier.Find(reference, source, extensions, progress, CancellationToken.None);
    WriteAvi(duplicate, 2, 99);
    string rejected = ClipIdentifier.Apply(changed, reference, destination, progress, CancellationToken.None);
    Check(File.Exists(duplicate) && rejected.Contains("Errors: 1"), "Files changed after search remain untouched");

    File.Copy(reference, duplicate, overwrite: true);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    bool cancelled = false;
    try { ClipIdentifier.Find(reference, source, extensions, progress, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Search supports cancellation");
    string cancelledAction = ClipIdentifier.Apply(found, reference, destination, progress, cts.Token);
    Check(File.Exists(duplicate) && cancelledAction.Contains("Moved: 0"), "Cancelled actions preserve files and report no completed moves");

    var alreadyThere = new ClipSearchResult(found.Reference, [reference], []);
    string excluded = ClipIdentifier.Apply(alreadyThere, reference, null, progress, CancellationToken.None);
    Check(File.Exists(reference) && excluded.Contains("Sent to Recycle Bin: 0"), "Action layer also protects reference clip");
    var inDestination = new ClipSearchResult(found.Reference, [Path.Combine(destination, "renamed__1.AVI")], []);
    string skipped = ClipIdentifier.Apply(inDestination, reference, destination, progress, CancellationToken.None);
    Check(skipped.Contains("Skipped (already in destination): 1"), "Clips already in destination are left in place");

    // Only this generated duplicate is sent to the Recycle Bin.
    string deleted = ClipIdentifier.Apply(found, reference, null, progress, CancellationToken.None);
    Check(!File.Exists(duplicate) && File.Exists(reference) && deleted.Contains("Sent to Recycle Bin: 1"), "Delete recycles the match and preserves the reference");

    string first = Path.Combine(nested, "first.avi");
    string second = Path.Combine(nested, "second.avi");
    File.Copy(reference, first);
    File.Copy(reference, second);
    using var partialCts = new CancellationTokenSource();
    var partialProgress = new TestProgress { OnReport = current => { if (current == 1) partialCts.Cancel(); } };
    var twoMatches = new ClipSearchResult(found.Reference, [first, second], []);
    string partial = ClipIdentifier.Apply(twoMatches, reference, destination, partialProgress, partialCts.Token);
    Check(!File.Exists(first) && File.Exists(second) && partial.Contains("Moved: 1") && partial.Contains("Unprocessed: 1"),
        "Cancellation after a move reports partial completion and preserves remaining matches");
    Console.WriteLine("All Identify clip integration checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static void WriteAvi(string path, uint frames, byte content)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    void FourCc(string text) => writer.Write(System.Text.Encoding.ASCII.GetBytes(text));
    FourCc("RIFF"); writer.Write(80u); FourCc("AVI ");
    FourCc("LIST"); writer.Write(192u); FourCc("hdrl");
    FourCc("avih"); writer.Write(56u);
    writer.Write(1_000_000u); // one second per frame
    writer.Write(0u); writer.Write(0u); writer.Write(0u);
    writer.Write(frames);
    writer.Write(0u); writer.Write(0u); writer.Write(0u);
    writer.Write(1u); writer.Write(1u); // width and height
    writer.Write(new byte[16]);
    FourCc("LIST"); writer.Write(116u); FourCc("strl");
    FourCc("strh"); writer.Write(56u);
    FourCc("vids"); FourCc("DIB ");
    writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0);
    writer.Write(0u); writer.Write(1u); writer.Write(1u); writer.Write(0u);
    writer.Write(frames); writer.Write(3u); writer.Write(uint.MaxValue); writer.Write(0u);
    writer.Write((short)0); writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);
    FourCc("strf"); writer.Write(40u);
    writer.Write(40u); writer.Write(1); writer.Write(1);
    writer.Write((ushort)1); writer.Write((ushort)24);
    writer.Write(0u); writer.Write(3u); writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
    // An ignored chunk provides differing content without changing size/duration.
    FourCc("JUNK"); writer.Write(2u); writer.Write(content); writer.Write((byte)0);
    stream.Position = 4;
    writer.Write((uint)(stream.Length - 8));
}

sealed class TestProgress : IOperationProgress
{
    public Action<int>? OnReport { get; init; }
    public CancellationToken CancellationToken => CancellationToken.None;
    public void Report(int current, int total, string message) => OnReport?.Invoke(current);
    public void SetIndeterminate(string message) { }
}
