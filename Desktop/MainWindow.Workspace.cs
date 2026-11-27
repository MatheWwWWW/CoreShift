using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private readonly LocalState localState;
    private AppPreferences preferences;
    private bool lightTheme;
    private SettingsWindow? settingsWindow;
    private IInputElement? focusBeforeSearch;

    private sealed record SearchAction(string Title, string Description, string Category, string Keywords, Action Execute);
    private sealed record SystemDrive(string Name, ulong FreeBytes, ulong TotalBytes);
    private sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

    private readonly Queue<double> cpuHistory = new();
    private readonly Queue<double> ramHistory = new();
    private CpuTimes? previousCpuTimes;

    internal bool IsLightTheme => lightTheme;
    internal bool StartMinimized => preferences.StartMinimized;
    internal bool PreferNativeInterface => preferences.UseNativeInterface;
    internal string StateDirectory => localState.DirectoryPath;

    private RadioButton[] SectionButtons =>
        [OverviewNav, PcInfoNav, MemoryNav, CleanNav, TweaksNav, PrivacyNav, ProfileNav, SnapshotsNav, PackagesNav, PersonalizationNav];

    private void InitializeWorkspace()
    {
        HostNameText.Text = Environment.MachineName;
        boardRequested = preferences.OpenBoardIn3D;
        UpdateBoardReadings();
        UpdateBoardView();
        if (NeedsStandaloneNetFramework)
            NetFrameworkButton.SetResourceReference(ContentControl.ContentProperty, "UI:.NET Framework 3.5 на сайте Microsoft");
        ApplyWorkspacePreferences();
    }

    internal void SavePreferences(AppPreferences next)
    {
        if (next == preferences)
            return;

        next.Save(localState);
        bool chooseDefaults = profileStop is null &&
            (next.CompareFocusByDefault != preferences.CompareFocusByDefault || next.UsePowerPlanByDefault != preferences.UsePowerPlanByDefault);
        bool refreshSensors = next.ShowHardwareSensors && !preferences.ShowHardwareSensors;
        if (next.OpenBoardIn3D != preferences.OpenBoardIn3D)
            boardRequested = next.OpenBoardIn3D;
        preferences = next;
        ApplyWorkspacePreferences(chooseDefaults);
        UpdateAppearance();
        if (refreshSensors && OverviewPage.IsVisible)
            _ = RefreshBoardGpuAsync(force: true);
    }

    private void ApplyWorkspacePreferences(bool chooseDefaults = true)
    {
        var interval = TimeSpan.FromSeconds(preferences.OverviewInterval);
        if (overviewTimer.Interval != interval)
        {
            overviewTimer.Interval = interval;
            ClearOverviewTrends();
        }
        if (initialized && AppUpdater.IsInstalledCopy())
            updateTimer.IsEnabled = preferences.CheckUpdatesAutomatically;

        if (chooseDefaults)
        {
            CompareMode.IsChecked = preferences.CompareFocusByDefault;
            ProfileGamePower.IsChecked = preferences.UsePowerPlanByDefault && ProfileGamePower.IsEnabled;
            UpdateProfileControls();
        }
    }

    private void RememberWorkspace()
    {
        if (!preferences.RestoreLastPage)
            return;

        int index = Array.FindIndex(SectionButtons, button => button.IsChecked == true);
        if (index < 0 || preferences.LastPage == (WorkspacePage)index)
            return;

        try
        {
            var updated = preferences with { LastPage = (WorkspacePage)index };
            updated.Save(localState);
            preferences = updated;
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
        }
    }

    private void UpdateAppearance()
    {
        StopDashboardEffects();
        lightTheme = AppAppearance.Apply(preferences);
        ApplyWindowMaterial();
        settingsWindow?.UpdateMaterial(lightTheme);
        focusLiveWindow?.UpdateMaterial(lightTheme);
        trayMenu.Items[0].Text = UiText.Get("Открыть CoreShift");
        trayMenu.Items[1].Text = UiText.Get("Выход");
        UpdateMaximizeLabel();
        if (lastOverview is { } overview)
        {
            SetMemory(overview.Memory, overview.Commit);
            SetPagefiles(overview.Pagefiles);
            SetOverviewStatus(overview.HasErrors);
        }
        RenderMemorySession();
        RenderSnapshotComparison();
        RenderComponents();
        UpdateBoardReadings();
        UpdateBoardView();
        RenderFocusComparison();
        RenderFocusSignal(updateTargets: true);
    }

    private void SetOverviewStatus(bool hasErrors)
    {
        string message = hasErrors ? UiText.Get("Часть данных Windows недоступна") :
            preferences.OverviewInterval == 1 ? UiText.Get("Обновление раз в секунду") :
            UiText.Format($"Интервал обновления {preferences.OverviewInterval} с");
        if (RefreshStatus.Text != message)
        {
            RefreshStatus.Text = message;
            RefreshStatus.SetResourceReference(TextBlock.ForegroundProperty, hasErrors ? "Warning" : "Text");
        }
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowMaterial();
        SystemEvents.UserPreferenceChanged += WindowsPreferenceChanged;
    }

    private void WindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or
            UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility))
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (!closing)
                UpdateAppearance();
        });
    }

    private void ApplyWindowMaterial()
    {
        bool glass = AppAppearance.SetWindowMaterial(this, lightTheme);
        WindowSurface.SetResourceReference(Panel.BackgroundProperty, glass ? "GlassTint" : "Canvas");
    }

    private void OpenSystemSettings(string uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
            MessageBox.Show(this, UiText.Format($"Не удалось открыть инструмент Windows. {ex.Message}"), "CoreShift");
        }
    }

    private void OpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        if (webReady)
        {
            webSettings ??= new SettingsWindow(this, preferences, projected: true);
            SendWebState();
            return;
        }
        if (settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(this, preferences) { Owner = this };
        settingsWindow = window;
        window.Closed += (_, _) => settingsWindow = null;
        window.Show();
    }

    private void OpenSnapshotsClick(object sender, RoutedEventArgs e) => SnapshotsNav.IsChecked = true;

    private void OpenSearchClick(object sender, RoutedEventArgs e) => OpenSearch();
    private void CloseSearchClick(object sender, RoutedEventArgs e) => CloseSearch();
    private void DismissSearchClick(object sender, MouseButtonEventArgs e) => CloseSearch();

    private void OpenSearch()
    {
        if (closing)
            return;

        if (!CommandOverlay.IsVisible)
            focusBeforeSearch = Keyboard.FocusedElement;
        CommandOverlay.Visibility = Visibility.Visible;
        UiMotion.Enter(CommandPanel);
        RefreshSearch();
        SearchQuery.Focus();
        SearchQuery.SelectAll();
    }

    private void CloseSearch()
    {
        CommandOverlay.Visibility = Visibility.Collapsed;
        if (focusBeforeSearch is { } previous)
            Keyboard.Focus(previous);
        focusBeforeSearch = null;
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (webReady)
            return;
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CloseSearch();
            OpenSettings();
            e.Handled = true;
            return;
        }

        if (CommandOverlay is not { IsVisible: true })
            return;

        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                break;
            case Key.Enter when SearchResults.SelectedItem is SearchAction action:
                RunSearchAction(action);
                break;
            case Key.Up or Key.Down when SearchResults.Items.Count > 0:
                SearchResults.SelectedIndex = Math.Clamp(SearchResults.SelectedIndex + (e.Key == Key.Up ? -1 : 1),
                    0, SearchResults.Items.Count - 1);
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void SearchQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (CommandOverlay is { IsVisible: true })
            RefreshSearch();
    }

    private void SearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(SearchResults, source) is ListBoxItem { Content: SearchAction action })
            RunSearchAction(action);
    }

    private void RunSearchAction(SearchAction action)
    {
        CloseSearch();
        action.Execute();
    }

    private void RefreshSearch()
    {
        string[] words = SearchQuery.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = SearchActions().Where(action => words.All(word =>
                action.Keywords.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(action => words.Length > 0 && action.Title.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        SearchResults.ItemsSource = results;
        SearchResults.SelectedIndex = results.Length > 0 ? 0 : -1;
        SearchEmpty.Visibility = VisibleWhen(results.Length == 0);
        SearchResults.Visibility = VisibleWhen(results.Length > 0);
    }

    private SearchAction[] SearchActions()
    {
        SearchAction Create(string title, string description, string category, string keywords, Action execute) =>
            new(UiText.Get(title), UiText.Get(description), UiText.Get(category),
                string.Join(" ", title, UiText.Get(title), description, UiText.Get(description), keywords), execute);
        SearchAction Page(string title, string description, string keywords, RadioButton nav) =>
            Create(title, description, "Раздел", keywords, () => nav.IsChecked = true);
        SearchAction Windows(string title, string uri) =>
            Create(title, "Открыть параметры Windows", "Windows", uri, () => OpenSystemSettings(uri));
        return
        [
            Create("Шифтер", "Свободный чат и справка по CoreShift", "Действие", "ии ai помощник assistant фото скриншот shifter", OpenAssistant),
            Page("Обзор системы", "Текущая нагрузка и свободное место", "live overview cpu", OverviewNav),
            Page("О компьютере", "Комплектующие, мониторы и отчёт", "hardware gpu cpu display specs", PcInfoNav),
            Page("Память", "ОЗУ, подкачка и рабочие наборы", "ram commit pagefile process", MemoryNav),
            Page("Очистка", "Поиск временных файлов и кэшей", "temp cleanup shaders", CleanNav),
            Page("Оптимизация", "Производительность и фоновые задачи", "tweak performance power", TweaksNav),
            Page("Телеметрия", "Диагностика и отчёты Windows",
                "privacy telemetry diagnostics clipboard speech реклама буфер речь журналы дампы", PrivacyNav),
            Page("Персонализация", "Темы, панель задач и оформление Windows", "personalization theme taskbar", PersonalizationNav),
            Page("UWP-приложения", "Управление пакетами текущего пользователя", "appx uwp store uninstall", PackagesNav),
            Create("Разрешения приложений", "Камера, микрофон, геолокация и данные учётной записи", "Раздел",
                "privacy camera microphone location account", () => { PrivacyNav.IsChecked = true; PrivacyPolicyTabs.SelectedIndex = 3; BringControlIntoView(PrivacyPolicyTabs); }),
            Create("Проводник", "Расширения, скрытые файлы и папки", "Раздел", "explorer file extensions hidden",
                () => { PersonalizationNav.IsChecked = true; PersonalizationTabs.SelectedIndex = 1; BringControlIntoView(PersonalizationTabs); }),
            Page("Профиль фокуса", "Приоритет программы и отчёт по сеансу", "focus process priority games", ProfileNav),
            Page("До и после", "Сравнить память и место на диске", "snapshot compare before after до после", SnapshotsNav),
            Create("Компоненты Windows", "DirectPlay, .NET Framework и WinSxS", "Раздел", "dism optional features",
                () => { TweaksNav.IsChecked = true; MaintenanceActions.SelectedIndex = 2; }),
            Create("Гибернация и быстрый запуск", "Проверить режимы сна", "Раздел", "hibernate fast startup sleep питание",
                () => { TweaksNav.IsChecked = true; MaintenanceActions.SelectedIndex = 0; BringControlIntoView(HibernateCard); }),
            Create("Восстановление", "SFC, DISM и точки восстановления", "Раздел", "sfc dism restore",
                () => { TweaksNav.IsChecked = true; MaintenanceActions.SelectedIndex = 3; }),
            Create("Приложения и процессы", "Найти процесс по имени или PID", "Действие", "working set trim ram процессы",
                () => OpenProcessSearch("")),
            Create("Файл подкачки", "Использование, размер и системные дампы", "Раздел", "swap pagefile virtual memory подкачка",
                () =>
                {
                    MemoryNav.IsChecked = true;
                    BringControlIntoView(MemoryPagefileCard);
                }),
            Create("Сканировать временные файлы", "Показать список файлов перед очисткой", "Действие", "scan temp cache",
                () => { CleanNav.IsChecked = true; if (ScanButton.IsEnabled) ScanClick(ScanButton, new RoutedEventArgs()); }),
            Create("Снять показания", "Сохранить текущий снимок системы", "Действие", "capture snapshot baseline",
                () => { CaptureSnapshotClick(CaptureSnapshotButton, new RoutedEventArgs()); SnapshotsNav.IsChecked = true; }),
            Windows("Хранилище", "ms-settings:storagesense"),
            Windows("Установленные приложения", "ms-settings:appsfeatures"),
            Windows("Обновления Windows", "ms-settings:windowsupdate"),
            Windows("Автозагрузка Windows", "ms-settings:startupapps"),
            Create("Диспетчер задач", "Открыть Диспетчер задач Windows", "Windows", "taskmgr task manager",
                () =>
                {
                    TweaksNav.IsChecked = true;
                    OpenStartupImpactClick(TweaksNav, new RoutedEventArgs());
                })
        ];
    }

    private void BringControlIntoView(FrameworkElement control)
    {
        if (webReady)
            webScrollTarget = control.Name;
        else
            control.BringIntoView();
    }

    private static SystemDrive? ReadSystemDrive()
    {
        if (Path.GetPathRoot(Environment.SystemDirectory) is not { } root)
            return null;

        var drive = new DriveInfo(root);
        return new SystemDrive(root.TrimEnd('\\'), (ulong)drive.AvailableFreeSpace, (ulong)drive.TotalSize);
    }

    private static CpuTimes? ReadCpuTimes() => GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
        ? new CpuTimes(idle, kernel, user) : null;

    private void SetOverviewTelemetry(Overview snapshot)
    {
        var drive = snapshot.Drive;
        if (lastOverview?.Drive != drive)
        {
            SetChanged(SystemDiskFreeText, FormatBytes(drive?.FreeBytes));
            SetChanged(SystemDiskVolumeText, drive is null ? "—" : $"{drive.Name} · {FormatBytes(drive.TotalBytes)}");
            SystemDiskMeter.Value = drive is { TotalBytes: > 0 }
                ? 100d * Math.Min(1d, (double)drive.FreeBytes / drive.TotalBytes) : 0;
        }
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        SetChanged(ComponentUptime, uptime.Days > 0
            ? UiText.Format($"{uptime:%d} д {uptime:hh} ч {uptime:mm} мин")
            : UiText.Format($"{uptime:%h} ч {uptime:mm} мин"));
        if (snapshot.Cpu is { } reading && previousCpuTimes is { } previous &&
            reading.Kernel >= previous.Kernel && reading.User >= previous.User && reading.Idle >= previous.Idle)
        {
            ulong total = reading.Kernel - previous.Kernel + reading.User - previous.User;
            if (total > 0)
            {
                double rate = 100d * (total - Math.Min(total, reading.Idle - previous.Idle)) / total;
                SetChanged(CpuLoadText, $"{rate:0}%");
                AddTrendSample(cpuHistory, rate);
                DrawTrend(cpuHistory, CpuTrendPlot, CpuTrendLine);
            }
        }
        else
            SetChanged(CpuLoadText, "—");
        previousCpuTimes = snapshot.Cpu;
        if (snapshot.Memory is { TotalBytes: > 0 } memory)
        {
            AddTrendSample(ramHistory, 100d * Math.Max(0, 1d - (double)memory.AvailableBytes / memory.TotalBytes));
            DrawTrend(ramHistory, RamTrendPlot, RamTrendLine);
        }
        UpdateBoardReadings();
    }

    private void CpuTrendSizeChanged(object sender, SizeChangedEventArgs e) => DrawTrend(cpuHistory, CpuTrendPlot, CpuTrendLine);
    private void RamTrendSizeChanged(object sender, SizeChangedEventArgs e) => DrawTrend(ramHistory, RamTrendPlot, RamTrendLine);

    private void AddTrendSample(Queue<double> history, double value)
    {
        history.Enqueue(value);
        if (history.Count > 60 / preferences.OverviewInterval)
            history.Dequeue();
    }

    private void ClearOverviewTrends()
    {
        if (cpuHistory.Count == 0 && ramHistory.Count == 0 && previousCpuTimes is null)
            return;
        cpuHistory.Clear();
        ramHistory.Clear();
        previousCpuTimes = null;
        CpuTrendLine.Data = null;
        RamTrendLine.Data = null;
    }

    private void DrawTrend(Queue<double> history, Canvas? plot, System.Windows.Shapes.Path? line)
    {
        if (webReady)
            return;
        if (line is null || plot is null || history.Count == 0)
            return;

        var geometry = new StreamGeometry();
        using (var renderer = geometry.Open())
        {
            int index = 0;
            double step = plot.ActualWidth / Math.Max(1, history.Count - 1);
            double height = Math.Max(0, plot.ActualHeight - 2);
            foreach (double value in history)
            {
                var point = new Point(index * step, (1 - value / 100) * height + 1);
                if (index == 0)
                    renderer.BeginFigure(point, false, false);
                else
                    renderer.LineTo(point, true, false);
                index++;
            }
        }
        geometry.Freeze();
        line.Data = geometry;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
