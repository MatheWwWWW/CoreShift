using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartResourceDispatcher.Core;

internal sealed class ContextProfileManager
{
    internal readonly record struct SelectedProcess(int Id, DateTime StartedUtc);
    internal readonly record struct WatchResult(bool Restored, bool ProcessExited);
    internal enum PriorityState { Applied, AlreadySet, Restored, External, Unavailable, Exited }
    internal readonly record struct PriorityUpdate(int ProcessId, PriorityState State,
        ProcessPriorityClass? Original, ProcessPriorityClass Requested);

    private sealed record ActiveProcess(Process Process, string Name, ProcessPriorityClass OriginalPriority);
    private readonly string? processName;
    private readonly ProcessPriorityClass priority;
    private readonly bool useGamePowerPlan;
    private readonly Dictionary<int, SelectedProcess> selection;
    private readonly Dictionary<int, ActiveProcess> active = [];
    private readonly HashSet<int> attempted = [];
    private readonly HashSet<int> reportedRestoreFailures = [];
    private GamePowerProfile? powerProfile;

    internal ContextProfileManager(string processName, ProcessPriorityClass priority, bool useGamePowerPlan = false,
        SelectedProcess? selected = null)
    {
        this.processName = processName;
        this.priority = priority;
        this.useGamePowerPlan = useGamePowerPlan;
        selection = selected is { } target ? new Dictionary<int, SelectedProcess> { [target.Id] = target } : [];
    }

    internal ContextProfileManager(IEnumerable<SelectedProcess> processes, ProcessPriorityClass priority, bool useGamePowerPlan = false)
    {
        this.priority = priority;
        this.useGamePowerPlan = useGamePowerPlan;
        selection = processes.Distinct().ToDictionary(target => target.Id);
        if (selection.Count == 0)
            throw new ArgumentException("At least one process must be selected.", nameof(processes));
    }

    internal static string NormalizeProcessName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException(UiText.Get("Укажите имя процесса, например notepad.exe."));

