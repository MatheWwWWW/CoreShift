using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private readonly record struct ProcessMemoryKey(int Pid, DateTime StartedUtc);
    private sealed record ProcessMemory(ProcessMemoryKey Key, string Name, long Bytes, bool CanTrim);
    private sealed record ProcessMemorySample(DateTimeOffset At, Dictionary<ProcessMemoryKey, ProcessMemory> Processes);
    private sealed record MemoryPoint(DateTimeOffset At, MemorySnapshot Snapshot);
    private sealed record MemoryRead(MemoryPoint? System, ProcessMemorySample? Processes);
    private sealed record MemoryPagefileRow(string Name, uint? CurrentMiB, uint? AllocatedMiB, uint? PeakMiB, ulong? FreeBytes)
    {
        public string Allocation => $"{FormatMiB(CurrentMiB)} / {FormatMiB(AllocatedMiB)}";
        public string Peak => FormatMiB(PeakMiB);
        public string FreeSpace => FormatBytes(FreeBytes);
        public bool HasUsage => CurrentMiB.HasValue && AllocatedMiB is > 0;
        public double UsagePercent => HasUsage
            ? Math.Min(CurrentMiB.GetValueOrDefault() * 100d / AllocatedMiB.GetValueOrDefault(), 100)
            : 0;
    }

    private sealed record PagefileDiagnostics(bool? Automatic, MemoryPagefileRow[]? Files, CrashDumpSettings? Dump);

    private sealed class ProcessMemoryRow : INotifyPropertyChanged
    {
        public ProcessMemoryRow(ProcessMemory process) => Data = process;

        public ProcessMemory Data { get; private set; }
        public string Name => Data.Name;
        public int Pid => Data.Key.Pid;
        public long WorkingSetBytes => Data.Bytes;
        public long? GrowthBytes { get; private set; }
        public string WorkingSet => FormatBytes((ulong)WorkingSetBytes);
        public string Growth => GrowthBytes is { } change ? FormatGrowth(change) : "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(ProcessMemory process, long? growth)
        {
            if (process == Data && growth == GrowthBytes)
                return;

            Data = process;
            GrowthBytes = growth;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    private sealed class MemorySession(string name)
    {
        public string Name { get; } = name;
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? Ended { get; set; }
        public MemoryPoint? Minimum { get; set; }
        public ProcessMemorySample? Baseline { get; set; }
        public string[] Growth { get; set; } = [];
    }

    private static readonly TimeSpan memoryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan memoryHistoryDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan processGrowthWindow = TimeSpan.FromMinutes(2);
    private readonly DispatcherTimer memoryTimer = new() { Interval = memoryInterval };
    private readonly Queue<MemoryPoint> memoryHistory = [];
    private readonly Queue<ProcessMemorySample> processHistory = [];
    private readonly ObservableCollection<ProcessMemoryRow> memoryRows = [];
    private readonly Dictionary<ProcessMemoryKey, ProcessMemoryRow> memoryRowsByProcess = [];
    private ListCollectionView? memoryProcessView;
    private MemorySession? memorySession;
    private PagefileDiagnostics? pagefileDiagnostics;
    private DateTimeOffset nextPagefileRefresh;
    private bool readingMemory;
    private bool readingPagefiles;
    private bool trimmingProcess;
    private readonly Dictionary<ProcessMemoryKey, ProcessMemory> memoryTrimQueue = [];

    private bool MemoryDiagnosticsVisible => MemoryPage.IsVisible && WindowState != WindowState.Minimized;

    private void InitializeMemoryDiagnostics()
    {
        memoryProcessView = (ListCollectionView)CollectionViewSource.GetDefaultView(memoryRows);
        memoryProcessView.Filter = FilterMemoryProcess;
        memoryProcessView.LiveSortingProperties.Add(nameof(ProcessMemoryRow.WorkingSetBytes));
        memoryProcessView.LiveSortingProperties.Add(nameof(ProcessMemoryRow.GrowthBytes));
        memoryProcessView.IsLiveSorting = true;
        SetMemoryProcessSort();
        MemoryProcesses.ItemsSource = memoryProcessView;
        memoryTimer.Tick += async (_, _) => await RefreshMemoryDiagnosticsAsync();
        MemoryPage.IsVisibleChanged += (_, _) => UpdateMemoryPolling();
        RenderMemorySession();
    }

    private async void UpdateMemoryPolling()
    {
        if (!initialized || closing || (!MemoryDiagnosticsVisible && memorySession is not { Ended: null }))
        {
            memoryTimer.Stop();
            return;
        }

        if (!memoryTimer.IsEnabled)
        {
            memoryTimer.Start();
            await RefreshMemoryDiagnosticsAsync();
        }
    }

    private Task RefreshMemoryDiagnosticsAsync() =>
        Task.WhenAll(RefreshMemoryReadingAsync(), RefreshPagefileDiagnosticsAsync());

    private async Task RefreshMemoryReadingAsync()
    {
        if (readingMemory || closing || !memoryTimer.IsEnabled)
            return;

        readingMemory = true;
        var activeSession = memorySession is { Ended: null } ? memorySession : null;
        try
        {
            var read = await RunInBackground(ReadMemoryDiagnostics);
            if (closing)
                return;

            var now = DateTimeOffset.UtcNow;
            while (memoryHistory.TryPeek(out var oldest) && now - oldest.At > memoryHistoryDuration)
                memoryHistory.Dequeue();
            if (read.System is { } point)
                memoryHistory.Enqueue(point);
            if (read.Processes is { } processes)
                AddProcessMemorySample(processes);

            if (activeSession is { Ended: null } && ReferenceEquals(activeSession, memorySession))
                UpdateMemorySession(activeSession, read);

            if (!MemoryDiagnosticsVisible)
                return;

            RenderMemoryReading(read.System, now);
            if (read.Processes is { } current)
                RenderMemoryProcesses(current);
            else
                SetStatus(MemoryProcessStatus, UiText.Get("Не удалось прочитать список процессов. Повторим через несколько секунд."), error: true);
        }
        finally
        {
            readingMemory = false;
        }
    }

    private static MemoryRead ReadMemoryDiagnostics()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemoryPoint? system = null;
        ProcessMemorySample? processes = null;
        try { system = new MemoryPoint(now, MemoryAndPagefileManager.ReadMemorySnapshot()); }
        catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        try { processes = new ProcessMemorySample(now, ReadProcessMemory()); }
        catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        return new MemoryRead(system, processes);
    }

    private static Dictionary<ProcessMemoryKey, ProcessMemory> ReadProcessMemory()
    {
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var samples = new Dictionary<ProcessMemoryKey, ProcessMemory>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id <= 4)
                    continue;

                try
                {
                    var key = new ProcessMemoryKey(process.Id, process.StartTime.ToUniversalTime());
                    bool canTrim = process.Id != current.Id && process.SessionId == session && session != 0;
                    samples[key] = new ProcessMemory(key, $"{process.ProcessName}.exe", process.WorkingSet64, canTrim);
                }
                catch (Exception ex) when (IsSystemError(ex))
                {
                    // A process can exit or refuse access while the snapshot is being collected.
                }
            }
        }

        return samples;
    }

    private void RenderMemoryReading(MemoryPoint? point, DateTimeOffset now)
    {
        var snapshot = point?.Snapshot;
        DiagnosticRam.Text = snapshot is null ? "—" : UiText.Format($"{FormatBytes(snapshot.Physical.AvailableBytes)} из {FormatBytes(snapshot.Physical.TotalBytes)}");
        DiagnosticCommit.Text = snapshot is null ? "—" : UiText.Format($"{FormatBytes(snapshot.Commit.CurrentBytes)} из {FormatBytes(snapshot.Commit.LimitBytes)}");
        DiagnosticHeadroom.Text = FormatBytes(snapshot?.Commit.AvailableBytes);
        DiagnosticSystemCache.Text = FormatBytes(snapshot?.SystemCacheBytes);
        DrawMemoryHistory(now);
        MemoryHistoryStatus.Visibility = VisibleWhen(point is null || memoryHistory.Count < 2);
        MemoryHistoryStatus.Text = point is null ? UiText.Get("Показатели Windows сейчас недоступны.") : UiText.Get("Собираем первые замеры…");

        int nearLimit = memoryHistory.Reverse().TakeWhile(sample => now - sample.At <= TimeSpan.FromSeconds(30))
            .Count(sample => sample.Snapshot.Commit is { LimitBytes: > 0 } commit && commit.CurrentBytes / (double)commit.LimitBytes >= .90);
        SetStatus(MemoryPressureStatus, nearLimit >= 8
            ? UiText.Get("Выделенная память близка к лимиту в большинстве замеров за последние 30 секунд. Проверьте режим подкачки и свободное место на диске.")
            : "", error: nearLimit >= 8);
        MemoryPressureStatus.Visibility = VisibleWhen(nearLimit >= 8);
        MemoryAdvice.Text = MemoryAndPagefileManager.ExplainManualSizing(snapshot?.Physical.TotalBytes, snapshot?.Commit, pagefileDiagnostics?.Dump);
    }

    private void MemoryHistorySizeChanged(object sender, SizeChangedEventArgs e) =>
        DrawMemoryHistory(DateTimeOffset.UtcNow);

    private void DrawMemoryHistory(DateTimeOffset now)
    {
        if (MemoryHistoryPlot is null || RamHistoryLine is null || CommitHistoryLine is null)
            return;

        double width = MemoryHistoryPlot.ActualWidth;
        double height = MemoryHistoryPlot.ActualHeight;
        if (width <= 0 || height <= 2)
            return;

        RamHistoryLine.Data = MemoryHistoryGeometry(now, width, height, sample =>
            sample.Physical.TotalBytes > 0 ? 1 - sample.Physical.AvailableBytes / (double)sample.Physical.TotalBytes : null);
        CommitHistoryLine.Data = MemoryHistoryGeometry(now, width, height, sample =>
            sample.Commit.LimitBytes > 0 ? sample.Commit.CurrentBytes / (double)sample.Commit.LimitBytes : null);
    }

    private StreamGeometry MemoryHistoryGeometry(DateTimeOffset now, double width, double height,
        Func<MemorySnapshot, double?> ratio)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            DateTimeOffset? previous = null;
            foreach (var sample in memoryHistory)
            {
                var age = now - sample.At;
                if (age > memoryHistoryDuration || ratio(sample.Snapshot) is not { } value)
                {
                    previous = null;
                    continue;
                }

                double x = Math.Clamp(1 - age.TotalSeconds / memoryHistoryDuration.TotalSeconds, 0, 1) * width;
                double y = 1 + (1 - Math.Clamp(value, 0, 1)) * (height - 2);
                var point = new Point(x, y);
                if (previous is { } timestamp && sample.At - timestamp <= memoryInterval * 2.5)
                    path.LineTo(point, isStroked: true, isSmoothJoin: false);
                else
                    path.BeginFigure(point, isFilled: false, isClosed: false);
                previous = sample.At;
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void AddProcessMemorySample(ProcessMemorySample sample)
    {
        var cutoff = sample.At - processGrowthWindow;
        while (processHistory.Count > 1 && processHistory.ElementAt(1).At <= cutoff)
            processHistory.Dequeue();
        processHistory.Enqueue(sample);
    }

    private void RenderMemoryProcesses(ProcessMemorySample latest)
    {
        var age = latest.At - processHistory.Peek().At;
        var baseline = age >= processGrowthWindow && age <= processGrowthWindow + memoryInterval * 2.5
            ? processHistory.Peek().Processes : null;

        foreach (var key in memoryRowsByProcess.Keys.Where(key => !latest.Processes.ContainsKey(key)).ToArray())
        {
            memoryRows.Remove(memoryRowsByProcess[key]);
            memoryRowsByProcess.Remove(key);
        }

        foreach (var process in latest.Processes.Values)
        {
            if (!memoryRowsByProcess.TryGetValue(process.Key, out var row))
            {
                row = new ProcessMemoryRow(process);
                memoryRowsByProcess.Add(process.Key, row);
                memoryRows.Add(row);
            }

            long? growth = baseline is not null && baseline.TryGetValue(process.Key, out var previous) ? process.Bytes - previous.Bytes : null;
            row.Update(process, growth);
        }

        SetStatus(MemoryProcessStatus, memoryRows.Count == 0 ? UiText.Get("Windows не вернула доступных процессов.") :
            baseline is null ? UiText.Format($"Доступно процессов: {memoryRows.Count}. Прирост появится после двух минут наблюдения.") :
            UiText.Format($"Доступно процессов: {memoryRows.Count}. Рабочий набор может включать общую память."));
        UpdateMemoryProcessFilterState();
    }

    private void OpenProcessSearch(string name)
    {
        MemoryNav.IsChecked = true;
        MemoryProcessSearch.Text = name;
        BringControlIntoView(MemoryProcessSearch);
        if (!webReady)
            MemoryProcessSearch.Focus();
    }

    private bool FilterMemoryProcess(object item)
    {
        string search = MemoryProcessSearch.Text.Trim();
        return item is ProcessMemoryRow row && (row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
            row.Pid.ToString().StartsWith(search, StringComparison.Ordinal));
    }

    private void MemoryProcessSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (memoryProcessView is not { } view)
            return;

        view.Refresh();
        UpdateMemoryProcessFilterState();
    }

    private void UpdateMemoryProcessFilterState() =>
        MemoryProcessNoMatches.Visibility = VisibleWhen(memoryRows.Count > 0 && !MemoryProcesses.HasItems);

    private void MemoryProcessSortChanged(object sender, SelectionChangedEventArgs e) => SetMemoryProcessSort();

    private void SetMemoryProcessSort()
    {
        if (memoryProcessView is not { } view)
            return;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            if (MemoryProcessSort.SelectedIndex == 1)
                view.SortDescriptions.Add(new SortDescription(nameof(ProcessMemoryRow.GrowthBytes), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription(nameof(ProcessMemoryRow.WorkingSetBytes), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription(nameof(ProcessMemoryRow.Pid), ListSortDirection.Ascending));
        }
    }

    private void MemoryProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (memoryProcessView is null)
            return;

        if (!trimmingProcess)
            SetStatus(TrimStatus, "");
        UpdateMemoryProcessAction();
    }

    private void UpdateMemoryProcessAction()
    {
        var process = MemoryProcesses.SelectedItem as ProcessMemoryRow;
        bool canTrim = process is { Data.CanTrim: true };
        TrimButton.IsEnabled = canTrim && !trimmingProcess;
        QueueMemoryProcessButton.IsEnabled = process is { Data.CanTrim: true } && !trimmingProcess &&
            memoryTrimQueue.Count < 8 && !memoryTrimQueue.ContainsKey(process.Data.Key);
        RunMemoryTrimQueueButton.IsEnabled = ClearMemoryTrimQueueButton.IsEnabled = !trimmingProcess && memoryTrimQueue.Count > 0;
        MemoryTrimQueueText.Text = memoryTrimQueue.Count > 0
            ? string.Join(" · ", memoryTrimQueue.Values.Select(item => $"{item.Name} (PID {item.Key.Pid})")) : UiText.Get("Очередь пуста");
        MemoryProcessSelection.Text = process is null
            ? UiText.Get("Выберите процесс в таблице") : $"{process.Name}  ·  PID {process.Pid}";
        MemoryProcessActionHint.Visibility = VisibleWhen(process is not null);
        MemoryProcessActionHint.Text = canTrim
            ? UiText.Get("При необходимости Windows снова загрузит страницы в рабочий набор.")
            : UiText.Get("Сброс рабочего набора для этого процесса недоступен.");
    }

    private async void TrimClick(object sender, RoutedEventArgs e)
    {
        if (trimmingProcess || MemoryProcesses.SelectedItem is not ProcessMemoryRow { Data.CanTrim: true } selected)
            return;

        var process = selected.Data;
        trimmingProcess = true;
        UpdateMemoryProcessAction();
        SetStatus(TrimStatus, UiText.Format($"Сбрасываем рабочий набор {process.Name} (PID {process.Key.Pid})…"));
        try
        {
            var (before, after) = await RunInBackground(() => MemoryOptimizer.TrimWorkingSet(process.Key.Pid, process.Key.StartedUtc));
            SetStatus(TrimStatus, before.HasValue && after.HasValue
                ? UiText.Format($"{process.Name} (PID {process.Key.Pid}) · Рабочий набор: {FormatBytes(before)} → {FormatBytes(after)}.")
                : UiText.Format($"Рабочий набор {process.Name} (PID {process.Key.Pid}) сброшен. Замер до или после операции недоступен."));
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(TrimStatus, UiText.Format($"Не удалось сбросить рабочий набор {process.Name}: {ex.Message}"), error: true);
        }
        finally
        {
            trimmingProcess = false;
            UpdateMemoryProcessAction();
        }
    }

    private void QueueMemoryProcessClick(object sender, RoutedEventArgs e)
    {
        if (!trimmingProcess && memoryTrimQueue.Count < 8 && MemoryProcesses.SelectedItem is ProcessMemoryRow { Data.CanTrim: true } row)
            memoryTrimQueue.TryAdd(row.Data.Key, row.Data);
        UpdateMemoryProcessAction();
    }

    private void ClearMemoryQueueClick(object sender, RoutedEventArgs e)
    {
        if (trimmingProcess) return;
        memoryTrimQueue.Clear();
        UpdateMemoryProcessAction();
    }

    private async void TrimMemoryQueueClick(object sender, RoutedEventArgs e)
    {
        if (trimmingProcess || closing || memoryTrimQueue.Count == 0 || !Confirm(UiText.Get("Сброс ОЗУ для выбранных процессов"),
            MemoryTrimQueueText.Text + "\n\n" + UiText.Get("Windows может снова загрузить освобождённые страницы."), UiText.Get("Сбросить очередь")))
            return;
        var targets = memoryTrimQueue.Values.ToArray();
        trimmingProcess = true;
        UpdateMemoryProcessAction();
        try
        {
            var measurements = await TrackOperation(RunInBackground(() => targets.Select(process =>
            {
                try
                {
                    var (before, after) = MemoryOptimizer.TrimWorkingSet(process.Key.Pid, process.Key.StartedUtc);
                    return $"{process.Name} ({process.Key.Pid}) · {FormatBytes(before)} → {FormatBytes(after)}";
                }
                catch (Exception ex) when (IsSystemError(ex)) { return $"{process.Name} ({process.Key.Pid}) · {ex.Message}"; }
            }).ToArray()));
            SetStatus(MemoryTrimQueueStatus, string.Join(Environment.NewLine, measurements));
            memoryTrimQueue.Clear();
        }
        finally { trimmingProcess = false; if (!closing) UpdateMemoryProcessAction(); }
    }

    private async Task RefreshPagefileDiagnosticsAsync()
    {
        if (!MemoryDiagnosticsVisible || closing || readingPagefiles || DateTimeOffset.UtcNow < nextPagefileRefresh)
            return;

        readingPagefiles = true;
        try
        {
            var result = await RunInBackground(ReadPagefileDiagnostics);
            if (closing)
                return;

            pagefileDiagnostics = result;
            PagefileMode.Text = result.Automatic switch
            {
                true => UiText.Get("Режим: Windows управляет размером автоматически."),
                false => UiText.Get("Режим: управление настроено отдельно для каждого диска."),
                null => UiText.Get("Режим управления недоступен.")
            };
            DiagnosticPagefileStatus.Text = result.Files switch
            {
                null => UiText.Get("Данные об использовании подкачки недоступны."),
                { Length: 0 } => UiText.Get("Активных файлов подкачки нет."),
                _ => ""
            };
            DiagnosticPagefileStatus.Visibility = VisibleWhen(result.Files is null or { Length: 0 });
            DiagnosticPagefiles.ItemsSource = result.Files;
        }
        finally
        {
            nextPagefileRefresh = DateTimeOffset.UtcNow.AddSeconds(15);
            readingPagefiles = false;
        }
    }

    private static PagefileDiagnostics ReadPagefileDiagnostics()
    {
        bool? automatic = null;
        CrashDumpSettings? dump = null;
        MemoryPagefileRow[]? files = null;
        try { automatic = MemoryAndPagefileManager.ReadAutomaticPagefileManagement(); }
        catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        try { dump = MemoryAndPagefileManager.ReadCrashDumpSettings(); }
        catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        try
        {
            files = MemoryAndPagefileManager.ReadLivePagefiles().Select(file => new MemoryPagefileRow(
                file.Name, file.CurrentMiB, file.AllocatedMiB, file.PeakMiB, ReadPagefileDiskSpace(file.Name))).ToArray();
        }
        catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        return new PagefileDiagnostics(automatic, files, dump);
    }

    private static ulong? ReadPagefileDiskSpace(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && Path.IsPathFullyQualified(root)
                ? (ulong)new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private void OpenPagefileSettingsClick(object sender, RoutedEventArgs e) =>
        OpenWindowsTool(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "SystemPropertiesAdvanced.exe"))
            { UseShellExecute = true }, PagefileSettingsStatus);

    private void StartMemorySession(string name)
    {
        if (memorySession is { Ended: null })
            return;

        memorySession = new MemorySession(name);
        RenderMemorySession();
        UpdateMemoryPolling();
    }

    private void FinishMemorySession()
    {
        if (memorySession is not { Ended: null } active)
            return;

        active.Ended = DateTimeOffset.UtcNow;
        RenderMemorySession();
        UpdateMemoryPolling();
    }

    private void UpdateMemorySession(MemorySession session, MemoryRead read)
    {
        session.Baseline ??= read.Processes;
        if (read.System is not { Snapshot.Commit.LimitBytes: > 0 } point ||
            session.Minimum is { } previous && previous.Snapshot.Commit.AvailableBytes <= point.Snapshot.Commit.AvailableBytes)
            return;

        session.Minimum = point;
        session.Growth = [];
        if (session.Baseline is { } baseline && read.Processes is { } current)
        {
            session.Growth = current.Processes.Values
                .Select(process => (Process: process, Growth: baseline.Processes.TryGetValue(process.Key, out var initial)
                    ? process.Bytes - initial.Bytes
                    : process.Key.StartedUtc >= baseline.At.UtcDateTime ? process.Bytes : 0))
                .Where(item => item.Growth > 0)
                .OrderByDescending(item => item.Growth)
                .Take(3)
                .Select(item => UiText.Format($"{item.Process.Name} (PID {item.Process.Key.Pid})  ·  {FormatGrowth(item.Growth)} ОЗУ"))
                .ToArray();
        }

        RenderMemorySession();
    }

    private void RenderMemorySession()
    {
        var session = memorySession;
        MemorySessionStatus.Text = session is null
            ? UiText.Get("Запустите профиль и переключитесь на выбранное приложение, чтобы начать замеры.")
            : $"{session.Name}  ·  {session.Started.ToLocalTime():HH:mm:ss}" +
              (session.Ended is { } end ? $"–{end.ToLocalTime():HH:mm:ss}" : UiText.Get("  ·  Идёт запись"));
        MemorySessionPeak.Text = session?.Minimum is { } sample
            ? UiText.Format($"Минимальный запас: {FormatBytes(sample.Snapshot.Commit.AvailableBytes)} в {sample.At.ToLocalTime():HH:mm:ss}.") +
              (session.Growth.Length > 0 ? UiText.Get(" На тот момент больше всего выросли с начала сеанса:") : "")
            : session is null ? "" : session.Ended.HasValue ? UiText.Get("За сеанс не успели получить замеры.") : UiText.Get("Собираем первые замеры…");
        MemorySessionPeak.Visibility = VisibleWhen(session is not null);
        MemorySessionProcesses.ItemsSource = session?.Growth;
    }

    private static string FormatGrowth(long bytes) =>
        bytes == 0 ? FormatBytes(0) : $"{(bytes < 0 ? "−" : "+")}{FormatBytes((ulong)Math.Abs(bytes))}";
}
