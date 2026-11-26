using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace CoreShift.Desktop;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private Mutex? updateMutex;
    private EventWaitHandle? showWindow;
    private RegisteredWaitHandle? showRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Shutdown(1);
            return;
        }

        if (e.Args is ["--update", var parent, var ready, var version])
        {
            StartUpdateWorker(parent, ready, version);
            return;
        }

        if (Mutex.TryOpenExisting(AppUpdater.WorkerMutex, out var worker))
        {
            using (worker)
            {
                if (!TryAcquire(worker))
                {
                    Shutdown();
                    return;
                }
                worker.ReleaseMutex();
            }
        }

        string instanceName = $@"Local\CoreShift.998EEA5D.{identity.User!.Value}.Administrator";
        showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        instanceMutex = new Mutex(true, instanceName, out bool firstInstance);
        if (!firstInstance)
        {
            showWindow.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showWindow, (_, _) =>
            Dispatcher.InvokeAsync(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

        window.Show();
    }

    private void StartUpdateWorker(string parent, string ready, string version)
    {
        Process? previousApp = null;
        try
        {
            string? directory = AppUpdater.InstalledDirectory();
            if (directory is null || !int.TryParse(parent, out int pid) || !Version.TryParse(version, out var target) ||
                !ready.StartsWith(AppUpdater.WorkerReadyPrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(ready.AsSpan(AppUpdater.WorkerReadyPrefix.Length), "N", out _))
            {
                Shutdown(1);
                return;
            }

            string executable = Path.Combine(directory, AppUpdater.ExecutableName);
            previousApp = Process.GetProcessById(pid);
            if (!string.Equals(previousApp.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(1);
                return;
            }

            using var handoff = EventWaitHandle.OpenExisting(ready);
            var gate = new Mutex(false, AppUpdater.WorkerMutex);
            if (!TryAcquire(gate))
            {
                gate.Dispose();
                Shutdown(1);
                return;
            }
            updateMutex = gate;

            var window = new UpdateWindow(previousApp, directory, target, ReleaseUpdateMutex);
            MainWindow = window;
            window.Show();
            previousApp = null;
            handoff.Set();
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex) || ex is WaitHandleCannotBeOpenedException)
        {
            Debug.WriteLine(ex);
            Shutdown(1);
        }
        finally
        {
            previousApp?.Dispose();
        }
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private void ReleaseUpdateMutex()
    {
        if (updateMutex is not { } mutex)
            return;

        updateMutex = null;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && MainWindow is MainWindow window)
            window.PrepareForSessionEnd();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showRegistration?.Unregister(null);
        showWindow?.Dispose();
        instanceMutex?.Dispose();
        ReleaseUpdateMutex();
        base.OnExit(e);
    }
}
