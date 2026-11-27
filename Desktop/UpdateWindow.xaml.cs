using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class UpdateWindow : Window
{
    private const int ErrorCancelled = 1223;
    private readonly Process previousApp;
    private readonly string installDirectory;
    private readonly Version requestedVersion;
    private readonly Action releaseUpdateMutex;
    private readonly CancellationTokenSource downloadStop = new();
    private bool running = true;
    private bool downloading;
    private bool installing;
    private bool installationCompleted;
    private bool restartRequired;

    internal UpdateWindow(Process previousApp, string installDirectory, Version requestedVersion, Action releaseUpdateMutex)
    {
        this.previousApp = previousApp;
        this.installDirectory = installDirectory;
        this.requestedVersion = requestedVersion;
        this.releaseUpdateMutex = releaseUpdateMutex;
        InitializeComponent();
        ShowActivity(UiText.Get("Закрываем основное окно…"));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (SystemParameters.ClientAreaAnimation)
            Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));

        string? installerPath = null;
        string? logPath = null;
        int? installerExitCode = null;
        try
        {
            var token = downloadStop.Token;
            using (previousApp)
            using (var handoff = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                await previousApp.WaitForExitAsync(handoff.Token);
            token.ThrowIfCancellationRequested();

            ShowActivity(UiText.Get("Проверяем релиз на GitHub…"));
            var release = await AppUpdater.FetchLatestAsync(token);
            if (release is null || release.Version < requestedVersion)
                throw new InvalidDataException("The requested update is no longer published.");

            installerPath = AppUpdater.CreateInstallerPath();
            logPath = Path.ChangeExtension(installerPath, ".log");
            downloading = true;
            Stage.Text = UiText.Format($"Скачиваем версию {release.Version}…");
            SetProgress(0);
            using var installer = await AppUpdater.DownloadAsync(release, installerPath, token,
                new Progress<int>(OnDownloadProgress));
            token.ThrowIfCancellationRequested();

            downloading = false;
            installing = true;
            ActionButton.IsEnabled = false;
            ShowActivity(UiText.Get("Устанавливаем обновление…"));
            using var setup = AppUpdater.StartInstaller(installer.Name, installDirectory, logPath);
            await setup.WaitForExitAsync();
            installerExitCode = setup.ExitCode;
            if (installerExitCode == 3010)
            {
                restartRequired = true;
                ShowResult(UiText.Get("Обновление установлено"), UiText.Get("Перезагрузите Windows, чтобы завершить установку."), UiText.Get("Закрыть"));
                return;
            }

            if (installerExitCode != 0)
                throw new InvalidOperationException($"CoreShift Setup exited with {installerExitCode}. See {logPath}.");

            installationCompleted = true;
            ShowResult(UiText.Get("Обновление установлено"), UiText.Get("Открываем CoreShift…"), UiText.Get("Открыть CoreShift"), enableAction: false);
            if (SystemParameters.ClientAreaAnimation)
                await Task.Delay(260);
            ReopenCoreShift();
        }
        catch (OperationCanceledException) when (downloadStop.IsCancellationRequested)
        {
            ReopenCoreShift();
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            Debug.WriteLine(ex);
            ShowFailure(installing ? UiText.Get("Не удалось установить обновление.") : UiText.Get("Не удалось скачать или проверить обновление."));
        }
        finally
        {
            if (installerPath is not null)
                AppUpdater.TryDelete(installerPath);
            if (logPath is not null && installerExitCode is null or 0)
                AppUpdater.TryDelete(logPath);
        }
    }

    private void OnDownloadProgress(int value)
    {
        if (!downloading || downloadStop.IsCancellationRequested)
            return;

        if (value == 100)
        {
            downloading = false;
            ShowActivity(UiText.Get("Проверяем установщик…"));
        }
        else
        {
            SetProgress(value);
        }
    }

    private void ShowActivity(string text)
    {
        Stage.Text = text;
        Percent.Text = "";
        UpdateProgress.BeginAnimation(ProgressBar.ValueProperty, null);
        UpdateProgress.Value = 25;
        UpdateProgress.IsIndeterminate = SystemParameters.ClientAreaAnimation;
    }

    private void SetProgress(int value, bool showPercent = true)
    {
        bool wasIndeterminate = UpdateProgress.IsIndeterminate;
        double previous = wasIndeterminate ? 0 : UpdateProgress.Value;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.BeginAnimation(ProgressBar.ValueProperty, null);
        UpdateProgress.Value = value;
        if (SystemParameters.ClientAreaAnimation && previous != value)
            UpdateProgress.BeginAnimation(ProgressBar.ValueProperty, new DoubleAnimation(previous, value,
                TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        Percent.Text = showPercent ? $"{value}%" : "";
    }

    private void ShowResult(string title, string message, string action, bool enableAction = true)
    {
        running = !restartRequired;
        Heading.Text = title;
        Stage.Text = message;
        ActionButton.Content = action;
        ActionButton.IsEnabled = enableAction;
        SetProgress(100);
    }

    private void ShowFailure(string message)
    {
        Heading.Text = UiText.Get("Обновление прервано");
        SetProgress(0, showPercent: false);
        ShowRetry(message);
    }

    private void ShowRetry(string message)
    {
        running = false;
        downloading = installing = false;
        Stage.Text = message;
        Stage.Foreground = (Brush)FindResource("Warning");
        ActionButton.Content = UiText.Get("Открыть CoreShift");
        ActionButton.IsEnabled = true;
    }

    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (running)
            CancelDownload();
        else if (restartRequired)
            Close();
        else
            ReopenCoreShift();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (running)
        {
            e.Cancel = true;
            if (!installing)
                CancelDownload();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        downloadStop.Dispose();
        base.OnClosed(e);
    }

    private void CancelDownload()
    {
        ActionButton.IsEnabled = false;
        ShowActivity(UiText.Get("Отменяем обновление…"));
        downloadStop.Cancel();
    }

    private void ReopenCoreShift()
    {
        try
        {
            releaseUpdateMutex();
            string executable = Path.Combine(installDirectory, AppUpdater.ExecutableName);
            using var application = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = installDirectory
            });
            running = false;
            Close();
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            Debug.WriteLine(ex);
            string message = ex is Win32Exception { NativeErrorCode: ErrorCancelled }
                ? UiText.Get("Запуск отменён. При повторной попытке подтвердите запрос Windows.")
                : UiText.Get("Не удалось открыть CoreShift. Попробуйте ещё раз или откройте его из меню «Пуск».");
            if (installationCompleted)
                ShowRetry(message);
            else
                ShowFailure(message);
        }
    }
}
