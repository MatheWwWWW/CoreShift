using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartResourceDispatcher.Core;

internal static class MemoryOptimizer
{
    internal static void TrimWorkingSet(int processId)
    {
        if (processId <= 4 || processId == Environment.ProcessId)
            throw new ArgumentException("Укажите PID другого пользовательского процесса.");

        const uint queryLimitedInformation = 0x1000;
        const uint setQuota = 0x0100;
        using var handle = OpenProcess(queryLimitedInformation | setQuota, false, processId);
        if (handle.IsInvalid || !EmptyWorkingSet(handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);
}
