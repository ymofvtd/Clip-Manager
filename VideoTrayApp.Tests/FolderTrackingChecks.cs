using System.Diagnostics;
using VideoTrayApp;

internal static class FolderTrackingChecks
{
    public static void Run(string testRoot, Action<string, uint, byte> writeAvi)
    {
        string root = Directory.CreateDirectory(Path.Combine(testRoot, "tracking")).FullName;
        string first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(first, "child")).FullName;
        string deep = Directory.CreateDirectory(Path.Combine(child, "deep")).FullName;
        string unavailable = Path.Combine(root, "unavailable");
        string appData = Directory.CreateDirectory(Path.Combine(root, "settings")).FullName;
        string legacy = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
        File.WriteAllText(Path.Combine(legacy, "config.txt"), first);
        File.WriteAllText(Path.Combine(appData, "working_folder.txt"), second);

        Check(WatchFolderSettings.Load(appData, legacy).SequenceEqual([first]), "Legacy single watch folder migrates automatically");
        WatchFolderSettings.Save(appData, [first, first.ToUpperInvariant() + Path.DirectorySeparatorChar,
            second, unavailable, child, " "]);
        var saved = WatchFolderSettings.Load(appData, legacy);
        Check(saved.SequenceEqual([first, second, unavailable, child]), "Multiple watch folders persist, normalize duplicates, and retain unavailable roots");
        Check(File.ReadAllText(Path.Combine(appData, "working_folder.txt")) == second,
            "Watch folder settings preserve the separate working folder");
        WatchFolderSettings.Save(appData, []);
        Check(WatchFolderSettings.Load(appData, legacy).Count == 0,
            "Saving an empty list disables tracking without restoring legacy settings");
        RunWindowChecks([first, second, unavailable]);

        string firstClip = Path.Combine(first, "one.AVI");
        string secondClip = Path.Combine(second, "two.avi");
        writeAvi(firstClip, 2, 1);
        writeAvi(secondClip, 3, 2);
        writeAvi(Path.Combine(child, "child.avi"), 4, 3);
        writeAvi(Path.Combine(deep, "deep.avi"), 5, 4);
        File.WriteAllText(Path.Combine(first, "notes.txt"), "Keep my notes");
        File.WriteAllText(Path.Combine(first, "99M9S.txt"), "stale marker");

        using (var tracker = new FolderDurationTracker([".avi"]))
        {
            tracker.SetFolders([unavailable, first, second, first]);
            WaitFor(() => HasMarker(first, "0M2S.txt") && HasMarker(second, "0M3S.txt")
                && HasMarker(child, "0M4S.txt"), "Startup refresh updates independent roots and immediate children");
            Check(!Directory.GetFiles(deep, "*.txt").Any(), "Existing immediate-child tracking scope is preserved");
            Check(File.ReadAllText(Path.Combine(first, "notes.txt")) == "Keep my notes"
                && !File.Exists(Path.Combine(first, "99M9S.txt")), "Duration refresh preserves notes and replaces only old markers");

            writeAvi(firstClip, 6, 5);
            WaitFor(() => HasMarker(first, "0M6S.txt"), "Changing a clip duration in place refreshes its folder");
            string added = Path.Combine(second, "added.avi");
            writeAvi(added, 7, 6);
            WaitFor(() => HasMarker(second, "0M10S.txt"), "Creating clips in another watched root refreshes its total");
            string renamed = Path.Combine(second, "added.txt");
            File.Move(added, renamed);
            WaitFor(() => HasMarker(second, "0M3S.txt"), "Renaming a video to a non-video extension removes its duration");
            File.Move(secondClip, Path.Combine(first, "moved.avi"));
            WaitFor(() => HasMarker(first, "0M9S.txt") && !Directory.GetFiles(second, "*M*S.txt").Any(),
                "Moving clips between roots updates both totals and clears empty-folder markers");

            tracker.Pause();
            writeAvi(firstClip, 10, 7);
            writeAvi(secondClip, 8, 8);
            Thread.Sleep(900);
            Check(HasMarker(first, "0M9S.txt") && !File.Exists(Path.Combine(second, "0M8S.txt")),
                "Pausing prevents marker changes in every watched folder");
            tracker.Resume();
            WaitFor(() => HasMarker(first, "0M13S.txt") && HasMarker(second, "0M8S.txt"),
                "Resuming refreshes every watched root after operations");

            tracker.SetFolders([first, second, child]);
            WaitFor(() => HasMarker(deep, "0M5S.txt"), "An overlapping child root also tracks its own immediate children");

            string outside = Directory.CreateDirectory(Path.Combine(root, "incoming")).FullName;
            writeAvi(Path.Combine(outside, "incoming.avi"), 11, 9);
            string incoming = Path.Combine(second, "incoming");
            Directory.Move(outside, incoming);
            WaitFor(() => HasMarker(incoming, "0M11S.txt"), "Moving a populated subfolder into a watched root updates its duration");

            tracker.SetFolders([first]);
            writeAvi(secondClip, 12, 10);
            writeAvi(firstClip, 14, 11);
            WaitFor(() => HasMarker(first, "0M17S.txt"), "Remaining watched folders continue updating after removal");
            Check(HasMarker(second, "0M8S.txt"), "Removed roots stop updating their markers");
            tracker.SetFolders([]);
            writeAvi(firstClip, 18, 12);
            Thread.Sleep(900);
            Check(HasMarker(first, "0M17S.txt"), "Removing all roots stops duration tracking");
        }

