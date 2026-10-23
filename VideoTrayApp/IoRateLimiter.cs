namespace VideoTrayApp;

internal interface IArchiveClock
{
    DateTimeOffset UtcNow { get; }
    void Delay(TimeSpan delay, CancellationToken ct);
}

internal sealed class ArchiveClock : IArchiveClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public void Delay(TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero && ct.WaitHandle.WaitOne(delay)) ct.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
    }
}

// One shared budget counts source reads, destination writes and verification reads.
internal sealed class IoRateLimiter(long? bytesPerSecond, IArchiveClock clock)
{
    private DateTimeOffset next = clock.UtcNow;
    internal long BytesCharged { get; private set; }
    internal void Charge(int bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BytesCharged += bytes;
        if (bytesPerSecond is not > 0) return;
        DateTimeOffset now = clock.UtcNow;
        if (next < now) next = now;
        next += TimeSpan.FromSeconds((double)bytes / bytesPerSecond.Value);
        clock.Delay(next - now, ct);
    }
}

internal sealed class ArchiveSchedule(ArchiveSettings settings, IArchiveClock clock)
{
    private DateTimeOffset? lastStart;
    internal TimeSpan RemainingDelay => settings.Speed == ArchiveSpeed.Slow && lastStart is { } previous
        ? previous.AddSeconds(settings.IntervalSeconds) - clock.UtcNow : TimeSpan.Zero;
    internal void BeforeClip(CancellationToken ct)
    {
        if (settings.Speed == ArchiveSpeed.Slow && lastStart is { } previous)
            clock.Delay(previous.AddSeconds(settings.IntervalSeconds) - clock.UtcNow, ct);
        lastStart = clock.UtcNow;
    }
    internal void AfterPause() => lastStart = clock.UtcNow;
}
