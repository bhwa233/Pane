using System.Diagnostics;

namespace Pane.Services.Traffic;

internal readonly record struct InterfaceTrafficPoint(long Timestamp, InterfaceRate Rate);

internal sealed class InterfaceTrafficTrend
{
    public const int WindowSeconds = 60;
    const int MaximumSamples = WindowSeconds + 1;
    readonly Queue<InterfaceTrafficPoint> points = new(MaximumSamples);
    long lastTimestamp;

    public IReadOnlyCollection<InterfaceTrafficPoint> Points => points;
    public long LatestTimestamp => lastTimestamp;

    public void Add(long timestamp, InterfaceRate? rate)
    {
        if (rate is not { } valid || !double.IsFinite(valid.Download) || !double.IsFinite(valid.Upload) ||
            valid.Download < 0 || valid.Upload < 0)
        {
            Clear();
            return;
        }

        if (points.Count > 0 && (timestamp <= lastTimestamp || Stopwatch.GetElapsedTime(lastTimestamp, timestamp).TotalSeconds > 5))
            Clear();
        while (points.TryPeek(out var first) && (points.Count >= MaximumSamples ||
            Stopwatch.GetElapsedTime(first.Timestamp, timestamp).TotalSeconds > WindowSeconds))
            points.Dequeue();

        points.Enqueue(new(timestamp, valid));
        lastTimestamp = timestamp;
    }

    public void Clear()
    {
        points.Clear();
        lastTimestamp = 0;
    }
}