        WatchFolderSettings.Save(appData, [first, second, unavailable]);
        using var restarted = new FolderDurationTracker([".avi"]);
        restarted.SetFolders(WatchFolderSettings.Load(appData, legacy));
        WaitFor(() => HasMarker(first, "0M21S.txt") && HasMarker(second, "0M12S.txt"),
            "Restart restores multiple saved folders and refreshes their durations");
        Directory.CreateDirectory(unavailable);
        writeAvi(Path.Combine(unavailable, "reconnected.avi"), 20, 13);
        restarted.Resume();
        WaitFor(() => HasMarker(unavailable, "0M20S.txt"), "Unavailable saved folders are retried on resume");
    }

    private static bool HasMarker(string folder, string marker) => File.Exists(Path.Combine(folder, marker))
        && Directory.GetFiles(folder, "*.txt").Count(path =>
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^\d+M\d+S\.txt$")) == 1;

    private static void RunWindowChecks(string[] folders)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var settings = new SettingsForm(folders.Concat([folders[0]]));
                settings.CreateControl();
                var list = settings.Controls.OfType<ListBox>().Single();
                var remove = settings.Controls.OfType<Button>().Single(button => button.Text == "Remove");
                Check(list.Items.Cast<string>().SequenceEqual(folders) && !remove.Enabled,
                    "Watch folder dialog restores the saved list, including unavailable folders, without duplicates");
                list.SelectedIndex = 1;
                Click(remove);
                Click(settings.Controls.OfType<Button>().Single(button => button.Text == "Save"));
                Check(settings.DialogResult == DialogResult.OK && settings.SelectedFolderPaths.SequenceEqual([folders[0], folders[2]]),
                    "Remove and Save return the edited folder list");

                using var empty = new SettingsForm([folders[0]]);
                var emptyList = empty.Controls.OfType<ListBox>().Single();
                emptyList.SelectedIndex = 0;
                Click(empty.Controls.OfType<Button>().Single(button => button.Text == "Remove"));
                Click(empty.Controls.OfType<Button>().Single(button => button.Text == "Save"));
                Check(empty.DialogResult == DialogResult.OK && empty.SelectedFolderPaths.Count == 0,
                    "Dialog accepts removing all folders to disable tracking");

                using var cancelled = new SettingsForm(folders);
                cancelled.Controls.OfType<ListBox>().Single().SelectedIndex = 0;
                Click(cancelled.Controls.OfType<Button>().Single(button => button.Text == "Remove"));
                Check(cancelled.SelectedFolderPaths.SequenceEqual(folders), "Unsaved dialog edits leave the original selection intact");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Exception("Watch folder window checks failed", error);
    }

    private static void Click(Button button) => typeof(Control).GetMethod("OnClick",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);

    private static void WaitFor(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            try { if (condition()) { Check(true, description); return; } }
            catch (IOException) { }
            Thread.Sleep(50);
        }
        Check(false, description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
