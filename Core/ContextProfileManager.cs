using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartResourceDispatcher.Core;

internal sealed class ContextProfileManager(string processName, ProcessPriorityClass priority)
{
    private sealed record ActiveProcess(Process Process, ProcessPriorityClass OriginalPriority);

    private ActiveProcess? active;
    private int? observedPid;
    private bool reportedRestoreFailure;

    internal static string NormalizeProcessName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Укажите имя процесса, например notepad.exe.");

        return name;
    }

    internal bool Watch(CancellationToken stop, Action started, Action<string> report)
    {
        using var mutex = new Mutex(false, @"Local\SmartResourceDispatcher.ContextProfile");
        try
        {
            if (!mutex.WaitOne(0))
                throw new InvalidOperationException("В этой сессии уже работает контекстный профиль.");
        }
        catch (AbandonedMutexException)
        {
            // A previous monitor stopped without releasing the mutex.
        }

        bool restored = false;
        try
        {
            started();
            while (!stop.IsCancellationRequested)
            {
                Observe(ReadForegroundProcessId(), report);
                stop.WaitHandle.WaitOne(250);
            }
        }
        finally
        {
            try { restored = Restore(report); }
            finally { mutex.ReleaseMutex(); }
        }

        return restored;
    }

    internal void Observe(int? foregroundPid, Action<string> report)
    {
        if (foregroundPid == observedPid && (active is null || !active.Process.HasExited))
            return;

        if (!Restore(report))
            return;

        observedPid = foregroundPid;
        if (foregroundPid is not > 4 || foregroundPid.Value == Environment.ProcessId)
            return;

        Process? process = null;
        try
        {
            process = Process.GetProcessById(foregroundPid.Value);
            if (!process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                return;

            // Keep the handle to the same process instance until the priority is restored.
            _ = process.SafeHandle;
            ProcessPriorityClass original = process.PriorityClass;
            if (original == priority)
                return;

            process.PriorityClass = priority;
            active = new ActiveProcess(process, original);
            process = null;
            report($"Профиль активирован для {processName} ({foregroundPid}): {original} → {priority}.");
        }
        catch (ArgumentException)
        {
            // The process exited before it could be opened.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            report($"Не удалось применить профиль к PID {foregroundPid}: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal bool Restore(Action<string> report)
    {
        if (active is not { } entry)
            return true;

        Process process = entry.Process;
        try
        {
            process.Refresh();
            if (process.HasExited)
                report($"Процесс {processName} ({process.Id}) завершился.");
            else if (process.PriorityClass == priority)
            {
                process.PriorityClass = entry.OriginalPriority;
                report($"Восстановлен приоритет {processName} ({process.Id}): {entry.OriginalPriority}.");
            }
            else
                report($"Приоритет {processName} ({process.Id}) изменён извне. Текущее значение сохранено.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            if (!process.HasExited)
            {
                if (!reportedRestoreFailure)
                    report($"Не удалось восстановить приоритет PID {process.Id}: {ex.Message}");
                reportedRestoreFailure = true;
                return false;
            }
        }

        process.Dispose();
        active = null;
        reportedRestoreFailure = false;
        return true;
    }

    internal static int? ReadForegroundProcessId()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out int processId) == 0)
            return null;

        return processId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}
