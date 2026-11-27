using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private bool gamePowerPlanAvailable;
    private bool workingWithDownloadLimit;
    private bool workingWithIndexExclusions;

    private void InitializePerformanceControls()
    {
        gamePowerPlanAvailable = GamePowerProfile.IsAvailable();
        ProfileGamePower.IsEnabled = gamePowerPlanAvailable;
        if (!gamePowerPlanAvailable)
            ProfilePowerStatus.Text = UiText.Get("План «Высокая производительность» недоступен на этом компьютере.");

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            WindowedGamesHelp.Text = UiText.Get("Оптимизация оконных игр доступна в Windows 11 версии 22H2 и новее.");
            WindowedGamesButton.IsEnabled = false;
        }
    }

    private Task RefreshPerformanceAsync() => Task.WhenAll(RefreshDownloadLimitAsync(), RefreshIndexExclusionsAsync(),
        RefreshPagefileTweakAsync(), RefreshWindowEffectsAsync());

    private async Task RefreshDownloadLimitAsync()
    {
        if (workingWithDownloadLimit)
            return;

        SetDownloadLimitControls(false, false);
        workingWithDownloadLimit = true;
        try { await ReadDownloadLimitAsync(); }
        finally { workingWithDownloadLimit = false; }
    }

    private async Task ReadDownloadLimitAsync()
    {
        WindowsTweak tweak = TweaksEngine.BackgroundDownloadLimit;
        try
        {
            var (unsupported, state) = await RunInBackground(() => (TweaksEngine.SupportIssue(tweak), TweaksEngine.ReadSetting(tweak)));
            DownloadLimitState.Text = unsupported ?? state.Current switch
            {
                null => UiText.Get("Системная политика лимита не задана."),
                0 => UiText.Get("В политике задан автоматический лимит."),
                var percent => UiText.Format($"В политике задан лимит {percent}%."),
            };
            DownloadLimitValue.Text = (state.Current is >= 1 and <= 100 ? state.Current.Value : tweak.AppliedValue)
                .ToString(CultureInfo.CurrentCulture);
            SetDownloadLimitControls(unsupported is null, state.HasBackup);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            DownloadLimitState.Text = UiText.Format($"Не удалось прочитать политику. {ex.Message}");
            SetDownloadLimitControls(false, false);
        }
    }

    private void SetDownloadLimitControls(bool canApply, bool canRestore)
    {
        DownloadLimitValue.IsEnabled = canApply;
        ApplyDownloadLimitButton.IsEnabled = canApply;
        RestoreDownloadLimitButton.IsEnabled = canRestore;
    }

    private async void ApplyDownloadLimitClick(object sender, RoutedEventArgs e)
    {
        DownloadLimitStatus.Visibility = Visibility.Visible;
        if (!int.TryParse(DownloadLimitValue.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int percent) || percent is < 1 or > 100)
        {
            SetStatus(DownloadLimitStatus, UiText.Get("Укажите целое число от 1 до 100."), error: true);
            return;
        }
        await ChangeDownloadLimitAsync(TweaksEngine.BackgroundDownloadLimit with { AppliedValue = percent }, restore: false);
    }

    private async void RestoreDownloadLimitClick(object sender, RoutedEventArgs e) =>
        await ChangeDownloadLimitAsync(TweaksEngine.BackgroundDownloadLimit, restore: true);

    private async Task ChangeDownloadLimitAsync(WindowsTweak tweak, bool restore)
    {
        if (workingWithDownloadLimit)
            return;

        workingWithDownloadLimit = true;
        SetDownloadLimitControls(false, false);
        DownloadLimitStatus.Visibility = Visibility.Visible;
        SetStatus(DownloadLimitStatus, UiText.Get("Обновляем политику загрузок…"));
        try
        {
            bool changed = await RunInBackground(() => TweaksEngine.Change(tweak, restore));
            SetStatus(DownloadLimitStatus, (restore, changed) switch
            {
                (false, true) => UiText.Format($"Записан лимит {tweak.AppliedValue}%."),
                (false, false) => UiText.Get("Этот лимит уже задан."),
                (true, true) => UiText.Get("Исходная настройка восстановлена."),
                _ => UiText.Get("Сохранённая настройка отсутствует.")
            });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(DownloadLimitStatus, ex.Message, error: true);
        }
        finally
        {
            if (!closing)
                await ReadDownloadLimitAsync();
            workingWithDownloadLimit = false;
        }
    }

    private async Task RefreshIndexExclusionsAsync()
    {
        if (workingWithIndexExclusions)
            return;

        workingWithIndexExclusions = true;
        UpdateIndexExclusionControls();
        try { await ReadIndexExclusionsAsync(); }
        finally
        {
            workingWithIndexExclusions = false;
            UpdateIndexExclusionControls();
        }
    }

    private async Task ReadIndexExclusionsAsync()
    {
        try
        {
            var folders = await RunInBackground(SearchIndexExclusions.ReadSavedFolders);
            var selected = IndexExclusionsList.SelectedItem as string;
            IndexExclusionsList.ItemsSource = folders;
            IndexExclusionsList.Visibility = VisibleWhen(folders.Length > 0);
            IndexExclusionsList.SelectedItem = folders.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : folders.FirstOrDefault();
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(IndexExclusionStatus, UiText.Format($"Не удалось загрузить список исключений. {ex.Message}"), error: true);
        }
    }

    private void IndexExclusionSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateIndexExclusionControls();

    private void UpdateIndexExclusionControls()
    {
        AddIndexExclusionButton.IsEnabled = !workingWithIndexExclusions;
        RestoreIndexExclusionButton.IsEnabled = AddIndexExclusionButton.IsEnabled && IndexExclusionsList.SelectedItem is string;
    }

    private async void AddIndexExclusionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = UiText.Get("Выберите папку для исключения из индексации") };
        if (dialog.ShowDialog(this) == true)
            await ChangeIndexExclusionAsync(dialog.FolderName, restore: false);
    }

    private async void RestoreIndexExclusionClick(object sender, RoutedEventArgs e)
    {
        if (IndexExclusionsList.SelectedItem is string folder)
            await ChangeIndexExclusionAsync(folder, restore: true);
    }

    private async Task ChangeIndexExclusionAsync(string folder, bool restore)
    {
        if (workingWithIndexExclusions)
            return;

        workingWithIndexExclusions = true;
        UpdateIndexExclusionControls();
        SetStatus(IndexExclusionStatus, UiText.Get("Обновляем правила индексации…"));
        try
        {
            bool changed = await RunInBackground(() => SearchIndexExclusions.Change(folder, restore));
            SetStatus(IndexExclusionStatus, (restore, changed) switch
            {
                (false, true) => UiText.Format($"Папка исключена из индексации: {folder}"),
                (false, false) => UiText.Get("Папка уже вне области индексации или для неё задано отдельное правило."),
                (true, true) => UiText.Format($"Восстановлена исходная настройка для папки: {folder}"),
                _ => UiText.Get("Сохранённое исключение отсутствует.")
            });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(IndexExclusionStatus, UiText.Format($"Не удалось изменить правила индексации. {ex.Message}"), error: true);
        }
        finally
        {
            if (!closing)
                await ReadIndexExclusionsAsync();
            workingWithIndexExclusions = false;
            UpdateIndexExclusionControls();
        }
    }

    private void OpenPerformanceSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            OpenWindowsTool(new ProcessStartInfo(uri) { UseShellExecute = true }, PerformanceSettingsStatus);
    }

    private void OpenTweakSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            OpenWindowsTool(new ProcessStartInfo(uri) { UseShellExecute = true }, PrivacyStatus);
    }

    private void OpenStartupImpactClick(object sender, RoutedEventArgs e)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")) { UseShellExecute = true };
        OpenWindowsTool(startInfo, PerformanceSettingsStatus);
    }

    private void OpenWindowsTool(ProcessStartInfo startInfo, TextBlock status)
    {
        try
        {
            using var process = Process.Start(startInfo);
            status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            status.Visibility = Visibility.Visible;
            SetStatus(status, UiText.Format($"Не удалось открыть инструмент Windows. {ex.Message}"), error: true);
            status.BringIntoView();
        }
    }
}
