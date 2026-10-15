using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class DateLookupChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "date-source")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        var created = new DateTimeOffset(2025, 3, 4, 18, 30, 0, TimeSpan.Zero);
        string clip = Path.Combine(source, "original.mp4"); write(clip, 2, 17);
        byte[] bytes = File.ReadAllBytes(clip);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44), checked((uint)Seconds(created)));
        File.WriteAllBytes(clip, bytes);
        string copied = Path.Combine(nested, "renamed.MP4"); File.Copy(clip, copied);
        File.SetCreationTimeUtc(copied, new DateTime(2026, 1, 1));
        File.SetLastWriteTimeUtc(copied, new DateTime(2026, 2, 2));
        write(Path.Combine(source, "unknown.mp4"), 2, 18);
        string invalid = Path.Combine(source, "invalid.mp4"); File.WriteAllBytes(invalid, [0, 0, 0, 7, 109, 111, 111, 118]);
        File.Copy(clip, Path.Combine(source, "ignored.mov"));
        string future = Path.Combine(source, "version1.mp4");
        var futureDate = new DateTimeOffset(2045, 8, 9, 0, 0, 0, TimeSpan.Zero);
        WriteMetadata(future, 1, Seconds(futureDate), extended: true);
        Check(ClipDateLookup.ReadCreationTime(future, CancellationToken.None) == futureDate,
            "64-bit movie timestamps and extended atom sizes are supported");
        string zeroSize = Path.Combine(nested, "zero-size.mp4");
        WriteMetadata(zeroSize, 0, Seconds(created), toEnd: true);
        Check(ClipDateLookup.ReadCreationTime(zeroSize, CancellationToken.None) == created,
            "Movie atoms extending to end-of-file are supported");
        string parserCases = Directory.CreateDirectory(Path.Combine(parent, "date-parser-cases")).FullName;
        string sentinel = Path.Combine(parserCases, "unset.mp4");
        WriteMetadata(sentinel, 1, ulong.MaxValue);
        Check(ClipDateLookup.ReadCreationTime(sentinel, CancellationToken.None) is null,
            "Unset 64-bit timestamps stay unknown instead of overflowing");
        string broken = Path.Combine(parserCases, "outside-parent.mp4");
        byte[] malformed = File.ReadAllBytes(zeroSize);
        BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(8), uint.MaxValue);
        File.WriteAllBytes(broken, malformed);
        bool invalidBounds = false;
        try { ClipDateLookup.ReadCreationTime(broken, CancellationToken.None); }
        catch (InvalidDataException) { invalidBounds = true; }
        Check(invalidBounds, "Malformed child atoms cannot escape their containing movie atom");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "Test UTC+8", "Test UTC+8");
        var day = new DateOnly(2025, 3, 5);
        var found = ClipDateLookup.Find(source, day, day, true, false, zone, new TestProgress(), CancellationToken.None);
        Check(found.Matches.Count == 3 && found.Matches.All(c => c.Created == created) && found.Matches.Any(c => c.Path == copied),
            "Copied/renamed clips match embedded creation dates regardless of filesystem dates; local calendar boundaries are inclusive");
        Check(found.Unknown == 1 && found.Errors.Count == 1 && found.Scanned == 6,
            "Unset dates, malformed files, non-MP4 files and valid dates outside the range are distinguished");
        var unknown = ClipDateLookup.Find(source, day, day, false, true, zone, new TestProgress(), CancellationToken.None);
        Check(unknown.Matches.Count == 2 && unknown.Matches.Count(c => c.Created is null) == 1,
            "Optional unknown dates and nonrecursive searches work without a filesystem-date fallback");
        var utc = ClipDateLookup.Find(source, day, day, true, false, TimeZoneInfo.Utc, new TestProgress(), CancellationToken.None);
        Check(utc.Matches.Count == 0, "Date filtering uses the selected time zone rather than assuming UTC calendar dates");
        bool rejected = false;
        try { ClipDateLookup.Find(source, day.AddDays(1), day, true, false, zone, new TestProgress(), CancellationToken.None); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Reversed date ranges are rejected");
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            ClipDateLookup.Find(source, day, day, true, false, zone,
                new TestProgress { OnMessage = _ => cancellation.Cancel() }, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && File.ReadAllBytes(clip).SequenceEqual(bytes) && File.ReadAllBytes(copied).SequenceEqual(bytes),
            "Date lookup is cancellable and preserves clip bytes");
        ArchiveOrigins(parent, clip, created);
        Ui(parent, source);
    }

    private static void ArchiveOrigins(string parent, string clip, DateTimeOffset created)
    {
        string data = Directory.CreateDirectory(Path.Combine(parent, "date-archive-data")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(parent, "date-archive-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(parent, "date-archive-destination")).FullName;
        string original = Path.Combine(source, "recording.mp4"); File.Copy(clip, original);
        var settings = new ArchiveSettings { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        var result = ClipArchiver.Run(settings, data, VideoFiles.CreateExtensions(), new TestProgress(), CancellationToken.None);
        Check(result.Copied == 1 && result.Errors.Count == 0, "Archiving with origin capture still copies and verifies successfully");
        string indexPath = ArchiveIndex.IndexPath(data, destination);
        var index = ArchiveIndex.Load(indexPath, destination); var entry = index.Entries.Single(); var origin = entry.Sources.Single();
        string target = entry.Locations.Single();
        Check(entry.EmbeddedCreatedUtc == created && origin.OriginalName == "recording.mp4" && origin.Path == original
            && origin.SourceCreatedUtc == new DateTimeOffset(File.GetCreationTimeUtc(original))
            && origin.SourceModifiedUtc == new DateTimeOffset(File.GetLastWriteTimeUtc(original))
            && origin.FirstArchivedUtc is not null, "Persistent archive origins include embedded creation, source timestamps, original name/path/root and first verification time");
        Check(SHA256.HashData(File.ReadAllBytes(original)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))),
            "Origin capture preserves exact MP4 bytes and content identity");
        var again = ClipArchiver.Run(settings, data, VideoFiles.CreateExtensions(), new TestProgress(), CancellationToken.None);
        var after = ArchiveIndex.Load(indexPath, destination).Entries.Single();
        Check(again.Unchanged == 1 && after.Sources.Single() == origin && after.EmbeddedCreatedUtc == created,
            "Routine watch/rerun fast paths retain origin data and its first timestamp");
        string renamed = Path.Combine(destination, "shuffled.mp4"); File.Move(target, renamed);
        ClipArchiver.Run(settings, data, VideoFiles.CreateExtensions(), new TestProgress(), CancellationToken.None);
        after = ArchiveIndex.Load(indexPath, destination).Entries.Single();
        Check(after.Locations.Single() == renamed && after.Sources.Single() == origin && after.EmbeddedCreatedUtc == created,
            "Archive origin metadata follows content through renaming and reconciliation");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(indexPath))!;
        foreach (var item in legacy["Entries"]!.AsArray())
        {
            item!.AsObject().Remove("EmbeddedCreatedUtc");
            foreach (var legacySource in item["Sources"]!.AsArray())
                foreach (string field in new[] { "SourceCreatedUtc", "SourceModifiedUtc", "FirstArchivedUtc" })
                    legacySource!.AsObject().Remove(field);
        }
        File.WriteAllText(indexPath, legacy.ToJsonString());
        Check(ArchiveIndex.Load(indexPath, destination).Entries.Single().EmbeddedCreatedUtc is null,
            "Older archive catalogs without origin date fields remain readable");
        settings.VerifyExistingContent = true;
        var recheck = ClipArchiver.Run(settings, data, VideoFiles.CreateExtensions(), new TestProgress(), CancellationToken.None);
        after = ArchiveIndex.Load(indexPath, destination).Entries.Single();
        Check(recheck.Errors.Count == 0 && after.EmbeddedCreatedUtc == created && after.Sources.Single().SourceCreatedUtc is not null,
            "Full recheck backfills new origin fields for legacy catalog entries with surviving sources");
        settings.VerifyExistingContent = false;
        using var coordinator = new OperationCoordinator();
        using var worker = new ArchiveWorker(coordinator, VideoFiles.CreateExtensions(), data);
        settings.WatchContinuously = true;
        worker.Start(settings);
        var timeout = Stopwatch.StartNew();
        while (worker.Status.State != ArchiveRuntimeState.Watching)
        {
            if (timeout.ElapsedMilliseconds > 10000) throw new Exception("Archive watch startup timed out.");
            Thread.Sleep(10);
        }
        string arrival = Path.Combine(source, "watch-arrival.mp4");
        byte[] arrivalBytes = File.ReadAllBytes(clip); arrivalBytes[^1] ^= 1;
        File.WriteAllBytes(arrival, arrivalBytes);
        timeout.Restart();
        while (worker.Status.Copied < 1)
        {
            if (timeout.ElapsedMilliseconds > 10000) throw new Exception("Archive origin watch arrival timed out.");
            Thread.Sleep(10);
        }
        worker.StopAsync().GetAwaiter().GetResult();
        var watched = ArchiveIndex.Load(indexPath, destination).Entries.Single(e => e.Sources.Any(s => s.Path == arrival));
        Check(watched.EmbeddedCreatedUtc == created && watched.Sources.Single().FirstArchivedUtc is not null,
            "Continuous watching automatically captures origins for a newly arriving clip");
    }

    private static void Ui(string parent, string source)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string data = Directory.CreateDirectory(Path.Combine(parent, "date-ui-data")).FullName;
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                shell.Navigate("Find & clean");
                Descendants(shell).OfType<Button>().Single(b => b.Text == "Find by date").PerformClick();
                Application.DoEvents();
                var workspace = Descendants(shell).OfType<ClipDateWorkspace>().Single();
                Check(workspace.Visible, "Find by date is reachable in the native Find & clean tool group");
                foreach (var picker in Descendants(workspace).OfType<DateTimePicker>()) picker.Value = new DateTime(2025, 3, 5);
                Descendants(workspace).OfType<CheckBox>().Single(c => c.Text.StartsWith("Also show")).Checked = true;
                var task = workspace.FindAsync(); var timeout = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (timeout.ElapsedMilliseconds > 15000) throw new Exception("Date UI lookup timed out.");
                    Application.DoEvents(); Thread.Sleep(2);
                }
                task.GetAwaiter().GetResult(); Application.DoEvents();
                var list = Descendants(workspace).OfType<FileContextList>().Single();
                Check(list.Visible && list.Rows.Any(r => r.Created is null) && list.Table.Columns["Created"]!.Visible
                    && !list.Table.Columns["Modified"]!.Visible
                    && !Descendants(workspace).OfType<Button>().Any(b => b.Visible && b.Text.Contains("Recycle")),
                    "Native results show embedded dates/unknown dates and omit modified time and recycling controls");
                foreach (var size in new[] { new Size(1180, 760), new Size(640, 480) })
                {
                    shell.ClientSize = size; Application.DoEvents();
                    Check(list.Visible && list.Width > 300 && list.Table.IsHandleCreated,
                        $"Date results remain reachable at {size.Width}x{size.Height}");
                }
                if (Environment.GetEnvironmentVariable("CLIP_DATE_SCREENSHOT") is { Length: > 0 } screenshot)
                {
                    shell.ClientSize = new Size(1180, 760); Application.DoEvents();
                    using var bitmap = new Bitmap(shell.Width, shell.Height);
                    shell.DrawToBitmap(bitmap, new Rectangle(Point.Empty, shell.Size)); bitmap.Save(screenshot);
                }
                shell.Close();
                while (!shell.IsDisposed) { Application.DoEvents(); Thread.Sleep(2); }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Date lookup UI check failed.", error);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    private static ulong Seconds(DateTimeOffset date) => (ulong)(date - new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
    private static void WriteMetadata(string path, byte version, ulong seconds, bool extended = false, bool toEnd = false)
    {
        int payloadSize = version == 0 ? 100 : 112;
        byte[] movie = new byte[(extended ? 16 : 8) + 8 + payloadSize];
        BinaryPrimitives.WriteUInt32BigEndian(movie, extended ? 1u : toEnd ? 0u : (uint)movie.Length);
        "moov"u8.CopyTo(movie.AsSpan(4));
        int offset = extended ? 16 : 8;
        if (extended) BinaryPrimitives.WriteUInt64BigEndian(movie.AsSpan(8), (ulong)movie.Length);
        BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(offset), (uint)(8 + payloadSize));
        "mvhd"u8.CopyTo(movie.AsSpan(offset + 4)); movie[offset + 8] = version;
        if (version == 0) BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(offset + 12), (uint)seconds);
        else BinaryPrimitives.WriteUInt64BigEndian(movie.AsSpan(offset + 12), seconds);
        File.WriteAllBytes(path, movie);
    }

    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception("FAIL: " + description); Console.WriteLine("PASS: " + description); }
}
