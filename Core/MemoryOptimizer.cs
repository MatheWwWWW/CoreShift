using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartResourceDispatcher.Core;

internal static class MemoryOptimizer
{
    internal static void TrimWorkingSet(int processId) => TrimWorkingSet(processId, null);

    internal static (ulong? BeforeBytes, ulong? AfterBytes) TrimWorkingSet(int processId, DateTime? expectedStartUtc)
    {
        if (processId <= 4 || processId == Environment.ProcessId)
            throw new ArgumentException("Укажите PID другого пользовательского процесса.");

        const uint queryLimitedInformation = 0x1000;
        const uint setQuota = 0x0100;
        using var handle = OpenProcess(queryLimitedInformation | setQuota, false, processId);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (expectedStartUtc is not { } startUtc)
        {
            if (!EmptyWorkingSet(handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return (null, null);
        }

        if (!GetProcessTimes(handle, out long created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (created != startUtc.ToFileTimeUtc())
            throw new InvalidOperationException("Выбранный процесс уже завершился.");

        ulong? before = ReadWorkingSet(handle);
        if (!EmptyWorkingSet(handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (before, ReadWorkingSet(handle));
    }

    private static ulong? ReadWorkingSet(SafeProcessHandle handle)
    {
        uint size = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
        return GetProcessMemoryInfo(handle, out var counters, size) ? (ulong)counters.WorkingSetSize : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out ProcessMemoryCounters counters, uint size);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);
}
