using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SmartResourceDispatcher.Core;
using Tray = System.Windows.Forms;

namespace CoreShift.Desktop;

public partial class MainWindow : Window
{
    private sealed record Overview(
        PhysicalMemory? Memory, MemoryCommit? Commit, IReadOnlyList<PagefileUsage>? Pagefiles,
        CpuTimes? Cpu, SystemDrive? Drive, bool HasErrors);

    private sealed record PagefileRow(string Name, string Usage);
    private sealed record CleanRow(string Path, string Size);
    private sealed record CleanCategoryRow(string Heading, string Roots, string Skipped);
    private sealed record CleanPreview(List<TempFile> Candidates, List<CleanRow> Files, CleanCategoryRow[] Categories,
        long TotalBytes, bool IncludesWerQueue);
    private sealed record ProfileApplicationRow(string ProcessName, ContextProfileManager.SelectedProcess Process, string Label)
    {
        public string DisplayName => $"{ProcessName}.exe";
        public bool IsSelected { get; set; }
    }

    private ProfileApplicationRow[] profileApplications = [];
    private ICollectionView? profileApplicationView;
    private sealed record TweakRow(WindowsTweak Tweak, string State, bool CanApply, bool CanRestore)
    {
        public string Name => UiText.Get(Tweak.Name);
        public string Description => UiText.Get(Tweak.Description);
        public string Risk => UiText.Get(Tweak.Risk);
        public string? SettingsUri => Tweak.Id switch
        {
            "advertising-id" => "ms-settings:privacy-general",
            "app-diagnostics" => "ms-settings:privacy-appdiagnostics",
            "online-speech" => "ms-settings:privacy-speech",
            "clipboard-sync" or "clipboard-history" => "ms-settings:clipboard",
            "app-location" => "ms-settings:privacy-location",
            "app-camera" => "ms-settings:privacy-webcam",
            "app-microphone" => "ms-settings:privacy-microphone",
            "app-account" => "ms-settings:privacy-accountinfo",
            "cloud-search" when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) => "ms-settings:search-permissions",
            "search-highlights" when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) => "ms-settings:search-permissions",
            "recent-apps" => "ms-settings:personalization-start",
            _ => null
        };
    }

    private readonly Tray.NotifyIcon trayIcon;
    private readonly Tray.ContextMenuStrip trayMenu = new();
    private readonly System.Drawing.Icon? appIcon;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer overviewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource updateLifetime = new();
    private readonly HashSet<Task> pendingOperations = [];
    private readonly ObservableCollection<string> profileEvents = [];
    private CleanPreview? lastScan;
    private int cleanSelectionVersion;
    private CancellationTokenSource? profileStop;
    private Task<ContextProfileManager.WatchResult>? profileTask;
    private bool loadingPcInfo;
    private bool refreshingOverview;
    private long lastDiskRead;
    private Overview? lastOverview;
    private bool loadingTweaks;
    private bool loadingProfileApplications;
    private bool closing;
    private bool allowClose;
    private bool initialized;
    private bool checkingUpdate;
    private bool installingUpdate;
    private AppRelease? availableUpdate;

    internal MainWindow(LocalState? localState = null)
    {
        this.localState = localState ?? LocalState.UserProfile;
        preferences = AppPreferences.Load(this.localState);
        lightTheme = AppAppearance.Apply(preferences);
        InitializeComponent();
        CleanTemp.Checked += CleanCategoryChanged;
        CleanTemp.Unchecked += CleanCategoryChanged;
        CleanThumbnails.Checked += CleanCategoryChanged;
        CleanThumbnails.Unchecked += CleanCategoryChanged;
        CleanIcons.Checked += CleanCategoryChanged;
        CleanIcons.Unchecked += CleanCategoryChanged;
        CleanDirectX.Checked += CleanCategoryChanged;
        CleanDirectX.Unchecked += CleanCategoryChanged;
        CleanNvidia.Checked += CleanCategoryChanged;
        CleanNvidia.Unchecked += CleanCategoryChanged;
        CleanWerArchive.Checked += CleanCategoryChanged;
        CleanWerArchive.Unchecked += CleanCategoryChanged;
        CleanWerQueue.Checked += CleanCategoryChanged;
        CleanWerQueue.Unchecked += CleanCategoryChanged;
        CleanFontCache.Checked += CleanCategoryChanged;
        CleanFontCache.Unchecked += CleanCategoryChanged;
        InitializePerformanceControls();
        InitializeMemoryDiagnostics();
        InitializeWorkspace();
        InitializeFocusComparison();
        ProfileEvents.ItemsSource = profileEvents;

        if (Environment.ProcessPath is { } exePath)
            appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        trayMenu.Items.Add(UiText.Get("Открыть CoreShift"), null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(UiText.Get("Выход"), null, async (_, _) => await ExitAsync());
        trayIcon = new Tray.NotifyIcon
        {
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            Text = "CoreShift",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Tray.MouseButtons.Left)
                ShowFromTray();
        };
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        overviewTimer.Tick += async (_, _) => await RefreshOverviewAsync();
        OverviewPage.IsVisibleChanged += (_, _) => UpdateOverviewPolling();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
            return;

        initialized = true;
        if (!webReady)
            UiMotion.Enter(PageContent);
        var startupPage = preferences.RestoreLastPage ? preferences.LastPage : preferences.StartPage;
        SectionButtons[(int)startupPage].IsChecked = true;
        UpdateOverviewPolling();
        UpdateMemoryPolling();
        if (AppUpdater.IsInstalledCopy() && preferences.CheckUpdatesAutomatically)
        {
            updateTimer.Start();
            await CheckForUpdatesAsync();
        }
    }

    internal async Task<string> CheckForUpdatesAsync()
    {
        if (checkingUpdate || installingUpdate || closing)
            return "Проверка обновлений занята.";

        checkingUpdate = true;
        try
        {
            var update = await AppUpdater.FetchLatestAsync(updateLifetime.Token);
            if (closing || installingUpdate)
                return "Проверка обновлений занята.";
            if (update?.Version != availableUpdate?.Version)
            {
                availableUpdate = update;
                UpdateBanner.Visibility = VisibleWhen(update is not null);
                if (update is not null)
                    SetStatus(UpdateStatus, UiText.Format($"Версия {update.Version}"));
            }
            return update is null ? "Обновлений для установки нет." : "Обновление доступно в главном окне.";
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            Debug.WriteLine(ex);
            return "Не удалось проверить обновления.";
        }
        finally
        {
            checkingUpdate = false;
        }
    }

    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update || installingUpdate || closing)
            return;

        installingUpdate = true;
        InstallUpdateButton.IsEnabled = false;
        SetStatus(UpdateStatus, UiText.Get("Готовим окно обновления…"));
        try
        {
            if (!await PrepareToExitAsync())
            {
                SetStatus(UpdateStatus, UiText.Get("Не удалось восстановить приоритет процесса. Повторите установку после проверки профиля."), error: true);
                return;
            }

            await AppUpdater.StartWorkerAsync(update.Version);
            CloseForExit();
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            Debug.WriteLine(ex);
            CancelExit();
            SetStatus(UpdateStatus, UiText.Get("Не удалось открыть окно обновления. Попробуйте ещё раз."), error: true);
        }
        finally
        {
            installingUpdate = false;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async void Navigate(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || closing)
            return;

        OverviewPage.Visibility = VisibleWhen(ReferenceEquals(sender, OverviewNav));
        PcInfoPage.Visibility = VisibleWhen(ReferenceEquals(sender, PcInfoNav));
        MemoryPage.Visibility = VisibleWhen(ReferenceEquals(sender, MemoryNav));
        CleanPage.Visibility = VisibleWhen(ReferenceEquals(sender, CleanNav));
        TweaksPage.Visibility = VisibleWhen(ReferenceEquals(sender, TweaksNav));
        PrivacyPage.Visibility = VisibleWhen(ReferenceEquals(sender, PrivacyNav));
        PersonalizationPage.Visibility = VisibleWhen(ReferenceEquals(sender, PersonalizationNav));
        PackagesPage.Visibility = VisibleWhen(ReferenceEquals(sender, PackagesNav));
        ProfilePage.Visibility = VisibleWhen(ReferenceEquals(sender, ProfileNav));
        SnapshotsPage.Visibility = VisibleWhen(ReferenceEquals(sender, SnapshotsNav));
        PageScroll.ScrollToTop();
        if (!webReady)
            UiMotion.Enter(PageContent);

        if (ReferenceEquals(sender, TweaksNav))
            await Task.WhenAll(RefreshTweaksAsync(), RefreshPerformanceAsync());
        else if (ReferenceEquals(sender, PrivacyNav))
        {
            LoadWindowsSettings();
            await Task.WhenAll(RefreshTweaksAsync(), RefreshDiagnosticPolicyAsync());
        }
        else if (ReferenceEquals(sender, PersonalizationNav))
        {
            LoadWindowsSettings();
            await Task.WhenAll(RefreshTweaksAsync(), RefreshWindowEffectsAsync());
        }
        else if (ReferenceEquals(sender, PackagesNav) && !packagesLoaded)
            await RefreshPackagesAsync();
        else if (ReferenceEquals(sender, PcInfoNav) && !loadingPcInfo)
            await LoadPcInfoAsync();
        else if (ReferenceEquals(sender, ProfileNav))
        {
            RenderFocusComparison();
            await Task.WhenAll(RefreshProfileApplicationsAsync(), LoadFocusReportsAsync());
        }
        else if (ReferenceEquals(sender, SnapshotsNav))
            await LoadSnapshotsAsync();
    }

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    private void OpenCleanClick(object sender, RoutedEventArgs e) => CleanNav.IsChecked = true;
    private void OpenProfileClick(object sender, RoutedEventArgs e) => ProfileNav.IsChecked = true;
    private void OpenMemoryClick(object sender, RoutedEventArgs e) => MemoryNav.IsChecked = true;
    private void OpenHardwareClick(object sender, RoutedEventArgs e) => PcInfoNav.IsChecked = true;

    private async void UpdateOverviewPolling()
    {
        if (initialized)
            UpdateBoardView();
        if (!initialized || closing || !OverviewPage.IsVisible || WindowState == WindowState.Minimized)
        {
            overviewTimer.Stop();
            lastDiskRead = 0;
            ClearOverviewTrends();
            return;
        }

        if (!overviewTimer.IsEnabled)
        {
            overviewTimer.Start();
            if (componentInventory is null)
                _ = RefreshComponentsAsync();
            await RefreshOverviewAsync();
        }
    }

    private async void OverviewDetailsExpanded(object sender, RoutedEventArgs e) => await RefreshOverviewAsync();

    private async Task RefreshOverviewAsync()
    {
        if (refreshingOverview || closing || !overviewTimer.IsEnabled)
            return;

        refreshingOverview = true;
        try
        {
            bool showPagefiles = OverviewDetails.IsExpanded;
            bool readStorage = lastDiskRead == 0 || Stopwatch.GetElapsedTime(lastDiskRead) >= TimeSpan.FromSeconds(5);
            var cachedDrive = lastOverview?.Drive;
            var overview = await RunInBackground(() => ReadOverview(showPagefiles, readStorage, cachedDrive));
            if (closing || !overviewTimer.IsEnabled)
                return;

            if (lastOverview is not { } previous || previous.Memory != overview.Memory || previous.Commit != overview.Commit)
                SetMemory(overview.Memory, overview.Commit);
            if (lastOverview is null || !SamePagefiles(lastOverview.Pagefiles, overview.Pagefiles))
                SetPagefiles(overview.Pagefiles);
            SetOverviewTelemetry(overview);
            SetOverviewStatus(overview.HasErrors);
            lastOverview = overview;
            if (readStorage && overview.Drive is not null)
                lastDiskRead = Stopwatch.GetTimestamp();
            if (boardSelection == BoardPart.Graphics)
                _ = RefreshBoardGpuAsync();
        }
        finally
        {
            refreshingOverview = false;
        }
    }

    private static bool SamePagefiles(IReadOnlyList<PagefileUsage>? previous, IReadOnlyList<PagefileUsage>? current) =>
        previous is null ? current is null : current is not null && previous.SequenceEqual(current);

    private static Overview ReadOverview(bool showPagefiles, bool readStorage, SystemDrive? cachedDrive)
    {
        bool hasErrors = false;
        T? Read<T>(Func<T?> read) where T : class
        {
            try { return read(); }
            catch (Exception ex) when (IsSystemError(ex))
            {
                hasErrors = true;
                return null;
            }
        }

        var snapshot = Read(MemoryAndPagefileManager.ReadMemorySnapshot);
        var pagefiles = showPagefiles ? Read(MemoryAndPagefileManager.ReadLivePagefiles) : null;
        var cpu = ReadCpuTimes();
        var drive = readStorage ? Read(ReadSystemDrive) : cachedDrive;
        return new Overview(snapshot?.Physical, snapshot?.Commit, pagefiles, cpu, drive, hasErrors);
    }

    private void SetMemory(PhysicalMemory? memory, MemoryCommit? commit)
    {
        CommitPeak.Text = FormatBytes(commit?.PeakBytes);
        CommitLimit.Text = FormatBytes(commit?.LimitBytes);
        if (memory is not { TotalBytes: > 0 })
        {
            RamPercent.Text = "—";
            RamUsage.Text = UiText.Get("Данные оперативной памяти недоступны");
            RamAvailable.Text = "";
            return;
        }

        ulong available = Math.Min(memory.AvailableBytes, memory.TotalBytes);
        ulong used = memory.TotalBytes - available;
        double fraction = (double)used / memory.TotalBytes;
        RamPercent.Text = $"{fraction * 100:0}%";
        RamUsage.Text = UiText.Format($"{FormatBytes(used)} из {FormatBytes(memory.TotalBytes)} занято");
        RamAvailable.Text = UiText.Format($"Доступно {FormatBytes(available)}");
    }

    private void SetPagefiles(IReadOnlyList<PagefileUsage>? files)
    {
        PagefileCurrent.Text = files is not null && files.All(file => file.CurrentMiB.HasValue)
            ? FormatBytes((ulong)files.Sum(file => (long)file.CurrentMiB.GetValueOrDefault()) * 1024 * 1024)
            : "—";
        PagefileDetail.Text = files switch
        {
            null => UiText.Get("Данные о файлах подкачки недоступны."),
            { Count: 0 } => UiText.Get("Windows не сообщила об активных файлах подкачки."),
            _ => ""
        };
        PagefileDetail.Visibility = VisibleWhen(files is null or { Count: 0 });
        PagefileItems.ItemsSource = files?.Select(file => new PagefileRow(file.Name,
            UiText.Format($"Занято {FormatMiB(file.CurrentMiB)}  ·  Выделено {FormatMiB(file.AllocatedMiB)}  ·  Пик {FormatMiB(file.PeakMiB)}"))).ToArray();
    }

    private bool HasCleanSelection => CleanTemp.IsChecked == true ||
        CleanThumbnails.IsChecked == true || CleanIcons.IsChecked == true || CleanDirectX.IsChecked == true ||
        CleanNvidia.IsChecked == true || CleanWerArchive.IsChecked == true || CleanWerQueue.IsChecked == true ||
        CleanFontCache.IsChecked == true;

    private void CleanCategoryChanged(object sender, RoutedEventArgs e)
    {
        cleanSelectionVersion++;
        ClearScan();
        bool hasSelection = HasCleanSelection;
        ScanButton.IsEnabled = CleanCategoryChoices.IsEnabled && hasSelection;
        SetStatus(CleanStatus, hasSelection
            ? UiText.Get("Запустите сканирование, чтобы увидеть список файлов.")
            : UiText.Get("Выберите хотя бы одну категорию для сканирования."));
    }

    private void SetCleanBusy(bool busy)
    {
        CleanCategoryChoices.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && HasCleanSelection;
        DeleteButton.IsEnabled = !busy && lastScan is { Candidates.Count: > 0 };
    }

    private async void ScanClick(object sender, RoutedEventArgs e)
    {
        if (closing || !ScanButton.IsEnabled)
            return;

        var selected = new List<(CleanupCategory Category, string Name)>(8);
        if (CleanTemp.IsChecked == true)
            selected.Add((CleanupCategory.Temp, (string)CleanTemp.Content));
        if (CleanThumbnails.IsChecked == true)
            selected.Add((CleanupCategory.ExplorerThumbnails, (string)CleanThumbnails.Content));
        if (CleanIcons.IsChecked == true)
            selected.Add((CleanupCategory.ExplorerIcons, (string)CleanIcons.Content));
        if (CleanDirectX.IsChecked == true)
            selected.Add((CleanupCategory.DirectXShaders, (string)CleanDirectX.Content));
        if (CleanNvidia.IsChecked == true)
            selected.Add((CleanupCategory.NvidiaShaders, (string)CleanNvidia.Content));
        if (CleanWerArchive.IsChecked == true)
            selected.Add((CleanupCategory.WerArchive, (string)CleanWerArchive.Content));
        if (CleanWerQueue.IsChecked == true)
            selected.Add((CleanupCategory.WerQueue, (string)CleanWerQueue.Content));
        if (CleanFontCache.IsChecked == true)
            selected.Add((CleanupCategory.FontCache, (string)CleanFontCache.Content));
        int selectionVersion = cleanSelectionVersion;

        ClearScan();
        SetCleanBusy(true);
        SetStatus(CleanStatus, UiText.Get("Сканируем выбранные категории…"));
        try
        {
            var scan = await RunInBackground(() => ScanCleanCategories(selected));
            if (selectionVersion != cleanSelectionVersion)
                return;

            lastScan = scan;
            SetStatus(CleanStatus, scan.Candidates.Count > 0
                ? UiText.Format($"К удалению: {scan.Candidates.Count:N0} · {DiskCleaner.FormatBytes(scan.TotalBytes)}")
                : UiText.Get("Сканирование завершено. Файлов к удалению нет."));
            CleanDetails.ItemsSource = scan.Categories;
            CleanItems.ItemsSource = scan.Files;
            CleanItems.Visibility = VisibleWhen(scan.Candidates.Count > 0);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            if (selectionVersion == cleanSelectionVersion)
                SetStatus(CleanStatus, ex.Message, error: true);
        }
        finally
        {
            SetCleanBusy(false);
        }
    }

    private static CleanPreview ScanCleanCategories(List<(CleanupCategory Category, string Name)> selected)
    {
        var categories = new CleanCategoryRow[selected.Count];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<TempFile>();
        var files = new List<CleanRow>();
        long totalBytes = 0;
        bool includesWerQueue = false;
        for (int i = 0; i < selected.Count; i++)
        {
            var (category, name) = selected[i];
            var report = DiskCleaner.ScanCategory(category);
            int categoryCount = 0;
            long categoryBytes = 0;
            foreach (var file in report.Candidates)
            {
                if (!paths.Add(file.Path))
                    continue;
                candidates.Add(file);
                files.Add(new CleanRow(file.Path, DiskCleaner.FormatBytes(file.Bytes)));
                categoryCount++;
                categoryBytes += file.Bytes;
            }
            totalBytes += categoryBytes;
            includesWerQueue |= category == CleanupCategory.WerQueue && report.Candidates.Count > 0;
            categories[i] = new CleanCategoryRow(
                UiText.Format($"{name} — к удалению: {categoryCount:N0} · {DiskCleaner.FormatBytes(categoryBytes)}"),
                UiText.Format($"Пути: {(report.Roots.Count > 0 ? string.Join(", ", report.Roots) : "нет доступных путей")}"),
                UiText.Format($"Пропущено: свежих {report.RecentFiles}, занятых {report.BusyFiles}, недоступных файлов {report.UnavailableFiles}, ") +
                UiText.Format($"недоступных папок {report.UnavailableFolders}, ссылок {report.SkippedLinks}."));
        }

        return new CleanPreview(candidates, files, categories, totalBytes, includesWerQueue);
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (closing || !DeleteButton.IsEnabled || lastScan is not { Candidates.Count: > 0 } scan ||
            !Confirm(UiText.Get("Удалить файлы из предпросмотра?"),
                UiText.Format($"Файлы из проверенного списка будут удалены без перемещения в корзину. Количество: {scan.Candidates.Count:N0}.") +
                (scan.IncludesWerQueue ? UiText.Get("\n\nВ списке есть файлы из очереди Windows. Неотправленные данные будут потеряны.") : ""),
                UiText.Get("Удалить файлы")) || closing || !ReferenceEquals(lastScan, scan))
            return;

        ClearScan();
        SetCleanBusy(true);
        SetStatus(CleanStatus, UiText.Get("Удаляем файлы из предпросмотра…"));
        try
        {
            var result = await RunInBackground(() => DiskCleaner.Clean(scan.Candidates));
            SetStatus(CleanStatus, UiText.Format($"Удалено: {result.DeletedFiles:N0} · {DiskCleaner.FormatBytes(result.DeletedBytes)}. ") +
                UiText.Format($"Занято: {result.BusyFiles}. Windows отказала в доступе: {result.DeniedFiles}. ") +
                UiText.Format($"Изменились или недоступны: {result.SkippedFiles}."));
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(CleanStatus, UiText.Format($"{ex.Message} Запустите повторное сканирование."), error: true);
        }
        finally
        {
            SetCleanBusy(false);
        }
    }

    private void ClearScan()
    {
        lastScan = null;
        DeleteButton.IsEnabled = false;
        CleanItems.ItemsSource = null;
        CleanItems.Visibility = Visibility.Collapsed;
        CleanDetails.ItemsSource = null;
    }

    private void OpenWindowsStorageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
            WindowsStorageStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(WindowsStorageStatus, UiText.Get("Не удалось открыть хранилище. Откройте «Параметры → Система → Память» вручную."), error: true);
            WindowsStorageStatus.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshTweaksAsync()
    {
        if (loadingTweaks)
            return;

        loadingTweaks = true;
        SetTweakControls(false);
        try
        {
            var (performance, diagnostics, recommendations, cloud, appPrivacy, appearance, explorer, taskbar) = await RunInBackground(() => (
                TweaksEngine.PerformanceCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.DiagnosticPrivacyCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.RecommendationPrivacyCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.CloudPrivacyCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.AppPrivacyCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.PersonalizationCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.ExplorerCatalog.Select(ReadTweak).ToArray(),
                TweaksEngine.TaskbarCatalog.Select(ReadTweak).ToArray()));
            TweakItems.ItemsSource = performance;
            DiagnosticPrivacyItems.ItemsSource = diagnostics;
            RecommendationPrivacyItems.ItemsSource = recommendations;
            CloudPrivacyItems.ItemsSource = cloud;
            AppPrivacyItems.ItemsSource = appPrivacy;
            PersonalizationItems.ItemsSource = appearance;
            ExplorerPersonalizationItems.ItemsSource = explorer;
            TaskbarPersonalizationItems.ItemsSource = taskbar;
        }
        finally
        {
            SetTweakControls(true);
            loadingTweaks = false;
        }
    }

    private void SetTweakControls(bool enabled)
    {
        TweakItems.IsEnabled = enabled;
        PrivacyPolicyTabs.IsEnabled = enabled;
        PersonalizationTabs.IsEnabled = enabled;
    }

    private static TweakRow ReadTweak(WindowsTweak tweak)
    {
        try
        {
            string? unsupported = TweaksEngine.SupportIssue(tweak);
            var (value, hasBackup) = TweaksEngine.ReadSetting(tweak);
            string state = unsupported ?? TweaksEngine.DescribeState(tweak, value);
            return new TweakRow(tweak, state, unsupported is null && value != tweak.AppliedValue, hasBackup);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            return new TweakRow(tweak, UiText.Format($"Состояние недоступно: {ex.Message}"), false, false);
        }
    }

    private async void ApplyTweakClick(object sender, RoutedEventArgs e) => await ChangeTweakAsync(sender, restore: false);
    private async void RestoreTweakClick(object sender, RoutedEventArgs e) => await ChangeTweakAsync(sender, restore: true);

    private async Task ChangeTweakAsync(object sender, bool restore)
    {
        if (loadingTweaks || sender is not Button { Tag: TweakRow row })
            return;

        if (!restore && !Confirm(row.Name, $"{row.Description}\n\n{row.Risk}", UiText.Get("Применить")))
            return;

        var status = PrivacyPage.IsVisible ? PrivacyStatus : PersonalizationPage.IsVisible ? PersonalizationStatus : TweaksStatus;
        loadingTweaks = true;
        SetTweakControls(false);
        status.Visibility = Visibility.Visible;
        SetStatus(status, UiText.Get("Обновляем политику Windows…"));
        try
        {
            bool changed = await RunInBackground(() => TweaksEngine.Change(row.Tweak, restore));
            SetStatus(status, (restore, changed) switch
            {
                (true, true) => UiText.Get("Исходное значение политики восстановлено."),
                (true, false) => UiText.Get("Сохранённое значение отсутствует."),
                (false, true) => UiText.Format($"Политика записана. {row.Description}"),
                _ => UiText.Get("Значение твика уже задано.")
            });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(status, ex.Message, error: true);
        }
        finally
        {
            loadingTweaks = false;
            if (!closing)
                await RefreshTweaksAsync();
        }
    }

    private async void RefreshProfileApplicationsClick(object sender, RoutedEventArgs e) =>
        await RefreshProfileApplicationsAsync();

    private async Task RefreshProfileApplicationsAsync()
    {
        if (loadingProfileApplications || profileStop is not null || closing)
            return;

        var selected = profileApplications.Where(app => app.IsSelected).Select(app => app.Process).ToHashSet();
        loadingProfileApplications = true;
        UpdateProfileControls();
        ProfileApplicationStatus.Visibility = Visibility.Visible;
        SetStatus(ProfileApplicationStatus, UiText.Get("Получаем список процессов…"));
        try
        {
            var applications = await RunInBackground(ReadProfileApplications);
            if (closing)
                return;

            foreach (var app in applications)
                app.IsSelected = selected.Contains(app.Process);
            profileApplications = applications;
            profileApplicationView = CollectionViewSource.GetDefaultView(applications);
            profileApplicationView.Filter = item => item is ProfileApplicationRow app &&
                app.Label.Contains(ProfileProcessFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
            ProfileApplication.ItemsSource = profileApplicationView;
            ProfileApplicationStatus.Visibility = VisibleWhen(applications.Length == 0);
            if (applications.Length == 0)
                SetStatus(ProfileApplicationStatus, UiText.Get("Откройте нужное приложение и нажмите «Обновить»."));
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(ProfileApplicationStatus, UiText.Get("Не удалось получить список приложений. Нажмите «Обновить»."), error: true);
        }
        finally
        {
            loadingProfileApplications = false;
            UpdateProfileControls();
        }
    }

    private static ProfileApplicationRow[] ReadProfileApplications()
    {
        var applications = new List<ProfileApplicationRow>();
        using var current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id <= 4 || process.Id == Environment.ProcessId || process.SessionId != sessionId)
                        continue;

                    string name = process.ProcessName;
                    string title = process.MainWindowTitle.Trim();
                    var identity = new ContextProfileManager.SelectedProcess(process.Id, process.StartTime.ToUniversalTime());
                    string label = $"{name}.exe · PID {identity.Id}";
                    if (title.Length > 0)
                        label += $" — {title}";
                    applications.Add(new ProfileApplicationRow(name, identity, label));
                }
                catch (Exception ex) when (IsSystemError(ex))
                {
                    // A process can exit or become inaccessible while the list is being read.
                }
            }
        }

        return applications.OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void ProfileApplicationChanged(object sender, RoutedEventArgs e) => UpdateProfileControls();
    private void ProfileProcessFilterChanged(object sender, TextChangedEventArgs e) => profileApplicationView?.Refresh();

    private async void StartProfileClick(object sender, RoutedEventArgs e)
    {
        var apps = profileApplications.Where(app => app.IsSelected).ToArray();
        if (loadingProfileApplications || profileStop is not null || closing || apps.Length == 0)
            return;

        var priority = AbovePriority.IsChecked == true ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.BelowNormal;
        string name = string.Join(", ", apps.Select(app => app.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        var profile = new ContextProfileManager(apps.Select(app => app.Process), priority, ProfileGamePower.IsChecked == true);
        using var stop = new CancellationTokenSource();
        profileStop = stop;
        signalTargets = apps;
        signalPriority = priority;
        signalChanges.Clear();
        var trial = BeginFocusTrial(apps, name, priority);
        RenderFocusSignal(updateTargets: true);
        bool followInBackground = FocusBackground.IsChecked == true;
        UpdateProfileControls();
        profileEvents.Clear();
        SetStatus(ProfileStatus, UiText.Get("Запускаем наблюдение…"));

        void Report(string message) => Dispatcher.InvokeAsync(() =>
        {
            profileEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            if (profileEvents.Count > 8)
                profileEvents.RemoveAt(8);
        });

        bool processExited = false;
        try
        {
            profileTask = Task.Run(() => profile.Watch(stop.Token, () =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(profileStop, stop) && !stop.IsCancellationRequested && profileTask is { IsCompleted: false })
                        SetStatus(ProfileStatus, UiText.Format($"Процессов под наблюдением: {apps.Length}."));
                });
            }, Report, (pid, startedUtc) => Dispatcher.InvokeAsync(() =>
            {
                if (!closing && ReferenceEquals(profileStop, stop) && !stop.IsCancellationRequested &&
                    profileTask is { IsCompleted: false })
                    StartMemorySession(name);
            }), trial is null ? null : () => trial.ApplyProfile, followInBackground, requireAllTargets: trial is not null,
                priorityChanged: change => Dispatcher.InvokeAsync(() =>
                {
                    if (!closing && ReferenceEquals(signalTargets, apps))
                    {
                        signalChanges[change.ProcessId] = change;
                        RenderFocusSignal(updateTargets: true);
                    }
                })));
            var result = await profileTask;
            processExited = result.ProcessExited;
            string status = !result.Restored ? UiText.Get("Не удалось полностью восстановить настройки профиля. См. журнал ниже.") :
                processExited ? UiText.Get(trial is null
                    ? "Все выбранные процессы завершились. Профиль остановлен."
                    : "Состав группы изменился. Замер остановлен.") : UiText.Get("Профиль остановлен.");
            SetStatus(ProfileStatus, status, error: !result.Restored);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(ProfileStatus, ex.Message, error: true);
        }
        finally
        {
            EndFocusTrial();
            FinishMemorySession();
            profileStop = null;
            profileTask = null;
            UpdateProfileControls();
            RenderFocusSignal(updateTargets: true);
        }

        if (processExited && !closing)
            await RefreshProfileApplicationsAsync();
    }

    private void StopProfileClick(object sender, RoutedEventArgs e)
    {
        StopProfileButton.IsEnabled = false;
        SetStatus(ProfileStatus, UiText.Get("Останавливаем профиль и восстанавливаем настройки…"));
        profileStop?.Cancel();
        RenderFocusSignal();
    }

    private void UpdateProfileControls()
    {
        bool running = profileStop is not null;
        bool canChoose = !running && !loadingProfileApplications;
        int chosen = profileApplications.Count(app => app.IsSelected);
        StartProfileButton.IsEnabled = canChoose && chosen > 0;
        StartProfileButton.Visibility = VisibleWhen(!running);
        StopProfileButton.Visibility = VisibleWhen(running);
        ProfileSelectionText.Text = UiText.Format($"Выбрано процессов: {chosen}");
        ProfileApplication.IsEnabled = canChoose && ProfileApplication.HasItems;
        ProfileProcessFilter.IsEnabled = canChoose;
        RefreshProfileApplicationsButton.IsEnabled = canChoose;
        PriorityChoices.IsEnabled = !running;
        CompareMode.IsEnabled = !running;
        FocusBackground.IsEnabled = !running;
        FocusMonitorToggle.IsEnabled = CompareMode.IsChecked == true;
        FocusHistoryPicker.IsEnabled = !running;
        StartProfileButton.SetResourceReference(ContentControl.ContentProperty,
            CompareMode.IsChecked == true ? "UI:Начать сравнение" : "UI:Запустить профиль");
        ProfileGamePower.IsEnabled = !running && gamePowerPlanAvailable;
        StopProfileButton.IsEnabled = profileStop is { IsCancellationRequested: false };
    }


    private Task<T> RunInBackground<T>(Func<T> work) => TrackOperation(Task.Run(work));

    private async Task<T> TrackOperation<T>(Task<T> task)
    {
        pendingOperations.Add(task);
        try { return await task; }
        finally { pendingOperations.Remove(task); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;

        e.Cancel = true;
        if (closing)
            return;

        if (preferences.CloseToTray)
        {
            RememberWorkspace();
            settingsWindow?.Close();
            Hide();
        }
        else
            _ = ExitAsync();
    }

    internal void ShowFromTray()
    {
        if (closing || allowClose)
            return;

        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    internal async Task ExitAsync()
    {
        if (!closing && await PrepareToExitAsync())
            CloseForExit();
    }

    private async Task<bool> PrepareToExitAsync()
    {
        closing = true;
        overviewTimer.Stop();
        memoryTimer.Stop();
        focusComparisonTimer.Stop();
        WindowSurface.IsEnabled = false;
        profileStop?.Cancel();
        var activeProfile = profileTask;
        try { await Task.WhenAll(pendingOperations.ToArray()); }
        catch (Exception ex) when (IsSystemError(ex))
        {
            // The operation reports its own failure on the current page.
        }

        bool restored = true;
        if (activeProfile is not null)
        {
            try { restored = (await activeProfile).Restored; }
            catch (Exception ex) when (IsSystemError(ex))
            {
                restored = false;
            }
        }

        if (restored)
            return true;

        CancelExit();
        ProfileNav.IsChecked = true;
        return false;
    }

    private void CancelExit()
    {
        WindowSurface.IsEnabled = true;
        closing = false;
        ShowFromTray();
        UpdateOverviewPolling();
        UpdateMemoryPolling();
    }

    private void CloseForExit()
    {
        RememberWorkspace();
        allowClose = true;
        Close();
    }

    internal void PrepareForSessionEnd()
    {
        allowClose = true;
        profileStop?.Cancel();
        try { profileTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) { Debug.WriteLine(ex); }
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeWebInterface();
        CloseFocusLiveWindow();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= WindowsPreferenceChanged;
        overviewTimer.Stop();
        memoryTimer.Stop();
        focusComparisonTimer.Stop();
        updateTimer.Stop();
        updateLifetime.Cancel();
        updateLifetime.Dispose();
        trayIcon.Dispose();
        trayMenu.Dispose();
        appIcon?.Dispose();
        base.OnClosed(e);
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateOverviewPolling();
        UpdateMemoryPolling();
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M 0,3 H 7 V 10 H 0 Z M 3,3 V 0 H 10 V 7 H 7" : "M 0,0 H 9 V 9 H 0 Z");
        UpdateMaximizeLabel();
    }

    private void UpdateMaximizeLabel()
    {
        bool maximized = WindowState == WindowState.Maximized;
        string action = maximized ? UiText.Get("Восстановить размер") : UiText.Get("Развернуть");
        MaximizeButton.ToolTip = action;
        AutomationProperties.SetName(MaximizeButton, action);
    }

    private void SetStatus(TextBlock element, string message, bool error = false)
    {
        element.Text = UiText.Get(message);
        element.SetResourceReference(TextBlock.ForegroundProperty, error ? "Warning" : "Muted");
    }

    private static bool IsSystemError(Exception ex) => ex is ArgumentException or InvalidOperationException
        or IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or Win32Exception or ManagementException or COMException;

    private static string FormatBytes(ulong? bytes) => bytes is { } value ? DiskCleaner.FormatBytes((long)value) : "—";
    private static string FormatMiB(uint? mib) => FormatBytes(mib is { } value ? (ulong)value * 1024 * 1024 : null);
}
