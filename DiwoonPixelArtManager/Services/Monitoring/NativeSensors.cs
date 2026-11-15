using System.Runtime.InteropServices;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Readings Windows gives without administrator rights: CPU / GPU load and ACPI temperature from performance counters,
/// GPU temperature from the graphics driver (the value Task Manager shows), memory load from the kernel.
/// </summary>
internal sealed partial class NativeSensors : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const int AdapterPerfDataQuery = 62; // KMTQAITYPE_ADAPTERPERFDATA

    private readonly IntPtr _query;
    private readonly IntPtr _cpuCounter, _gpuCounter, _thermalCounter;
    private readonly uint[] _adapters;

    public NativeSensors()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter);
            PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter);
            PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out _thermalCounter);
            PdhCollectQueryData(_query);
        }
        _adapters = OpenAdapters();
    }

    /// <summary>Reads every sensor once. Load counters need two reads, so the first call returns no load.</summary>
    public (double? CpuLoad, double? GpuLoad, double? AcpiTemperature, double? GpuTemperature, double RamLoad) Read()
    {
        double? cpu = null, gpu = null, acpi = null;
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            cpu = ReadSingle(_cpuCounter) is { } c ? Math.Min(c, 100) : null;
            var gpuValues = ReadArray(_gpuCounter);
            gpu = gpuValues.Count > 0 ? Math.Min(gpuValues.Sum(), 100) : null;
            // Thermal zones report Kelvin; desktops often expose only a fixed placeholder zone.
            var zones = ReadArray(_thermalCounter).Where(k => k > 273.15).ToList();
            acpi = zones.Count > 0 ? zones.Max() - 273.15 : null;
        }
        return (cpu, gpu, acpi, ReadGpuTemperature(), ReadMemoryLoad());
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
            PdhCloseQuery(_query);
        foreach (var adapter in _adapters)
        {
            var close = new CloseAdapter { Adapter = adapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    private static double? ReadSingle(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var value) == 0 && value.Status == 0
            ? value.Value
            : null;
    }

    private static List<double> ReadArray(IntPtr counter)
    {
        var result = new List<double>();
        if (counter == IntPtr.Zero) return result;

        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0)
            return result;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out uint count, buffer) != 0)
                return result;
            int itemSize = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Value.Status == 0)
                    result.Add(item.Value.Value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static uint[] OpenAdapters()
    {
        var enumeration = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref enumeration) != 0 || enumeration.Count == 0) return [];

        var infos = new AdapterInfo[enumeration.Count];
        var handle = GCHandle.Alloc(infos, GCHandleType.Pinned);
        try
        {
            enumeration.Adapters = handle.AddrOfPinnedObject();
            if (D3DKMTEnumAdapters2(ref enumeration) != 0) return [];
            return infos.Take((int)enumeration.Count).Select(a => a.Adapter).ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Hottest GPU in °C, from WDDM 2.5+ drivers. Integrated GPUs usually report nothing.</summary>
    private double? ReadGpuTemperature()
    {
        double? hottest = null;
        foreach (var adapter in _adapters)
        {
            var perf = new AdapterPerfData();
            var handle = GCHandle.Alloc(perf, GCHandleType.Pinned);
            try
            {
                var query = new QueryAdapterInfo
                {
                    Adapter = adapter,
                    Type = AdapterPerfDataQuery,
                    Data = handle.AddrOfPinnedObject(),
                    DataSize = (uint)Marshal.SizeOf<AdapterPerfData>()
                };
                if (D3DKMTQueryAdapterInfo(ref query) != 0) continue;
                perf = Marshal.PtrToStructure<AdapterPerfData>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            // Deci-degrees Celsius.
            if (perf.Temperature > 0 && perf.Temperature < 1500)
                hottest = Math.Max(hottest ?? 0, perf.Temperature / 10.0);
        }
        return hottest;
    }

    private static double ReadMemoryLoad()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint Count;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Adapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint DataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
