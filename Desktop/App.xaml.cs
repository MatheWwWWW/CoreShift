using System.Windows;

namespace CoreShift.Desktop;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? showWindow;
    private RegisteredWaitHandle? showRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        const string instanceName = @"Local\CoreShift.998EEA5D";
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
        base.OnExit(e);
    }
}
