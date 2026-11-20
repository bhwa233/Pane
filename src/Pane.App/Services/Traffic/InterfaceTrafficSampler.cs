using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Pane.Services.Traffic;

internal readonly record struct InterfaceRate(double Download, double Upload);
internal enum InterfaceTrafficState { Sampling, Live, Unavailable, Paused }
internal readonly record struct InterfaceTrafficReading(InterfaceRate? Rate, InterfaceTrafficState State);
internal sealed record InterfaceTrafficAdapter(uint Index, string? Name, IReadOnlyList<string> Addresses);

[StructLayout(LayoutKind.Sequential)]
internal readonly struct InterfaceTrafficCounters(Guid id, ulong download, ulong upload, uint index)
{
    public readonly Guid Id = id;
    public readonly ulong Download = download;
    public readonly ulong Upload = upload;
    public readonly uint Index = index;
}

internal sealed class InterfaceTrafficSampler
{
    const int MaxInterfaces = 4096;
    const int InitialCapacity = 8;
    InterfaceTrafficCounters[] buffer = new InterfaceTrafficCounters[InitialCapacity];
    Dictionary<(Guid Id, uint Index), InterfaceTrafficCounters> previous = new(), current = new();
    long previousTime;

    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern int PanePhysicalInterfaceCounters([Out] InterfaceTrafficCounters[] output, int capacity);

    public InterfaceTrafficReading Sample()
    {
        int count = ReadCounters(ref buffer);
        return ApplySnapshot(buffer.AsSpan(0, count), Stopwatch.GetTimestamp());
    }

    static int ReadCounters(ref InterfaceTrafficCounters[] buffer)
    {
        var count = PanePhysicalInterfaceCounters(buffer, buffer.Length);
        if (count > buffer.Length && count <= MaxInterfaces)
        {
            buffer = new InterfaceTrafficCounters[count];
            count = PanePhysicalInterfaceCounters(buffer, buffer.Length);
        }
        return count > 0 && count <= buffer.Length ? count : 0;
    }

    internal static InterfaceTrafficAdapter[] ReadAdapters()
    {
        var counters = new InterfaceTrafficCounters[InitialCapacity];
        int count = ReadCounters(ref counters);
        if (count == 0) return [];

        var interfaces = new Dictionary<Guid, NetworkInterface>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                if (Guid.TryParse(adapter.Id, out var id) && adapter.OperationalStatus == OperationalStatus.Up)
                    interfaces.TryAdd(id, adapter);
        }
        catch (NetworkInformationException) { }

        var result = new InterfaceTrafficAdapter[count];
        for (int index = 0; index < count; index++)
        {
            var item = counters[index];
            interfaces.TryGetValue(item.Id, out var adapter);
            IReadOnlyList<string> addresses = [];
            if (adapter != null)
            {
                try
                {
                    addresses = adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString()).Distinct().ToArray();
                }
                catch (NetworkInformationException) { }
            }
            result[index] = new(item.Index, adapter?.Name, addresses);
        }
        Array.Sort(result, (left, right) => left.Index.CompareTo(right.Index));
        return result;
    }

    internal InterfaceTrafficReading ApplySnapshot(ReadOnlySpan<InterfaceTrafficCounters> counters, long now)
    {
        current.Clear();
        foreach (var item in counters)
            current[(item.Id, item.Index)] = item;
        var seconds = Stopwatch.GetElapsedTime(previousTime, now).TotalSeconds;
        var hasBaseline = current.Count > 0 && current.Count == previous.Count && previousTime != 0 && seconds is > 0 and <= 5;
        double download = 0, upload = 0;
        if (hasBaseline)
        {
            foreach (var (identity, item) in current)
            {
                if (!previous.TryGetValue(identity, out var baseline) || item.Download < baseline.Download || item.Upload < baseline.Upload)
                {
                    hasBaseline = false;
                    break;
                }
                download += (item.Download - baseline.Download) / seconds;
                upload += (item.Upload - baseline.Upload) / seconds;
            }
        }

        var available = current.Count > 0;
        (previous, current) = (current, previous);
        previousTime = now;
        return hasBaseline ? new(new InterfaceRate(download, upload), InterfaceTrafficState.Live) :
            new(null, available ? InterfaceTrafficState.Sampling : InterfaceTrafficState.Unavailable);
    }
}
