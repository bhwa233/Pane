using System.Runtime.InteropServices;

namespace Pane.Services.Traffic;

internal sealed class ResourceUsageSampler : IDisposable
{
    internal readonly record struct Reading(double? Cpu, double? Gpu, double? Memory, double? Disk);

    const uint MoreData = 0x800007D2, DoubleFormat = 0x200, NoCap = 0x8000;
    readonly nint query, cpu, gpu, disk;

    internal ResourceUsageSampler()
    {
        if (PdhOpenQueryW(null, 0, out query) != 0) return;
        cpu = Add(@"\Processor Information(_Total)\% Processor Time");
        gpu = Add(@"\GPU Engine(*)\Utilization Percentage");
        disk = Add(@"\PhysicalDisk(*)\% Idle Time");
    }

    nint Add(string path) => PdhAddEnglishCounterW(query, path, 0, out var counter) == 0 ? counter : 0;

    internal Reading Sample()
    {
        var memoryStatus = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        double? memory = GlobalMemoryStatusEx(ref memoryStatus) ? memoryStatus.MemoryLoad : null;
        if (query == 0 || PdhCollectQueryData(query) != 0) return new(null, null, memory, null);
        double? processor = cpu != 0 && PdhGetFormattedCounterValue(cpu, DoubleFormat, 0, out var value) == 0 && value.Valid
            ? value.Number : null;
        var engines = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, utilization) in ReadArray(gpu))
        {
            // GPU Engine instances are per PID. Add workloads on the same adapter/engine before selecting the busiest engine.
            int adapter = name.IndexOf("_luid_", StringComparison.Ordinal);
            if (adapter < 0) continue;
            var engine = name[adapter..];
            engines[engine] = engines.GetValueOrDefault(engine) + utilization;
        }
        double? graphics = engines.Count == 0 ? null : engines.Values.Max();
        double? storage = null;
        foreach (var (name, idle) in ReadArray(disk))
        {
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            storage = Math.Max(storage ?? 0, 100 - idle);
        }
        return new(processor, graphics, memory, storage);
    }

    static List<(string Name, double Value)> ReadArray(nint counter)
    {
        List<(string, double)> values = [];
        if (counter == 0) return values;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, DoubleFormat | NoCap, ref size, out _, 0) != MoreData)
            return values;
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, DoubleFormat | NoCap, ref size, out var count, buffer) != 0)
                return values;
            int stride = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                if (item.Value.Valid && Marshal.PtrToStringUni(item.Name) is { } name)
                    values.Add((name, item.Value.Number));
            }
            return values;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (query != 0) PdhCloseQuery(query);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CounterValue
    {
        public uint Status;
        public double Number;
        public readonly bool Valid => Status is 0 or 1 && double.IsFinite(Number);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhOpenQueryW(string? source, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out CounterValue value);
    [DllImport("pdh.dll", ExactSpelling = true)]
    static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, nint buffer);
    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(nint query);
}
