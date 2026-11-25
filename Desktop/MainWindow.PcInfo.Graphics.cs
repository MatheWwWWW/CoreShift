using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private static readonly Guid DxgiFactory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;
    private const uint SoftwareAdapter = 2;

    private sealed record DisplayDriver(string Name, string? Version, string? DeviceId);
    private sealed record GraphicsInfo(string Name, ulong? VideoMemory, string? Driver);

    private static List<GraphicsInfo> ReadGraphicsInfo()
    {
        var drivers = ReadWmi("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController",
            item => new DisplayDriver(WmiText(item, "Name") ?? Unavailable, WmiText(item, "DriverVersion"),
                WmiText(item, "PNPDeviceID")));
        var adapters = ReadDxgiAdapters();
        var result = new List<GraphicsInfo>();
        var matchedDrivers = new HashSet<DisplayDriver>();
        foreach (var adapter in adapters)
        {
            string name = NonEmpty(adapter.Description?.Trim()) ?? Unavailable;
            string pciId = $@"PCI\VEN_{adapter.VendorId:X4}&DEV_{adapter.DeviceId:X4}&SUBSYS_{adapter.SubSystemId:X8}&REV_{adapter.Revision:X2}";
            var matches = drivers.Where(driver => driver.DeviceId?.StartsWith(pciId, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (matches.Length == 0)
                matches = drivers.Where(driver => string.Equals(driver.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();

            string? version = matches.Select(driver => driver.Version).Distinct().Count() == 1 ? matches[0].Version : null;
            matchedDrivers.UnionWith(matches);

            result.Add(new(name, (ulong)adapter.DedicatedVideoMemory, version));
        }

        result.AddRange(drivers.Where(driver => !matchedDrivers.Contains(driver))
            .Select(driver => new GraphicsInfo(driver.Name, null, driver.Version)));
        return result;
    }

    private static List<DxgiAdapterDescription> ReadDxgiAdapters()
    {
        var adapters = new List<DxgiAdapterDescription>();
        try
        {
            if (CreateDXGIFactory1(DxgiFactory1Id, out nint factory) < 0 || factory == 0)
                return adapters;

            try
            {
                var enumerate = GetComMethod<EnumDxgiAdapter>(factory, EnumAdapters1Slot);
                for (uint index = 0; enumerate(factory, index, out nint adapter) >= 0 && adapter != 0; index++)
                {
                    try
                    {
                        var describe = GetComMethod<GetDxgiAdapterDescription>(adapter, GetDesc1Slot);
                        if (describe(adapter, out var description) >= 0 && (description.Flags & SoftwareAdapter) == 0)
                            adapters.Add(description);
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex) when (IsSensorError(ex))
        {
            Debug.WriteLine(ex);
        }

        return adapters;
    }

    // Slots include IUnknown and all methods of the base DXGI interfaces.
    private static T GetComMethod<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static List<PcInfoRow> ReadMonitorInfo()
    {
        var rows = new List<PcInfoRow>();
        const uint activePaths = 2;
        const int insufficientBuffer = 122;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(activePaths, out uint pathCount, out uint modeCount) != 0)
                return rows;

            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            int status = QueryDisplayConfig(activePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == insufficientBuffer)
                continue;
            if (status != 0)
                return rows;

            for (int index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var source = path.Source.ModeIndex < modeCount && modes[path.Source.ModeIndex].Type == 1
                    ? modes[path.Source.ModeIndex].Data.Source : default;
                var target = path.Target.ModeIndex < modeCount && modes[path.Target.ModeIndex].Type == 2
                    ? modes[path.Target.ModeIndex].Data.Target : default;
                string? desktop = FormatResolution(source.Width, source.Height);
                string? signal = FormatResolution(target.Width, target.Height);
                double? hz = RefreshHz(path.Target.RefreshRate) ?? RefreshHz(target.VerticalRefresh);
                string? connector = path.Target.Technology switch
                {
                    0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 11 => "eDP", 15 => "Miracast", _ => null
                };
                string? name = ReadMonitorName(path.Target);
                rows.Add(new($"Монитор {index + 1}", JoinDetails(
                    name,
                    desktop ?? signal,
                    hz is { } refresh ? $"{refresh:0.##} Гц" : null,
                    desktop is not null && signal is not null && desktop != signal ? $"Сигнал {signal}" : null,
                    string.Equals(name, connector, StringComparison.OrdinalIgnoreCase) ? null : connector) ?? Unavailable));
            }
            return rows;
        }

        return rows;
    }

    private static string? ReadMonitorName(DisplayTarget path)
    {
        const uint getTargetName = 2;
        var target = new DisplayTargetName
        {
            Type = getTargetName,
            Size = (uint)Marshal.SizeOf<DisplayTargetName>(),
            AdapterId = path.AdapterId,
            TargetId = path.Id
        };
        return DisplayConfigGetDeviceInfo(ref target) == 0 ? NonEmpty(target.FriendlyName?.Trim()) : null;
    }

    private static string? FormatResolution(uint width, uint height) =>
        width > 0 && height > 0 ? $"{width} × {height}" : null;

    private static double? RefreshHz(DisplayRational rate) =>
        rate.Numerator > 0 && rate.Denominator > 0 ? rate.Numerator / (double)rate.Denominator : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public AdapterLuid AdapterId;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayRational
    {
        public uint Numerator, Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplaySource
    {
        public AdapterLuid AdapterId;
        public uint Id, ModeIndex, Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayTarget
    {
        public AdapterLuid AdapterId;
        public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public DisplayRational RefreshRate;
        public uint ScanLineOrdering, Available, Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        public DisplaySource Source;
        public DisplayTarget Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplaySourceMode
    {
        public uint Width, Height, PixelFormat;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayTargetMode
    {
        public ulong PixelRate;
        public DisplayRational HorizontalRefresh, VerticalRefresh;
        public uint Width, Height, TotalWidth, TotalHeight, SignalStandard, ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DisplayModeData
    {
        [FieldOffset(0)] public DisplaySourceMode Source;
        [FieldOffset(0)] public DisplayTargetMode Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayMode
    {
        public uint Type, Id;
        public AdapterLuid AdapterId;
        public DisplayModeData Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayTargetName
    {
        public uint Type, Size;
        public AdapterLuid AdapterId;
        public uint TargetId, Flags, Technology;
        public ushort ManufactureId, ProductCode;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DevicePath;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumDxgiAdapter(nint factory, uint index, out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapterDescription(nint adapter, out DxgiAdapterDescription description);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, [Out] DisplayMode[] modes, nint topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayTargetName request);
}
