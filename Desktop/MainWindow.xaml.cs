using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;
using Tray = System.Windows.Forms;

namespace CoreShift.Desktop;

public partial class MainWindow : Window
{
    private sealed record Overview(
        PhysicalMemory? Memory, MemoryCommit? Commit, IReadOnlyList<PagefileUsage>? Pagefiles,
        string Advice, bool HasErrors);

    private sealed record PagefileRow(string Name, string Usage);
    private sealed record PcInfoRow(string Label, string Value);
    private sealed record CleanRow(string Path, string Size);
    private sealed record CleanCategoryRow(string Heading, string Roots, string Skipped);
    private sealed record CleanPreview(List<TempFile> Candidates, List<CleanRow> Files, CleanCategoryRow[] Categories,
        long TotalBytes, bool IncludesWerQueue);
    private sealed record ProfileApplicationRow(string ProcessName, string Label);
    private sealed record TweakRow(WindowsTweak Tweak, string State, bool CanApply, bool CanRestore)
    {
        public string Name => Tweak.Name;
        public string Description => Tweak.Description;
        public string Risk => Tweak.Risk;
    }

    private readonly bool isElevated;
    private readonly Tray.NotifyIcon trayIcon;
    private readonly Tray.ContextMenuStrip trayMenu = new();
    private readonly System.Drawing.Icon? appIcon;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly CancellationTokenSource updateLifetime = new();
    private readonly HashSet<Task> pendingOperations = [];
    private readonly ObservableCollection<string> profileEvents = [];
    private CleanPreview? lastScan;
    private int cleanSelectionVersion;
    private CancellationTokenSource? profileStop;
    private Task<bool>? profileTask;
    private bool loadingPcInfo;
    private bool loadingTweaks;
    private bool loadingProfileApplications;
    private bool closing;
    private bool allowClose;
    private bool initialized;
    private bool checkingUpdate;
    private bool installingUpdate;
    private AppRelease? availableUpdate;

