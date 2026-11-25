using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal sealed record PagefileUsage(string Name, uint? AllocatedMiB, uint? CurrentMiB, uint? PeakMiB);
internal sealed record MemoryCommit(ulong CurrentBytes, ulong PeakBytes, ulong LimitBytes)
{
    public ulong AvailableBytes => LimitBytes - Math.Min(CurrentBytes, LimitBytes);
}
internal sealed record PhysicalMemory(ulong TotalBytes, ulong AvailableBytes);
internal sealed record MemorySnapshot(PhysicalMemory Physical, MemoryCommit Commit);
internal sealed record CrashDumpSettings(int? Mode, bool HasDedicatedFile);

internal static class MemoryAndPagefileManager
{
    internal static ulong? ReadInstalledRam()
    {
        using var query = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
        using var results = query.Get();
        foreach (ManagementObject result in results)
        {
            using (result)
            {
                if (result["TotalPhysicalMemory"] is ulong bytes && bytes > 0)
                    return bytes;
            }
        }

        return null;
    }

    internal static IReadOnlyList<PagefileUsage> ReadPagefiles()
    {
        using var query = new ManagementObjectSearcher(
            "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
        using var results = query.Get();
        var files = new List<PagefileUsage>();
        foreach (ManagementObject result in results)
        {
            using (result)
            {
                files.Add(new PagefileUsage(result["Name"] as string ?? "неизвестно",
                    result["AllocatedBaseSize"] as uint?, result["CurrentUsage"] as uint?,
                    result["PeakUsage"] as uint?));
            }
        }

        return files;
    }

    internal static IReadOnlyList<PagefileUsage> ReadLivePagefiles()
    {
        var files = new List<PagefileUsage>();
        int pageSize = Environment.SystemPageSize;
        uint ToMiB(nuint pages) => (uint)((ulong)pages * (uint)pageSize / (1024 * 1024));

        bool success = EnumPageFilesW((IntPtr _, in PagefileInformation info, string name) =>
        {
            string path = name.StartsWith(@"\??\", StringComparison.Ordinal) ? name[4..] : name;
            files.Add(new PagefileUsage(path, ToMiB(info.TotalSize), ToMiB(info.TotalInUse), ToMiB(info.PeakUsage)));
            return true;
        }, IntPtr.Zero);
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return files;
    }

    internal static MemoryCommit ReadCommit()
    {
        return ReadMemorySnapshot().Commit;
    }

    internal static MemorySnapshot ReadMemorySnapshot()
    {
        var info = ReadPerformanceInformation();
        return new MemorySnapshot(new PhysicalMemory((ulong)info.PhysicalTotal * info.PageSize, (ulong)info.PhysicalAvailable * info.PageSize),
            new MemoryCommit((ulong)info.CommitTotal * info.PageSize, (ulong)info.CommitPeak * info.PageSize,
                (ulong)info.CommitLimit * info.PageSize));
    }

    internal static bool? ReadAutomaticPagefileManagement()
    {
        using var query = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
        using var results = query.Get();
        foreach (ManagementObject result in results)
        {
            using (result)
                return result["AutomaticManagedPagefile"] as bool?;
        }

        return null;
    }

    private static PerformanceInformation ReadPerformanceInformation()
    {
        uint size = (uint)Marshal.SizeOf<PerformanceInformation>();
        if (!GetPerformanceInfo(out var info, size))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return info;
    }

    internal static CrashDumpSettings ReadCrashDumpSettings()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
        int? mode = key?.GetValue("CrashDumpEnabled") as int?;
        if (mode == 1 && key?.GetValue("FilterPages") is 1)
            mode = 10;

        bool dedicated = key?.GetValue("DedicatedDumpFile") is string path && !string.IsNullOrWhiteSpace(path);
        return new CrashDumpSettings(mode, dedicated);
    }

    internal static string ExplainManualSizing(ulong? ramBytes, MemoryCommit? commit, CrashDumpSettings? dump)
    {
        const ulong mib = 1024 * 1024;
        var notes = new List<string>();
        if (ramBytes is > 0 && commit is not null)
        {
            ulong workloadBytes = commit.PeakBytes > ramBytes.Value ? commit.PeakBytes - ramBytes.Value : 0;
            notes.Add(workloadBytes == 0
                ? "Пик выделенной памяти с последней загрузки укладывался в объём ОЗУ."
                : $"Пик выделенной памяти с последней загрузки превысил объём ОЗУ на {(workloadBytes + mib - 1) / mib} МБ. Разница помогает оценить размер подкачки без запаса на рост нагрузки.");
        }
        else
        {
            notes.Add("Для расчёта вручную нужны объём ОЗУ и пиковая выделенная память под типичной нагрузкой.");
        }

        if (dump is { HasDedicatedFile: true })
            notes.Add("Для дампов указан отдельный файл. Проверьте его доступность и размер.");
        else
            notes.Add(dump?.Mode switch
            {
                0 => "Системные дампы отключены.",
                1 when ramBytes is > 0 => $"Для полного дампа требуется не менее {(ramBytes.Value + mib - 1) / mib + 257} МБ.",
                1 => "Для полного дампа нужен объём ОЗУ плюс 257 МБ.",
                3 => "Для малого дампа нужен минимум 1 МБ.",
                2 or 7 or 10 => "Точный минимум для выбранного системного дампа зависит от его объёма.",
                _ => "Режим системных дампов неизвестен. Проверьте его перед ручной настройкой."
            });

        return string.Join(Environment.NewLine, notes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public nuint SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PagefileInformation
    {
        public uint Size, Reserved;
        public nuint TotalSize, TotalInUse, PeakUsage;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool PagefileCallback(IntPtr context, in PagefileInformation info, string name);

    [DllImport("psapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPageFilesW(PagefileCallback callback, IntPtr context);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);
}