        return name;
    }

    internal WatchResult Watch(CancellationToken stop, Action started, Action<string> report,
        Action<int, DateTime>? processFocused = null, Func<bool>? applyProfile = null, bool followInBackground = false,
        bool requireAllTargets = false, Action<PriorityUpdate>? priorityChanged = null)
    {
        using var mutex = new Mutex(false, @"Local\SmartResourceDispatcher.ContextProfile");
        return Watch(mutex, stop, started, report, processFocused, applyProfile, followInBackground, requireAllTargets, priorityChanged);
    }

    internal WatchResult Watch(Mutex mutex, CancellationToken stop, Action started, Action<string> report,
        Action<int, DateTime>? processFocused = null, Func<bool>? applyProfile = null, bool followInBackground = false,
        bool requireAllTargets = false, Action<PriorityUpdate>? priorityChanged = null)
    {
        using var powerMutex = useGamePowerPlan ? new Mutex(false, @"Global\SmartResourceDispatcher.GamePowerPlan") : null;
        try
        {
            if (!mutex.WaitOne(0))
                throw new InvalidOperationException(UiText.Get("В этой сессии уже работает контекстный профиль."));
        }
        catch (AbandonedMutexException) { }

        var watched = new Dictionary<int, Process>();
        bool restored = false;
        bool processExited = false;
        bool ownsPowerMutex = false;
        try
        {
            foreach (var target in selection.Values)
            {
                try
                {
                    if (OpenProcess(target.Id) is { } process)
                        watched.Add(target.Id, process);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    priorityChanged?.Invoke(new PriorityUpdate(target.Id, PriorityState.Unavailable, null, priority));
                    report(UiText.Format($"Не удалось применить профиль к PID {target.Id}: {ex.Message}"));
                }
            }
            if (SelectionEnded())
                return new WatchResult(Restored: true, ProcessExited: true);

            if (powerMutex is not null)
            {
                try { ownsPowerMutex = powerMutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsPowerMutex = true; }
                if (!ownsPowerMutex)
                    throw new InvalidOperationException(UiText.Get("Планом питания уже управляет другой профиль CoreShift."));
                if (!GamePowerProfile.IsAvailable())
                    throw new InvalidOperationException(UiText.Get("В Windows не найден план «Высокая производительность»."));
                powerProfile = new GamePowerProfile();
            }

            started();
            while (!stop.IsCancellationRequested)
            {
                foreach (var (id, process) in watched)
                {
                    if (!process.HasExited)
                        continue;

                    if (!active.ContainsKey(id))
                    {
                        priorityChanged?.Invoke(new PriorityUpdate(id, PriorityState.Exited, null, priority));
                        report(UiText.Format($"Процесс PID {id} завершился."));
                    }
                    process.Dispose();
                    watched.Remove(id);
                }

                if (SelectionEnded())
                {
                    processExited = true;
                    break;
                }

                bool enabled = applyProfile?.Invoke() != false;
                int? foreground = enabled && !followInBackground ? ReadForegroundProcessId() : null;
                if (selection.Count == 0)
                    Observe(foreground, report, processFocused);
                else
                {
                    bool apply = enabled &&
                        (followInBackground || foreground is int pid && watched.ContainsKey(pid));
                    UpdatePriorities(apply ? watched.Keys : [], report, processFocused, priorityChanged);
                }
                powerProfile?.Update(report);
                stop.WaitHandle.WaitOne(250);
            }
        }
        finally
        {
            try
            {
                restored = Restore(report, priorityChanged);
            }
            finally
            {
                foreach (var process in watched.Values)
                    process.Dispose();
                restored &= powerProfile?.Restore(report) ?? true;
                powerProfile?.Dispose();
                powerProfile = null;
                if (ownsPowerMutex)
                    powerMutex!.ReleaseMutex();
                mutex.ReleaseMutex();
            }
        }

        return new WatchResult(restored, processExited);

        bool SelectionEnded() => selection.Count > 0 && (watched.Count == 0 || requireAllTargets && watched.Count != selection.Count);
    }

    private Process? OpenProcess(int pid)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            // Keep the handle so that the same PID cannot identify a different process later.
            _ = process.SafeHandle;
            if (process.HasExited || processName is not null && !string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase) ||
                selection.TryGetValue(pid, out var expected) && process.StartTime.ToUniversalTime() != expected.StartedUtc)
                return null;
            var opened = process;
            process = null;
            return opened;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal void Observe(int? foregroundPid, Action<string> report, Action<int, DateTime>? processFocused = null)
    {
        if (foregroundPid is not > 4 || foregroundPid == Environment.ProcessId ||
            selection.Count > 0 && !selection.ContainsKey(foregroundPid.Value))
        {
            UpdatePriorities([], report, processFocused);
            return;
        }

        UpdatePriorities([foregroundPid.Value], report, processFocused);
    }

    private void UpdatePriorities(IReadOnlyCollection<int> targets, Action<string> report, Action<int, DateTime>? processFocused,
        Action<PriorityUpdate>? priorityChanged = null)
    {
        foreach (int pid in active.Keys)
        {
            if (!targets.Contains(pid))
                Restore(pid, report, priorityChanged);
        }
        attempted.RemoveWhere(pid => !targets.Contains(pid) && !active.ContainsKey(pid));

        foreach (int pid in targets)
        {
            if (pid > 4 && pid != Environment.ProcessId && attempted.Add(pid))
                Apply(pid, report, processFocused, priorityChanged);
        }
    }

    private void Apply(int pid, Action<string> report, Action<int, DateTime>? processFocused, Action<PriorityUpdate>? priorityChanged)
    {
        Process? process = null;
        try
        {
            process = OpenProcess(pid);
            if (process is null)
            {
                priorityChanged?.Invoke(new PriorityUpdate(pid, PriorityState.Unavailable, null, priority));
                return;
            }

            string name = process.ProcessName;
            processFocused?.Invoke(pid, process.StartTime.ToUniversalTime());
            powerProfile?.Track(process);
            var original = process.PriorityClass;
            if (original == priority)
            {
                priorityChanged?.Invoke(new PriorityUpdate(pid, PriorityState.AlreadySet, original, priority));
                return;
            }

            process.PriorityClass = priority;
            active.Add(pid, new ActiveProcess(process, name, original));
            process = null;
            priorityChanged?.Invoke(new PriorityUpdate(pid, PriorityState.Applied, original, priority));
            report(UiText.Format($"Профиль активирован для {name} ({pid}): {original} → {priority}."));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            priorityChanged?.Invoke(new PriorityUpdate(pid, PriorityState.Unavailable, null, priority));
            report(UiText.Format($"Не удалось применить профиль к PID {pid}: {ex.Message}"));
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal bool Restore(Action<string> report, Action<PriorityUpdate>? priorityChanged = null)
    {
        bool restored = true;
        foreach (int pid in active.Keys)
            restored &= Restore(pid, report, priorityChanged);
        attempted.Clear();
        return restored;
    }

    private bool Restore(int pid, Action<string> report, Action<PriorityUpdate>? priorityChanged)
    {
        var entry = active[pid];
        var process = entry.Process;
        var state = PriorityState.Exited;
        try
        {
            process.Refresh();
            if (process.HasExited)
                report(UiText.Format($"Процесс {entry.Name} ({pid}) завершился."));
            else if (process.PriorityClass == priority)
            {
                process.PriorityClass = entry.OriginalPriority;
                state = PriorityState.Restored;
                report(UiText.Format($"Восстановлен приоритет {entry.Name} ({pid}): {entry.OriginalPriority}."));
            }
            else
            {
                state = PriorityState.External;
                report(UiText.Format($"Приоритет {entry.Name} ({pid}) изменён извне. Текущее значение сохранено."));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            if (!process.HasExited)
            {
                if (reportedRestoreFailures.Add(pid))
                {
                    priorityChanged?.Invoke(new PriorityUpdate(pid, PriorityState.Unavailable, entry.OriginalPriority, priority));
                    report(UiText.Format($"Не удалось восстановить приоритет PID {pid}: {ex.Message}"));
                }
                return false;
            }
        }

        process.Dispose();
        active.Remove(pid);
        reportedRestoreFailures.Remove(pid);
        priorityChanged?.Invoke(new PriorityUpdate(pid, state, entry.OriginalPriority, priority));
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
