using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartResourceDispatcher.Core;

internal sealed class GamePowerProfile : IDisposable
{
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private readonly Dictionary<int, Process> games = [];
    private Guid? originalPlan;
    private bool attemptedForSession;
    private bool reportedRestoreFailure;

    internal static bool IsAvailable()
    {
        uint size = 0;
        return PowerReadFriendlyName(0, in HighPerformance, 0, 0, 0, ref size) == 0 && size > 0;
    }

    internal void Track(Process process)
    {
        if (games.TryGetValue(process.Id, out Process? previous))
        {
            if (!previous.HasExited)
                return;
            games.Remove(process.Id);
            previous.Dispose();
        }

        Process game = Process.GetProcessById(process.Id);
        try
        {
            // Open the handle now so a reused PID cannot keep the profile alive.
            _ = game.SafeHandle;
            if (game.StartTime == process.StartTime)
            {
                games.Add(game.Id, game);
                return;
            }
        }
        catch
        {
            game.Dispose();
            throw;
        }
        game.Dispose();
    }

    internal void Update(Action<string> report)
    {
        foreach (var (pid, game) in games)
        {
            if (!game.HasExited)
                continue;
            game.Dispose();
            games.Remove(pid);
        }

        bool onAc = GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;
        if (games.Count == 0 || !onAc)
        {
            if (Restore(report))
                attemptedForSession = false;
            return;
        }

        if (attemptedForSession)
            return;

        attemptedForSession = true;
        try
        {
            Guid current = ReadActivePlan();
            if (current == HighPerformance)
                return;
            Check(PowerSetActiveScheme(0, in HighPerformance));
            originalPlan = current;
            report(UiText.Get("Включён план питания «Высокая производительность»."));
        }
        catch (Win32Exception ex)
        {
            report(UiText.Format($"Не удалось переключить план питания: {ex.Message}"));
        }
    }

    internal bool Restore(Action<string> report)
    {
        if (originalPlan is not { } original)
            return true;

        try
        {
            if (ReadActivePlan() == HighPerformance)
            {
                Check(PowerSetActiveScheme(0, in original));
                report(UiText.Get("Восстановлен исходный план питания."));
            }
            else
            {
                report(UiText.Get("План питания изменён в Windows. Сохранён текущий план."));
            }

            originalPlan = null;
            reportedRestoreFailure = false;
            return true;
        }
        catch (Win32Exception ex)
        {
            if (!reportedRestoreFailure)
                report(UiText.Format($"Не удалось восстановить план питания: {ex.Message}"));
            reportedRestoreFailure = true;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (Process game in games.Values)
            game.Dispose();
        games.Clear();
    }

    private static Guid ReadActivePlan()
    {
        Check(PowerGetActiveScheme(0, out nint plan));
        try { return Marshal.PtrToStructure<Guid>(plan); }
        finally { LocalFree(plan); }
    }

    private static void Check(uint result)
    {
        if (result != 0)
            throw new Win32Exception((int)result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRoot, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint userRoot, in Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(nint userRoot, in Guid schemeGuid, nint subGroup, nint setting,
        nint buffer, ref uint bufferSize);
}
