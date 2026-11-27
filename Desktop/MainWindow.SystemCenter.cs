using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string NetFrameworkGuide = "https://learn.microsoft.com/dotnet/framework/install/dotnet-35-windows-11";
    private static readonly bool NeedsStandaloneNetFramework = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 28000);

    private sealed class PackageRow(WindowsOperations.Package package)
    {
        public WindowsOperations.Package Package { get; } = package;
        public string Name => Package.DisplayName;
        public string Version => Package.Version;
        public bool CanRemove => WindowsOperations.CanRemove(Package);
        public bool IsSelected { get; set; }
    }

    private PackageRow[] packageRows = [];
    private ICollectionView? packageView;
    private bool packagesLoaded;
    private bool workingWithPackages;
    private bool workingWithDiagnostics;
    private bool workingWithMaintenance;

    private async void RefreshPackagesClick(object sender, RoutedEventArgs e) => await RefreshPackagesAsync();

    private async Task RefreshPackagesAsync()
    {
        if (workingWithPackages || closing)
            return;
        workingWithPackages = true;
        UpdatePackageControls();
        SetStatus(PackagesStatus, "Получаем пакеты текущего пользователя…");
        try
        {
            await ReadPackagesIntoViewAsync();
            if (!closing)
                SetStatus(PackagesStatus, UiText.Format($"Установлено пакетов: {packageRows.Length}."));
        }
        catch (Exception ex) when (IsSystemError(ex) || ex is JsonException)
        {
            SetStatus(PackagesStatus, ex.Message, error: true);
        }
        finally
        {
            workingWithPackages = false;
            UpdatePackageControls();
        }
    }

    private async Task ReadPackagesIntoViewAsync()
    {
        var selected = packageRows.Where(row => row.IsSelected).Select(row => row.Package.FullName).ToHashSet();
        var installed = await TrackOperation(WindowsOperations.ReadPackagesAsync());
        if (closing)
            return;
        packageRows = installed.OrderBy(package => package.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(package => new PackageRow(package) { IsSelected = selected.Contains(package.FullName) }).ToArray();
        packageView = CollectionViewSource.GetDefaultView(packageRows);
        packageView.Filter = item => item is PackageRow row && (PackagesOptionalOnly.IsChecked != true || row.CanRemove) &&
            (row.Name.Contains(PackagesFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
             row.Package.Name.Contains(PackagesFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        PackagesList.ItemsSource = packageView;
        packagesLoaded = true;
    }

    private void PackageFilterChanged(object sender, RoutedEventArgs e)
    {
        packageView?.Refresh();
        if (packagesLoaded)
            UpdatePackageControls();
    }

    private void PackageChecked(object sender, RoutedEventArgs e) => UpdatePackageControls();

    private void UpdatePackageControls()
    {
        int count = packageRows.Count(row => row.IsSelected && row.CanRemove);
        RefreshPackagesButton.IsEnabled = !workingWithPackages;
        PackagesList.IsEnabled = packagesLoaded && !workingWithPackages;
        RemovePackagesButton.IsEnabled = packagesLoaded && !workingWithPackages && count > 0;
        PackageSelectionText.Text = UiText.Format($"Выбрано приложений: {count}");
        PackagesEmpty.Visibility = VisibleWhen(packagesLoaded && PackagesList.Items.Count == 0);
    }

    private async void RemovePackagesClick(object sender, RoutedEventArgs e)
    {
        var selected = packageRows.Where(row => row.IsSelected && row.CanRemove).Select(row => row.Package).ToArray();
        if (workingWithPackages || closing || selected.Length == 0)
            return;
        if (!Confirm(UiText.Get("Удалить выбранные UWP-приложения?"),
            UiText.Get("Приложения удалятся для текущего пользователя. Локальные данные этих приложений могут быть потеряны.") +
            Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, selected.Select(package => package.DisplayName)),
            UiText.Get("Удалить выбранное")))
            return;

        workingWithPackages = true;
        UpdatePackageControls();
        try
        {
            var removed = 0;
            var failures = new List<string>();
            foreach (var package in selected)
            {
                if (closing)
                    break;
                SetStatus(PackagesStatus, UiText.Format($"Удаляем {package.DisplayName}…"));
                try
                {
                    await TrackOperation(RemoveOnePackageAsync(package));
                    removed++;
                }
                catch (Exception ex) when (IsSystemError(ex))
                {
                    failures.Add(package.Name + " · " + ex.Message);
                }
            }
            if (closing)
                return;
            try { await ReadPackagesIntoViewAsync(); }
            catch (Exception ex) when (IsSystemError(ex) || ex is JsonException)
            {
                packagesLoaded = false;
                failures.Add(ex.Message);
            }
            SetStatus(PackagesStatus, UiText.Format($"Удалено: {removed} из {selected.Length}.") +
                (failures.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, failures)), error: failures.Count > 0);
        }
        finally
        {
            workingWithPackages = false;
            UpdatePackageControls();
        }

        static async Task<bool> RemoveOnePackageAsync(WindowsOperations.Package package)
        {
            await WindowsOperations.RemovePackageAsync(package);
            return true;
        }
    }

    private void OpenMicrosoftStoreClick(object sender, RoutedEventArgs e) => OpenSystemSettings("ms-windows-store://home");
    private void OpenWindowsLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            OpenSystemSettings(uri);
    }

    private async Task RefreshDiagnosticPolicyAsync()
    {
        if (workingWithDiagnostics || closing)
            return;
        workingWithDiagnostics = true;
        SetDiagnosticControls(false, false);
        try
        {
            var policy = TweaksEngine.DiagnosticData;
            var (state, offIssue) = await RunInBackground(() => (TweaksEngine.ReadSetting(policy), TweaksEngine.SupportIssue(policy)));
            DiagnosticOff.IsEnabled = offIssue is null;
            DiagnosticHint.Text = offIssue ?? UiText.Get("Эта редакция позволяет отключить диагностические данные Windows.");
            DiagnosticPicker.SelectedIndex = state.Current switch
            {
                0 when offIssue is null => 0,
                3 => 2,
                _ => 1
            };
            DiagnosticCurrent.Text = state.Current switch
            {
                null => UiText.Get("Уровень задаётся Windows или её настройками."),
                0 when offIssue is not null => UiText.Get("Записан уровень 0. Эта редакция применяет обязательный минимум."),
                0 => UiText.Get("Диагностические данные отключены политикой."),
                1 => UiText.Get("Разрешены только обязательные данные."),
                3 => UiText.Get("Разрешены необязательные диагностические данные."),
                var value => UiText.Format($"В политике записано значение {value}.")
            };
            SetDiagnosticControls(!closing, state.HasBackup && !closing);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(DiagnosticCurrent, ex.Message, error: true);
        }
        finally { workingWithDiagnostics = false; }
    }

    private void SetDiagnosticControls(bool enabled, bool canRestore)
    {
        DiagnosticPicker.IsEnabled = enabled;
        DiagnosticApplyButton.IsEnabled = enabled;
        DiagnosticRestoreButton.IsEnabled = canRestore;
    }

    private async void ChangeDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (workingWithDiagnostics || closing)
            return;
        bool restore = ReferenceEquals(sender, DiagnosticRestoreButton);
        int value = DiagnosticPicker.SelectedIndex switch { 0 => 0, 2 => 3, _ => 1 };
        var policy = TweaksEngine.DiagnosticData with { AppliedValue = value };
        if (!restore && !Confirm(UiText.Get("Изменить уровень диагностики Windows?"),
            UiText.Get("Политика применяется ко всему компьютеру. CoreShift сохранит прежнее значение для восстановления."),
            UiText.Get("Применить")))
            return;

        workingWithDiagnostics = true;
        SetDiagnosticControls(false, false);
        DiagnosticActionStatus.Visibility = Visibility.Visible;
        try
        {
            bool changed = await RunInBackground(() => TweaksEngine.Change(policy, restore));
            SetStatus(DiagnosticActionStatus, changed ? (restore ? "Исходное значение политики восстановлено." : "Политика диагностики сохранена.")
                : "Изменений нет.");
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(DiagnosticActionStatus, ex.Message, error: true);
        }
        finally
        {
            workingWithDiagnostics = false;
            await RefreshDiagnosticPolicyAsync();
        }
    }

    private async void RunMaintenanceClick(object sender, RoutedEventArgs e)
    {
        if (workingWithMaintenance || closing || sender is not Button { Tag: string tool } button)
            return;
        if (tool == "netfx3" && NeedsStandaloneNetFramework)
        {
            OpenSystemSettings(NetFrameworkGuide);
            return;
        }
        bool readsOnly = tool is "analyze" or "sfc-check" or "dism-scan" or "power-states";
        string label = button.Content?.ToString() ?? "";
        if (!readsOnly && !Confirm(label,
            UiText.Get("Windows запустит системную операцию. Она может занять несколько минут."),
            UiText.Get("Запустить")))
            return;

        workingWithMaintenance = true;
        bool fromPrivacy = PrivacyNav.IsChecked == true;
        var progress = fromPrivacy ? PrivacyMaintenanceProgress : MaintenanceProgress;
        var outputBox = fromPrivacy ? PrivacyMaintenanceOutput : MaintenanceOutput;
        var status = fromPrivacy ? PrivacyMaintenanceStatus : MaintenanceStatus;
        MaintenanceActions.IsEnabled = false;
        PrivacyMaintenanceButtons.IsEnabled = false;
        if (!fromPrivacy)
        {
            MaintenanceResult.Visibility = Visibility.Visible;
            BringControlIntoView(MaintenanceResult);
        }
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = true;
        outputBox.Visibility = Visibility.Collapsed;
        outputBox.Text = "";
        SetStatus(status, UiText.Format($"Выполняется «{label}»…"));
        try
        {
            var result = await TrackOperation(tool switch
            {
                "sfc-check" => WindowsOperations.RunAsync("sfc.exe", ["/verifyonly"], Encoding.Unicode),
                "sfc" => WindowsOperations.RunAsync("sfc.exe", ["/scannow"], Encoding.Unicode),
                "dism" => WindowsOperations.RunAsync("dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth"]),
                "dism-scan" => WindowsOperations.RunAsync("dism.exe", ["/Online", "/Cleanup-Image", "/ScanHealth"]),
                "analyze" => WindowsOperations.RunAsync("dism.exe", ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore"]),
                "cleanup" => WindowsOperations.RunAsync("dism.exe", ["/Online", "/Cleanup-Image", "/StartComponentCleanup"]),
                "directplay" => WindowsOperations.RunAsync("dism.exe", ["/Online", "/Enable-Feature", "/FeatureName:DirectPlay", "/All", "/NoRestart"]),
                "netfx3" => WindowsOperations.RunAsync("dism.exe", ["/Online", "/Enable-Feature", "/FeatureName:NetFx3", "/All", "/NoRestart"]),
                "restore-point" => WindowsOperations.CreateRestorePointAsync(),
                "power-states" => WindowsOperations.RunAsync("powercfg.exe", ["/availableSleepStates"]),
                "hibernate-on" => WindowsOperations.RunAsync("powercfg.exe", ["/hibernate", "on"]),
                "hibernate-off" => WindowsOperations.RunAsync("powercfg.exe", ["/hibernate", "off"]),
                _ => throw new ArgumentException(nameof(tool))
            });
            if (closing)
                return;
            string output = string.Join(Environment.NewLine, new[] { result.Output.Trim(), result.Error.Trim() }.Where(value => value.Length > 0));
            outputBox.Text = output.Length > 24000 ? output[^24000..] : output;
            outputBox.Visibility = VisibleWhen(output.Length > 0);
            SetStatus(status, result.ExitCode switch
            {
                0 when output.Length == 0 => UiText.Get("Операция завершилась."),
                0 => UiText.Get("Операция завершилась. Результат Windows приведён ниже."),
                3010 or 1641 => UiText.Get("Операция завершилась. Перезагрузите Windows."),
                var code => UiText.Format($"Инструмент Windows завершился с кодом {code}.")
            }, error: result.ExitCode is not (0 or 3010 or 1641));
            if (result.ExitCode is 0 or 3010 or 1641)
                CompleteTaskSound();
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(status, ex.Message, error: true);
        }
        finally
        {
            workingWithMaintenance = false;
            MaintenanceActions.IsEnabled = true;
            PrivacyMaintenanceButtons.IsEnabled = true;
            progress.IsIndeterminate = false;
            progress.Visibility = Visibility.Collapsed;
            if (!fromPrivacy && !closing && MaintenanceResult.IsVisible)
                MaintenanceResult.BringIntoView();
        }
    }

    private void OpenWindowsUtilityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string entry })
            return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, entry)) { UseShellExecute = true });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            if (workingWithMaintenance)
                return;
            MaintenanceResult.Visibility = Visibility.Visible;
            MaintenanceOutput.Visibility = Visibility.Collapsed;
            SetStatus(MaintenanceStatus, UiText.Format($"Не удалось открыть инструмент Windows. {ex.Message}"), error: true);
            MaintenanceResult.BringIntoView();
        }
    }
}
