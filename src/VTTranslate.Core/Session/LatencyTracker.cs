namespace VTTranslate.Core.Session;

/// <summary>
/// Tracks end-to-end latency samples (time from a source segment finalizing to its
/// translated audio being ready) and reports P50/P90/P99 so the UI shows measured
/// numbers, never an unverified claim.
/// </summary>
public sealed class LatencyTracker
{
    private readonly object _lock = new();
    private readonly List<double> _samplesMs = new();
    private const int MaxSamples = 500;

    public void Record(TimeSpan latency)
    {
        lock (_lock)
        {
            _samplesMs.Add(latency.TotalMilliseconds);
            if (_samplesMs.Count > MaxSamples)
                _samplesMs.RemoveAt(0);
        }
    }

    public LatencySnapshot Snapshot()
    {
        lock (_lock)
        {
            if (_samplesMs.Count == 0)
                return new LatencySnapshot(0, 0, 0, 0);

            var sorted = _samplesMs.OrderBy(x => x).ToArray();
            return new LatencySnapshot(
                sorted[^1],
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.90),
                Percentile(sorted, 0.99));
        }
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }
}

public readonly record struct LatencySnapshot(double LastMs, double P50Ms, double P90Ms, double P99Ms);
