using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

public class LatencyTrackerTests
{
    [Fact]
    public void Snapshot_IsZero_WhenNoSamples()
    {
        var tracker = new LatencyTracker();
        var snap = tracker.Snapshot();
        Assert.Equal(0, snap.LastMs);
        Assert.Equal(0, snap.P50Ms);
    }

    [Fact]
    public void Snapshot_ComputesPercentiles_ForKnownDistribution()
    {
        var tracker = new LatencyTracker();
        for (int i = 1; i <= 100; i++)
            tracker.Record(TimeSpan.FromMilliseconds(i));

        var snap = tracker.Snapshot();

        Assert.Equal(100, snap.LastMs);
        Assert.InRange(snap.P50Ms, 45, 55);
        Assert.InRange(snap.P90Ms, 85, 95);
        Assert.InRange(snap.P99Ms, 95, 100);
    }

    [Fact]
    public void Record_CapsSampleHistory_SoMemoryDoesNotGrowUnbounded()
    {
        var tracker = new LatencyTracker();
        for (int i = 0; i < 10_000; i++)
            tracker.Record(TimeSpan.FromMilliseconds(1));

        // Should not throw and should still produce a valid snapshot after heavy use.
        var snap = tracker.Snapshot();
        Assert.Equal(1, snap.P50Ms);
    }
}
