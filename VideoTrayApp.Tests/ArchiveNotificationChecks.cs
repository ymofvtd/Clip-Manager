using System.Collections.Concurrent;
using System.Reflection;
using VideoTrayApp;

internal static class ArchiveNotificationChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        Sessions();
        string root = Directory.CreateDirectory(Path.Combine(parent, "archive-notifications")).FullName;
        string data = Path.Combine(root, "settings");
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        string existing = Path.Combine(source, "existing.mp4");
        write(existing, 2, 7);
        File.Copy(existing, Path.Combine(destination, "existing.mp4"));
        using (var coordinator = new OperationCoordinator())
        using (var manager = new ArchiveManager(coordinator, VideoFiles.CreateExtensions(), data))
        {
            var notifications = new ConcurrentQueue<Guid>();
            manager.ArchivingStarted += route => notifications.Enqueue(route.Configuration.Id);
            var route = manager.Routes[0];
            var settings = new ArchiveSettings { Sources = [source], Destination = destination,
                WatchContinuously = true, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
            manager.Start(route, settings, "Clips");
            ArchiveChecks.Wait(() => route.Worker.ReadyForMonthlyBatch, 10000);
            Check(route.Worker.Status.AlreadyArchived == 1 && notifications.IsEmpty,
                "Startup scans and already archived clips do not announce archiving");
            for (int i = 0; i < 5; i++) write(Path.Combine(source, $"new-{i}.mp4"), 3, (byte)(20 + i));
            ArchiveChecks.Wait(() => route.Worker.Status.Copied == 5 && route.Worker.ReadyForMonthlyBatch, 10000);
            Check(notifications.SequenceEqual([route.Configuration.Id]), "A watched batch announces Archiving started once for all its clips");
            manager.RequestReconciliation();
            ArchiveChecks.Wait(() => route.Worker.ReadyForMonthlyBatch, 10000);
            Check(notifications.Count == 1, "Quiet metadata reconciliation does not send another notification");

            var pending = new ConcurrentQueue<bool>();
            route.Worker.PendingChanged += pending.Enqueue;
            route.Worker.Pause();
            write(Path.Combine(source, "paused.mp4"), 3, 30);
            ArchiveChecks.Wait(() => pending.Contains(true), 5000);
            Check(route.Worker.Status.Copied == 5, "Paused watcher arrivals report pending work immediately without starting a copy");
            route.Worker.Resume();
            ArchiveChecks.Wait(() => route.Worker.Status.Copied == 6 && route.Worker.ReadyForMonthlyBatch, 10000);
            Check(pending.Last() == false && notifications.Count == 1, "Draining paused work marks the route quiet without repeating its notification");

            var second = manager.Add();
            var other = settings.Clone();
            other.Sources = [Directory.CreateDirectory(Path.Combine(root, "other-source")).FullName];
            other.Destination = Directory.CreateDirectory(Path.Combine(root, "other-destination")).FullName;
            write(Path.Combine(other.Sources[0], "other.mp4"), 4, 40);
            manager.Start(second, other, "Exports");
            ArchiveChecks.Wait(() => second.Worker.Status.Copied == 1, 10000);
            Check(notifications.Count == 1, "Overlapping activity on multiple watched routes shares one notification");
            manager.StopAllAsync().GetAwaiter().GetResult();
            other.WatchContinuously = false;
            write(Path.Combine(other.Sources[0], "manual.mp4"), 5, 41);
            manager.Start(second, other, "Exports");
            ArchiveChecks.Wait(() => !second.Worker.Running, 10000);
            Check(second.Worker.Status.Copied == 1 && notifications.Count == 1, "Run-once archiving does not announce watched activity");
        }
        Ui(root, write);
    }

    private static void Sessions()
    {
        var clock = new FakeArchiveClock();
        var session = new ArchiveNotificationSession(clock);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Check(session.TryStart(first), "The first actual watched copy announces archiving");
        Check(Enumerable.Range(0, 200).All(_ => !session.TryStart(first)) && !session.TryStart(second),
            "Hundreds of clips and another route stay silent within one burst");
        session.Observe(first, pending: false);
        clock.Advance(TimeSpan.FromHours(1));
        Check(!session.TryStart(second), "A pending route prevents repeats during long copies, Slow waits, pauses or retries");
        session.Observe(second, pending: false);
        clock.Advance(ArchiveNotificationSession.QuietPeriod - TimeSpan.FromSeconds(1));
        session.Observe(first, pending: false); // A quiet scan must not move the idle deadline.
        Check(!session.TryStart(first), "A short gap between batches does not send another notification");
        session.Observe(first, pending: false);
        clock.Advance(ArchiveNotificationSession.QuietPeriod);
        session.Observe(second, pending: false);
        Check(session.TryStart(second) && !session.TryStart(first), "Five fully quiet minutes rearm exactly one notification across all routes");
        session.Observe(first, pending: false);
        session.Observe(second, pending: false);
        clock.Advance(TimeSpan.FromMinutes(4));
        session.Observe(first, pending: true);
        clock.Advance(TimeSpan.FromMinutes(10));
        Check(!session.TryStart(first), "Settling or scheduled work arriving before the quiet deadline keeps the current burst");

        var simultaneous = new ArchiveNotificationSession(clock);
        int count = 0;
        Parallel.For(0, 200, _ => { if (simultaneous.TryStart(Guid.NewGuid())) Interlocked.Increment(ref count); });
        Check(count == 1, "Simultaneous routes cannot race into duplicate notifications");
    }

    private static void Ui(string root, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                foreach (bool enabled in new[] { false, true })
                {
                    string data = Directory.CreateDirectory(Path.Combine(root, "ui-" + enabled)).FullName;
                    File.WriteAllText(Path.Combine(data, "ui_settings.json"), "{\"SchemaVersion\":1,\"Notifications\":false}");
                    var preferences = UiPreferences.Load(data, out _);
                    Check(preferences.ArchiveStartNotifications, "Existing settings enable watched archive notifications by default");
                    preferences.ArchiveStartNotifications = enabled;
                    preferences.LaunchIntoTray = preferences.SetupDismissed = true;
                    preferences.Save(data);
                    Check(UiPreferences.Load(data, out _).ArchiveStartNotifications == enabled,
                        "Archive notification preference persists independently from completion notifications");
                    string source = Directory.CreateDirectory(Path.Combine(data, "source")).FullName;
                    string destination = Directory.CreateDirectory(Path.Combine(data, "destination")).FullName;
                    write(Path.Combine(source, "new.mp4"), 6, 50);
                    WatchFolderSettings.Save(data, []);
                    new ArchiveSettings { Sources = [source], Destination = destination, WatchContinuously = true,
                        Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 }.Save(data);
                    using var shell = new MainShell(data);
                    try
                    {
                        shell.Show();
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        var tray = (NotifyIcon)typeof(MainShell).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                        while (shell.Archive.Status.Copied < 1 || enabled && tray.BalloonTipTitle.Length == 0)
                        {
                            if (timer.ElapsedMilliseconds > 10000) throw new Exception("Archive notification UI timed out.");
                            Application.DoEvents(); Thread.Sleep(5);
                        }
                        Application.DoEvents();
                        Check(!shell.Visible && (enabled ? tray.BalloonTipTitle == "Archiving started" : tray.BalloonTipTitle.Length == 0),
                            "Watched archive startup marshals the notification to the UI while hidden and honors its preference: " + enabled);
                    }
                    finally
                    {
                        var exit = (Task)typeof(MainShell).GetMethod("ExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [false])!;
                        while (!exit.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
                        exit.GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Archive notification UI checks failed.", error);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