    public MainWindow()
    {
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
        using var identity = WindowsIdentity.GetCurrent();
        isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        AccessMode.Text = isElevated ? "Администратор" : "Обычный пользователь";
        ProfileEvents.ItemsSource = profileEvents;

        if (Environment.ProcessPath is { } exePath)
            appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        trayMenu.Items.Add("Открыть CoreShift", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
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
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
            return;

        initialized = true;
        if (AppUpdater.IsInstalledCopy())
        {
            updateTimer.Start();
            await Task.WhenAll(RefreshOverviewAsync(), CheckForUpdatesAsync());
            return;
        }

        await RefreshOverviewAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (checkingUpdate || installingUpdate || closing)
            return;

        checkingUpdate = true;
        try
        {
            var update = await AppUpdater.FetchLatestAsync(updateLifetime.Token);
            if (closing || installingUpdate || update?.Version == availableUpdate?.Version)
                return;

            availableUpdate = update;
            UpdateBanner.Visibility = VisibleWhen(update is not null);
            if (update is not null)
                SetStatus(UpdateStatus, $"Версия {update.Version}");
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            Debug.WriteLine(ex);
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
        SetStatus(UpdateStatus, "Готовим окно обновления…");
        try
        {
            if (!await PrepareToExitAsync())
            {
                SetStatus(UpdateStatus, "Не удалось восстановить приоритет процесса. Повторите установку после проверки профиля.", error: true);
                return;
            }

            await AppUpdater.StartWorkerAsync(update.Version);
            CloseForExit();
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            Debug.WriteLine(ex);
            CancelExit();
            SetStatus(UpdateStatus, "Не удалось открыть окно обновления. Попробуйте ещё раз.", error: true);
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
        ProfilePage.Visibility = VisibleWhen(ReferenceEquals(sender, ProfileNav));
        PageScroll.ScrollToTop();

        if (ReferenceEquals(sender, TweaksNav) && !loadingTweaks)
            await RefreshTweaksAsync();
        else if (ReferenceEquals(sender, PcInfoNav) && !loadingPcInfo && PcInfoItems.ItemsSource is null)
            await LoadPcInfoAsync();
        else if (ReferenceEquals(sender, ProfileNav))
            await RefreshProfileApplicationsAsync();
    }

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    private async Task LoadPcInfoAsync()
    {
        loadingPcInfo = true;
        try
        {
            PcInfoItems.ItemsSource = await RunInBackground(ReadPcInfo);
            PcInfoStatus.Visibility = Visibility.Collapsed;
        }
        finally
        {
            loadingPcInfo = false;
        }
    }

    private static PcInfoRow[] ReadPcInfo()
    {
        const string unavailable = "Недоступно";
        string windows = ReadWmiValues("SELECT Caption FROM Win32_OperatingSystem", item => item["Caption"] as string)
            ?? "Windows";

        return
        [
            new("Имя компьютера", Environment.MachineName),
            new("Операционная система", $"{windows} · сборка {Environment.OSVersion.Version.Build} · {RuntimeInformation.OSArchitecture}"),
            new("Процессоры", ReadWmiValues("SELECT Name FROM Win32_Processor", item => item["Name"] as string) ?? unavailable),
            new("Оперативная память", ReadWmiValues("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem",
                item => item["TotalPhysicalMemory"] is ulong bytes && bytes > 0 ? FormatBytes(bytes) : null) ?? unavailable),
            new("Видеоадаптеры", ReadWmiValues("SELECT Name FROM Win32_VideoController", item => item["Name"] as string) ?? unavailable),
            new("Накопители", ReadWmiValues("SELECT Model, Size FROM Win32_DiskDrive", item =>
            {
                string? model = (item["Model"] as string)?.Trim();
                return item["Size"] is ulong bytes && bytes > 0
                    ? $"{(string.IsNullOrEmpty(model) ? "Накопитель" : model)} · {FormatBytes(bytes)}"
                    : model;
            }) ?? unavailable)
        ];
    }

    private static string? ReadWmiValues(string query, Func<ManagementBaseObject, string?> format)
    {
        var values = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                    if (format(item)?.Trim() is { Length: > 0 } value)
                        values.Add(value);
            }
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            return null;
        }

        return values.Count == 0 ? null : string.Join(Environment.NewLine, values);
    }

    private void OpenCleanClick(object sender, RoutedEventArgs e) => CleanNav.IsChecked = true;
    private void OpenProfileClick(object sender, RoutedEventArgs e) => ProfileNav.IsChecked = true;
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshOverviewAsync();

    private async Task RefreshOverviewAsync()
    {
        RefreshButton.IsEnabled = false;
        SetStatus(RefreshStatus, "Получаем данные Windows…");
        try
        {
            var overview = await RunInBackground(ReadOverview);
            SetMemory(overview.Memory, overview.Commit);
            SetPagefiles(overview.Pagefiles);
            MemoryAdvice.Text = overview.Advice;
            SetStatus(RefreshStatus,
                overview.HasErrors ? "Часть данных Windows недоступна" : $"Обновлено в {DateTime.Now:HH:mm:ss}",
                overview.HasErrors);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static Overview ReadOverview()
    {
        bool hasErrors = false;
        T? Read<T>(Func<T> read) where T : class
        {
            try { return read(); }
            catch (Exception ex) when (IsSystemError(ex))
            {
                hasErrors = true;
                return null;
            }
        }

        var snapshot = Read(MemoryAndPagefileManager.ReadMemorySnapshot);
        var pagefiles = Read(MemoryAndPagefileManager.ReadPagefiles);
        var dump = Read(MemoryAndPagefileManager.ReadCrashDumpSettings);
        string advice = MemoryAndPagefileManager.ExplainManualSizing(snapshot?.Physical.TotalBytes, snapshot?.Commit, dump);
        return new Overview(snapshot?.Physical, snapshot?.Commit, pagefiles, advice, hasErrors);
    }

    private void SetMemory(PhysicalMemory? memory, MemoryCommit? commit)
    {
        CommitPeak.Text = FormatBytes(commit?.PeakBytes);
        CommitLimit.Text = FormatBytes(commit?.LimitBytes);
        if (memory is not { TotalBytes: > 0 })
        {
            RamPercent.Text = "—";
            RamUsage.Text = "Данные оперативной памяти недоступны";
            RamAvailable.Text = "";
            RamGauge.Data = null;
            return;
        }

        ulong available = Math.Min(memory.AvailableBytes, memory.TotalBytes);
        ulong used = memory.TotalBytes - available;
        double fraction = (double)used / memory.TotalBytes;
        RamPercent.Text = $"{fraction * 100:0}%";
        RamUsage.Text = $"{FormatBytes(used)} из {FormatBytes(memory.TotalBytes)} занято";
        RamAvailable.Text = $"Доступно {FormatBytes(available)}";
        RamGauge.Data = GaugeArc(fraction);
    }

    private static Geometry? GaugeArc(double fraction)
    {
        const double center = 85;
        const double radius = 70;
        if (fraction <= 0)
            return null;
        if (fraction >= 1)
            return new EllipseGeometry(new Point(center, center), radius, radius);

        double angle = fraction * 2 * Math.PI;
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var arc = new ArcSegment(end, new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true);
        return new PathGeometry([new PathFigure(new Point(center, center - radius), [arc], false)]);
    }

    private void SetPagefiles(IReadOnlyList<PagefileUsage>? files)
    {
        PagefileCurrent.Text = files is not null && files.All(file => file.CurrentMiB.HasValue)
            ? FormatBytes((ulong)files.Sum(file => (long)file.CurrentMiB.GetValueOrDefault()) * 1024 * 1024)
            : "—";
        PagefileDetail.Text = files switch
        {
            null => "Данные о файлах подкачки недоступны.",
            { Count: 0 } => "Windows не сообщила об активных файлах подкачки.",
            _ => ""
        };
        PagefileDetail.Visibility = VisibleWhen(files is null or { Count: 0 });
        PagefileItems.ItemsSource = files?.Select(file => new PagefileRow(file.Name,
            $"Занято {FormatMiB(file.CurrentMiB)}  ·  Выделено {FormatMiB(file.AllocatedMiB)}  ·  Пик {FormatMiB(file.PeakMiB)}")).ToArray();
    }

    private async void TrimClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TrimPid.Text, out int pid) || pid <= 0)
        {
            SetStatus(TrimStatus, "Введите положительный числовой PID.", error: true);
            TrimPid.Focus();
            return;
        }

        TrimButton.IsEnabled = false;
        TrimPid.IsEnabled = false;
        SetStatus(TrimStatus, $"Сбрасываем рабочий набор PID {pid}…");
        try
        {
            await RunInBackground(() =>
            {
                MemoryOptimizer.TrimWorkingSet(pid);
                return pid;
            });
            SetStatus(TrimStatus, $"Рабочий набор PID {pid} сброшен.");
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(TrimStatus, ex.Message, error: true);
        }
        finally
        {
            TrimPid.IsEnabled = true;
            TrimButton.IsEnabled = true;
        }
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
            ? "Запустите сканирование, чтобы увидеть список файлов."
            : "Выберите хотя бы одну категорию для сканирования.");
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
        SetStatus(CleanStatus, "Сканируем выбранные категории…");
        try
        {
            var scan = await RunInBackground(() => ScanCleanCategories(selected));
            if (selectionVersion != cleanSelectionVersion)
                return;

            lastScan = scan;
            SetStatus(CleanStatus, scan.Candidates.Count > 0
                ? $"К удалению: {scan.Candidates.Count:N0} · {DiskCleaner.FormatBytes(scan.TotalBytes)}"
                : "Сканирование завершено. Файлов к удалению нет.");
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
                $"{name} — к удалению: {categoryCount:N0} · {DiskCleaner.FormatBytes(categoryBytes)}",
                $"Пути: {(report.Roots.Count > 0 ? string.Join(", ", report.Roots) : "нет доступных путей")}",
                $"Пропущено: свежих {report.RecentFiles}, занятых {report.BusyFiles}, недоступных файлов {report.UnavailableFiles}, " +
                $"недоступных папок {report.UnavailableFolders}, ссылок {report.SkippedLinks}.");
        }

        return new CleanPreview(candidates, files, categories, totalBytes, includesWerQueue);
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (closing || !DeleteButton.IsEnabled || lastScan is not { Candidates.Count: > 0 } scan ||
            !Confirm("Удалить файлы из предпросмотра?",
                $"Файлы из проверенного списка будут удалены без перемещения в корзину. Количество: {scan.Candidates.Count:N0}." +
                (scan.IncludesWerQueue ? "\n\nВ списке есть файлы из очереди Windows. Неотправленные данные будут потеряны." : ""),
                "Удалить файлы") || closing || !ReferenceEquals(lastScan, scan))
            return;

        ClearScan();
        SetCleanBusy(true);
        SetStatus(CleanStatus, "Удаляем файлы из предпросмотра…");
        try
        {
            var result = await RunInBackground(() => DiskCleaner.Clean(scan.Candidates));
            SetStatus(CleanStatus, $"Удалено: {result.DeletedFiles:N0} · {DiskCleaner.FormatBytes(result.DeletedBytes)}. " +
                $"Занято: {result.BusyFiles}. Windows отказала в доступе: {result.DeniedFiles}. " +
                $"Изменились или недоступны: {result.SkippedFiles}.");
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(CleanStatus, $"{ex.Message} Запустите повторное сканирование.", error: true);
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
            SetStatus(WindowsStorageStatus, "Не удалось открыть хранилище. Откройте «Параметры → Система → Память» вручную.", error: true);
            WindowsStorageStatus.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshTweaksAsync()
    {
        loadingTweaks = true;
        TweakItems.IsEnabled = false;
        try
        {
            TweakItems.ItemsSource = await RunInBackground(() => TweaksEngine.Catalog.Select(ReadTweak).ToArray());
        }
        finally
        {
            TweakItems.IsEnabled = true;
            loadingTweaks = false;
        }
    }

    private TweakRow ReadTweak(WindowsTweak tweak)
    {
        bool canWrite = isElevated || tweak.Hive == RegistryHive.CurrentUser;
        try
        {
            string? unsupported = TweaksEngine.SupportIssue(tweak);
            string state = unsupported ?? TweaksEngine.DescribeState(tweak);
            if (!canWrite)
                state = $"{state.TrimEnd('.')}. Для изменения запустите CoreShift от имени администратора.";
            return new TweakRow(tweak, state, canWrite && unsupported is null, canWrite);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            return new TweakRow(tweak, $"Состояние недоступно: {ex.Message}", false, canWrite);
        }
    }

    private async void ApplyTweakClick(object sender, RoutedEventArgs e) => await ChangeTweakAsync(sender, restore: false);
    private async void RestoreTweakClick(object sender, RoutedEventArgs e) => await ChangeTweakAsync(sender, restore: true);

    private async Task ChangeTweakAsync(object sender, bool restore)
    {
        if (loadingTweaks || sender is not Button { Tag: TweakRow row })
            return;

        if (!restore && !Confirm(row.Name, $"{row.Description}\n\n{row.Risk}", "Применить"))
            return;

        loadingTweaks = true;
        TweakItems.IsEnabled = false;
        TweaksStatus.Visibility = Visibility.Visible;
        SetStatus(TweaksStatus, "Обновляем политику Windows…");
        try
        {
            bool changed = await RunInBackground(() => TweaksEngine.Change(row.Tweak, restore));
            SetStatus(TweaksStatus, (restore, changed) switch
            {
                (true, true) => "Исходное значение политики восстановлено.",
                (true, false) => "Сохранённое значение отсутствует.",
                (false, true) => $"Политика записана. {row.Description}",
                _ => "Значение твика уже задано."
            });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(TweaksStatus, ex.Message, error: true);
        }

        if (!closing)
            await RefreshTweaksAsync();
    }

    private async void RefreshProfileApplicationsClick(object sender, RoutedEventArgs e) =>
        await RefreshProfileApplicationsAsync();

    private async Task RefreshProfileApplicationsAsync()
    {
        if (loadingProfileApplications || profileStop is not null)
            return;

        var selected = ProfileApplication.SelectedItem as ProfileApplicationRow;
        loadingProfileApplications = true;
        UpdateProfileControls();
        ProfileApplicationStatus.Visibility = Visibility.Visible;
        SetStatus(ProfileApplicationStatus, "Получаем список открытых приложений…");
        try
        {
            var applications = await RunInBackground(ReadProfileApplications);
            ProfileApplication.ItemsSource = applications;
            ProfileApplication.SelectedItem = applications.FirstOrDefault(app =>
                string.Equals(app.ProcessName, selected?.ProcessName, StringComparison.OrdinalIgnoreCase));
            ProfileApplicationStatus.Visibility = VisibleWhen(applications.Length == 0);
            if (applications.Length == 0)
                SetStatus(ProfileApplicationStatus, "Откройте нужное приложение и нажмите «Обновить».");
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(ProfileApplicationStatus, "Не удалось получить список приложений. Нажмите «Обновить».", error: true);
        }
        finally
        {
            loadingProfileApplications = false;
            UpdateProfileControls();
        }
    }

    private static ProfileApplicationRow[] ReadProfileApplications()
    {
        var applications = new Dictionary<string, ProfileApplicationRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id <= 4 || process.Id == Environment.ProcessId || process.MainWindowHandle == 0)
                        continue;

                    string name = process.ProcessName;
                    string title = process.MainWindowTitle.Trim();
                    string label = title.Length > 0 ? $"{name}.exe — {title}" : $"{name}.exe";
                    applications.TryAdd(name, new ProfileApplicationRow(name, label));
                }
                catch (Exception ex) when (IsSystemError(ex))
                {
                    // A process can exit or become inaccessible while the list is being read.
                }
            }
        }

        return applications.Values.OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void ProfileApplicationChanged(object sender, SelectionChangedEventArgs e) => UpdateProfileControls();

    private async void StartProfileClick(object sender, RoutedEventArgs e)
    {
        if (loadingProfileApplications || profileStop is not null || ProfileApplication.SelectedItem is not ProfileApplicationRow app)
            return;

        var priority = AbovePriority.IsChecked == true ? ProcessPriorityClass.AboveNormal
            : NormalPriority.IsChecked == true ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        string name = app.ProcessName;
        var profile = new ContextProfileManager(name, priority);
        using var stop = new CancellationTokenSource();
        profileStop = stop;
        UpdateProfileControls();
        profileEvents.Clear();
        SetStatus(ProfileStatus, "Запускаем наблюдение…");

        void Report(string message) => Dispatcher.InvokeAsync(() =>
        {
            profileEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            if (profileEvents.Count > 8)
                profileEvents.RemoveAt(8);
        });

        try
        {
            profileTask = Task.Run(() => profile.Watch(stop.Token, () =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!stop.IsCancellationRequested)
                        SetStatus(ProfileStatus, $"Наблюдение за {name}.exe активно.");
                });
            }, Report));
            bool restored = await profileTask;
            SetStatus(ProfileStatus, restored ? "Профиль остановлен." : "Не удалось восстановить исходный приоритет.",
                error: !restored);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(ProfileStatus, ex.Message, error: true);
        }
        finally
        {
            profileStop = null;
            profileTask = null;
            UpdateProfileControls();
        }
    }

    private void StopProfileClick(object sender, RoutedEventArgs e)
    {
        StopProfileButton.IsEnabled = false;
        SetStatus(ProfileStatus, "Останавливаем профиль и восстанавливаем приоритет…");
        profileStop?.Cancel();
    }

    private void UpdateProfileControls()
    {
        bool running = profileStop is not null;
        bool canChoose = !running && !loadingProfileApplications;
        StartProfileButton.IsEnabled = canChoose && ProfileApplication.SelectedItem is ProfileApplicationRow;
        ProfileApplication.IsEnabled = canChoose && ProfileApplication.HasItems;
        RefreshProfileApplicationsButton.IsEnabled = canChoose;
        PriorityChoices.IsEnabled = !running;
        StopProfileButton.IsEnabled = profileStop is { IsCancellationRequested: false };
    }

    private bool Confirm(string title, string message, string action) =>
        new ConfirmDialog(title, message, action) { Owner = this }.ShowDialog() == true;

    private async Task<T> RunInBackground<T>(Func<T> work)
    {
        var task = Task.Run(work);
        pendingOperations.Add(task);
        try { return await task; }
        finally { pendingOperations.Remove(task); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;

        e.Cancel = true;
        if (!closing)
            Hide();
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

    private async Task ExitAsync()
    {
        if (!closing && await PrepareToExitAsync())
            CloseForExit();
    }

    private async Task<bool> PrepareToExitAsync()
    {
        closing = true;
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
            try { restored = await activeProfile; }
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
    }

    private void CloseForExit()
    {
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
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M 0,3 H 7 V 10 H 0 Z M 3,3 V 0 H 10 V 7 H 7" : "M 0,0 H 9 V 9 H 0 Z");
        string action = maximized ? "Восстановить размер" : "Развернуть";
        MaximizeButton.ToolTip = action;
        AutomationProperties.SetName(MaximizeButton, action);
    }

    private void SetStatus(TextBlock element, string message, bool error = false)
    {
        element.Text = message;
        element.Foreground = (Brush)FindResource(error ? "Warning" : "Muted");
    }

    private static bool IsSystemError(Exception ex) => ex is ArgumentException or InvalidOperationException
        or IOException or UnauthorizedAccessException or SecurityException or Win32Exception or ManagementException or COMException;

    private static string FormatBytes(ulong? bytes) => bytes is { } value ? DiskCleaner.FormatBytes((long)value) : "—";
    private static string FormatMiB(uint? mib) => FormatBytes(mib is { } value ? (ulong)value * 1024 * 1024 : null);
}
