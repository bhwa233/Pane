using System.Diagnostics;
using Pane.Services.Traffic;

static class InterfaceTrafficTrendTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException(scenario);
            count++;
        }

        var trend = new InterfaceTrafficTrend();
        long second = Stopwatch.Frequency;
        var rate = new InterfaceRate(4096, 123);
        trend.Add(second, null);
        Check(trend.Points.Count == 0, "warming must not insert a zero sample");
        for (var index = 2; index <= 64; index += 2)
            trend.Add(index * second, rate);
        Check(trend.Points.First().Timestamp == 4 * second && trend.LatestTimestamp == 64 * second,
            "the retained window is based on elapsed time, not number of ticks");

        trend.Add(65 * second, null);
        Check(trend.Points.Count == 0, "a missing measurement must clear the trend without zero fill");
        trend.Add(66 * second, new InterfaceRate(0, 0));
        Check(trend.Points.Single().Rate.Download == 0, "a real measured zero belongs in the trend");
        trend.Add(72 * second, rate);
        Check(trend.Points.Single().Timestamp == 72 * second, "do not bridge a sleep or missed interval");
        trend.Clear();
        for (var index = 1; index <= 100; index++)
            trend.Add(index * (second / 2), rate);
        Check(trend.Points.Count == 61 && trend.Points.Last().Timestamp == 100 * (second / 2),
            "the sampling window remains bounded even with early ticks");

        var sampler = new InterfaceTrafficSampler();
        var ethernet = Guid.NewGuid();
        var wireless = Guid.NewGuid();
        var initial = sampler.ApplySnapshot([new(ethernet, 100, 200, 2), new(wireless, 400, 800, 5)], second);
        Check(initial is { State: InterfaceTrafficState.Sampling, Rate: null }, "the first counters establish a baseline without showing a zero");
        var reversed = sampler.ApplySnapshot([new(wireless, 430, 842, 5), new(ethernet, 130, 220, 2)], 3 * second);
        Check(reversed is { State: InterfaceTrafficState.Live, Rate: { Download: 30, Upload: 31 } },
            "adapter order does not change the aggregate delta over elapsed time");
        var reattached = sampler.ApplySnapshot([new(ethernet, 150, 230, 2), new(wireless, 465, 866, 7)], 4 * second);
        Check(reattached is { State: InterfaceTrafficState.Sampling, Rate: null }, "an interface index change invalidates the whole aggregate");
        var rollback = sampler.ApplySnapshot([new(ethernet, 155, 232, 2), new(wireless, 1, 5, 7)], 5 * second);
        Check(rollback is { State: InterfaceTrafficState.Sampling, Rate: null }, "a reset must not publish a partial total from the other interface");
        var gap = sampler.ApplySnapshot([new(ethernet, 155, 232, 2), new(wireless, 1, 5, 7)], 11 * second);
        Check(gap is { State: InterfaceTrafficState.Sampling, Rate: null }, "do not infer any total after a long sampling gap");
        var idle = sampler.ApplySnapshot([new(ethernet, 155, 232, 2), new(wireless, 1, 5, 7)], 12 * second);
        Check(idle is { State: InterfaceTrafficState.Live, Rate: { Download: 0, Upload: 0 } }, "a preceding complete sample allows a real zero rate");
        var offline = sampler.ApplySnapshot([], 13 * second);
        Check(offline is { State: InterfaceTrafficState.Unavailable, Rate: null }, "no physical interfaces is unavailable");
        var online = sampler.ApplySnapshot([new(ethernet, 190, 300, 2)], 14 * second);
        Check(online is { State: InterfaceTrafficState.Sampling, Rate: null }, "reconnecting cannot inherit a pre-disconnect baseline");
        return count;
    }
}
