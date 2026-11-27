using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string FocusReportFile = "focus-reports.json";
    private readonly DispatcherTimer focusComparisonTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private FocusTrial? focusTrial;
    private FocusComparisonReport? displayedFocusReport;
    private FocusComparisonReport[] focusReports = [];
    private bool readingFocusSample;
    private bool updatingFocusHistory;
    private bool readingFocusHistory;
    private bool savingFocusHistory;
    private int focusHistoryRevision;
    private FocusLiveWindow? focusLiveWindow;

    private enum FocusTrialStage { Baseline, Ready, Optimized, Complete, Stopped, Failed }
    private sealed record TargetProbe(FocusObservation Observation, ProcessPriorityClass Priority);
    private sealed record TrialProbe(FocusObservation Observation, TargetProbe[] Targets);
    private sealed record FocusMetric(string Label, string Baseline, string Optimized, string Delta);
    private sealed record FocusTargetResult(string Label, FocusPhaseResult? Baseline, FocusPhaseResult? Optimized,
        ProcessPriorityClass? Original);
    private sealed record FocusTargetMetric(string Label, string CpuDelta, string MemoryDelta);
    private sealed record FocusComparisonReport(string App, DateTimeOffset Date, FocusPhaseResult Baseline,
        FocusPhaseResult Optimized, ProcessPriorityClass? Original, ProcessPriorityClass Requested)
    {
        public double? BeforeFps { get; init; }
        public double? AfterFps { get; init; }
        public FocusTargetResult[] Targets { get; init; } = [];
        public string Label => $"{App} · {Date.ToLocalTime():g}";
    }

    private sealed class FocusTargetTrack(ProfileApplicationRow app)
    {
        internal ProfileApplicationRow App { get; } = app;
        internal FocusPhaseMeasurement Baseline { get; } = new(Environment.ProcessorCount);
        internal FocusPhaseMeasurement Optimized { get; } = new(Environment.ProcessorCount);
        internal ProcessPriorityClass? Original { get; set; }

        internal FocusTargetResult ReadResult() => new($"{App.DisplayName} · PID {App.Process.Id}",
            Baseline.ReadResult(), Optimized.ReadResult(), Original);
    }

    private sealed class FocusTrial(ProfileApplicationRow[] apps, string name, ProcessPriorityClass priority, int duration,
        bool trackInBackground)
    {
        private int stage;
        internal string Name { get; } = name;
        internal FocusTargetTrack[] Targets { get; } = apps.Select(app => new FocusTargetTrack(app)).ToArray();
        internal ProcessPriorityClass Priority { get; } = priority;
        internal int Duration { get; } = duration;
        internal bool TrackInBackground { get; } = trackInBackground;
        internal FocusTrialStage Stage
        {
            get => (FocusTrialStage)Volatile.Read(ref stage);
            set => Volatile.Write(ref stage, (int)value);
        }
        internal bool ApplyProfile => Stage is FocusTrialStage.Optimized or FocusTrialStage.Complete;
        internal FocusPhaseMeasurement Baseline { get; } = new(Environment.ProcessorCount);
        internal FocusPhaseMeasurement Optimized { get; } = new(Environment.ProcessorCount);
        internal int PriorityWaitSamples { get; set; }
        internal bool HasFocus { get; set; }
    }

    private void InitializeFocusComparison()
    {
        focusComparisonTimer.Tick += async (_, _) => await SampleFocusTrialAsync();
        RenderFocusComparison();
    }

    private FocusTrial? BeginFocusTrial(ProfileApplicationRow[] apps, string name, ProcessPriorityClass priority)
    {
        displayedFocusReport = null;
        focusTrial = CompareMode.IsChecked == true
            ? new FocusTrial(apps, name, priority, preferences.FocusPhaseSeconds, FocusBackground.IsChecked == true) : null;
        UpdateFocusHistory();
        ResetFocusInputs();
        FocusReportStatus.Visibility = Visibility.Collapsed;
        if (focusTrial is not null)
            focusComparisonTimer.Start();
        SyncFocusLiveWindow();
        RenderFocusComparison();
        return focusTrial;
    }

    private void EndFocusTrial()
    {
        focusComparisonTimer.Stop();
        if (focusTrial is { Stage: not (FocusTrialStage.Complete or FocusTrialStage.Failed) } trial)
            trial.Stage = FocusTrialStage.Stopped;
        if (focusTrial?.Stage != FocusTrialStage.Complete)
            CloseFocusLiveWindow();
        RenderFocusComparison();
    }

    private void FocusModeChanged(object sender, RoutedEventArgs e) => UpdateProfileControls();

    private void FocusMonitorChanged(object sender, RoutedEventArgs e)
    {
        SyncFocusLiveWindow();
        RenderFocusComparison();
    }

    private void SyncFocusLiveWindow()
    {
        if (closing || focusTrial is null || profileStop is null && focusTrial.Stage != FocusTrialStage.Complete || FocusMonitorToggle.IsChecked != true)
        {
            CloseFocusLiveWindow();
            return;
        }
        if (focusLiveWindow is not null)
            return;

        var window = new FocusLiveWindow(lightTheme);
        focusLiveWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(focusLiveWindow, window))
            {
                focusLiveWindow = null;
                FocusMonitorToggle.IsChecked = false;
            }
        };
        window.Show();
    }

    private void CloseFocusLiveWindow()
    {
        var window = focusLiveWindow;
        focusLiveWindow = null;
        window?.Close();
    }

    private void ContinueFocusTrialClick(object sender, RoutedEventArgs e)
    {
        if (focusTrial is not { Stage: FocusTrialStage.Ready } trial || profileStop is not { IsCancellationRequested: false })
            return;

        trial.Stage = FocusTrialStage.Optimized;
        trial.HasFocus = false;
        RenderFocusComparison();
    }

    private async Task SampleFocusTrialAsync()
    {
        if (readingFocusSample || closing || profileStop is not { IsCancellationRequested: false } ||
            focusTrial is not { Stage: FocusTrialStage.Baseline or FocusTrialStage.Optimized } trial)
            return;

        var stage = trial.Stage;
        readingFocusSample = true;
        try
        {
            var reading = await RunInBackground(() => ReadFocusProbe(trial.Targets, trial.TrackInBackground));
            if (closing || focusTrial != trial || trial.Stage != stage || profileStop is not { IsCancellationRequested: false })
                return;

            var measurement = stage == FocusTrialStage.Baseline ? trial.Baseline : trial.Optimized;
            trial.HasFocus = reading is not null;
            if (reading is { } probe && stage == FocusTrialStage.Optimized && probe.Targets.Any(target => target.Priority != trial.Priority))
            {
                ObserveFocusSample(trial, measurement, stage, null);
                if (++trial.PriorityWaitSamples >= 4)
                {
                    trial.Stage = FocusTrialStage.Failed;
                    profileStop.Cancel();
                }
            }
            else
            {
                trial.PriorityWaitSamples = 0;
                ObserveFocusSample(trial, measurement, stage, reading);
            }

            if (measurement.Seconds >= trial.Duration)
            {
                if (stage == FocusTrialStage.Baseline)
                    trial.Stage = FocusTrialStage.Ready;
                else if (trial.Baseline.ReadResult() is { } baseline && trial.Optimized.ReadResult() is { } optimized)
                {
                    var targets = trial.Targets.Select(target => target.ReadResult()).ToArray();
                    var report = new FocusComparisonReport(trial.Name, DateTimeOffset.UtcNow, baseline, optimized,
                        targets.Length == 1 ? targets[0].Original : null, trial.Priority) { Targets = targets };
                    trial.Stage = FocusTrialStage.Complete;
                    displayedFocusReport = report;
                    profileStop.Cancel();
                    RenderFocusComparison();
                    if (preferences.SaveFocusReports)
                        await SaveFocusReportAsync(report);
                }
            }
            RenderFocusComparison();
        }
        finally
        {
            readingFocusSample = false;
        }
    }

    private static void ObserveFocusSample(FocusTrial trial, FocusPhaseMeasurement group, FocusTrialStage stage, TrialProbe? sample)
    {
        group.Observe(sample?.Observation);
        for (int i = 0; i < trial.Targets.Length; i++)
        {
            var track = trial.Targets[i];
            var reading = sample?.Targets[i];
            if (stage == FocusTrialStage.Baseline)
            {
                track.Original ??= reading?.Priority;
                track.Baseline.Observe(reading?.Observation);
            }
            else
                track.Optimized.Observe(reading?.Observation);
        }
    }

    private static TrialProbe? ReadFocusProbe(FocusTargetTrack[] targets, bool trackInBackground)
    {
        bool HasSelectedWindow() => trackInBackground || ContextProfileManager.ReadForegroundProcessId() is int id &&
            targets.Any(target => target.App.Process.Id == id);
        if (!HasSelectedWindow())
            return null;

        try
        {
            ulong? headroom = null;
            try { headroom = MemoryAndPagefileManager.ReadMemorySnapshot().Commit.AvailableBytes; }
            catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
            var observations = new TargetProbe[targets.Length];
            var timestamp = Stopwatch.GetTimestamp();
            TimeSpan cpu = TimeSpan.Zero;
            long bytes = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i].App.Process;
                using var process = Process.GetProcessById(target.Id);
                if (process.StartTime.ToUniversalTime() != target.StartedUtc)
                    return null;
                var observation = new FocusObservation(timestamp, process.TotalProcessorTime, process.WorkingSet64, null);
                observations[i] = new TargetProbe(observation, process.PriorityClass);
                cpu += observation.Cpu;
                bytes += observation.WorkingSet;
            }
            return HasSelectedWindow() ? new TrialProbe(new FocusObservation(timestamp, cpu, bytes, headroom), observations) : null;
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private void RenderFocusComparison()
    {
        if (FocusTrialStatus is null || FocusReportHeader is null || FocusFpsPanel is null)
            return;
        RenderFocusSignal();

        var trial = focusTrial;
        var report = displayedFocusReport;
        var baseline = report?.Baseline ?? trial?.Baseline.ReadResult();
        var optimized = report?.Optimized ?? trial?.Optimized.ReadResult();
        string? status = trial?.Stage switch
        {
            FocusTrialStage.Ready => UiText.Get("Первый замер готов. Вернитесь сюда, начните второй и повторите ту же нагрузку."),
            FocusTrialStage.Stopped => UiText.Get("Профиль остановлен до завершения сравнения."),
            FocusTrialStage.Failed => UiText.Get("Приоритет не применился. Замер с профилем остановлен."),
            FocusTrialStage.Complete => UiText.Get("Два замера готовы. Исходные параметры восстанавливаются при остановке профиля."),
            _ when trial is { HasFocus: false, TrackInBackground: false } => UiText.Get("Переключитесь на окно одного из выбранных процессов. Замер приостановлен."),
            FocusTrialStage.Baseline => UiText.Format($"Замер без профиля · {trial.Baseline.Seconds:0} из {trial.Duration} с"),
            FocusTrialStage.Optimized => UiText.Format($"Замер с профилем · {trial.Optimized.Seconds:0} из {trial.Duration} с"),
            _ => null
        };
        focusLiveWindow?.UpdateReadings(status ?? "", baseline, optimized);
        if (trial is not null && profileStop is not null && (!ProfilePage.IsVisible || WindowState == WindowState.Minimized))
            return;

        if (status is null)
            FocusTrialStatus.SetResourceReference(TextBlock.TextProperty,
                "UI:Сравнить одинаковую нагрузку без профиля и с ним. Повторите один и тот же сценарий в обоих замерах.");
        else
            SetStatus(FocusTrialStatus, status, error: trial?.Stage == FocusTrialStage.Failed);

        ContinueFocusTrialButton.Visibility = VisibleWhen(trial?.Stage == FocusTrialStage.Ready);
        FocusTrialProgress.Visibility = VisibleWhen(trial is not null && trial.Stage != FocusTrialStage.Stopped);
        if (trial is not null)
        {
            double seconds = trial.Stage is FocusTrialStage.Baseline or FocusTrialStage.Ready ? trial.Baseline.Seconds : trial.Optimized.Seconds;
            FocusTrialProgress.Value = Math.Min(100, seconds / trial.Duration * 100);
        }

        FocusReportHeader.Visibility = VisibleWhen(baseline is not null);
        UpdateRows(FocusMetricRows, baseline is null ? [] : CreateFocusRows(baseline, optimized));
        var targets = report?.Targets ?? trial?.Targets.Select(target => target.ReadResult()).ToArray() ?? [];
        FocusTargetSection.Visibility = VisibleWhen(targets.Length > 1 && baseline is not null);
        UpdateRows(FocusTargetRows, targets.Length > 1 ? targets.Select(CreateTargetMetrics).ToArray() : []);
        CopyFocusReportButton.IsEnabled = report is not null;
        FocusFpsPanel.Visibility = VisibleWhen(report is not null);
        FocusTrialSummary.Text = report is null ? "" : report.Label + Environment.NewLine +
            (report.Targets is { Length: > 1 }
                ? UiText.Format($"Процессов: {report.Targets.Length}. Приоритет группы: {PriorityLabel(report.Requested)}. Замеры: {report.Baseline.Seconds:0.0} и {report.Optimized.Seconds:0.0} с.")
                : UiText.Format($"Приоритет: {PriorityLabel(report.Original)} → {PriorityLabel(report.Requested)}. Замеры: {report.Baseline.Seconds:0.0} и {report.Optimized.Seconds:0.0} с."));
        UpdateFocusFps();
    }

    private static void UpdateRows<T>(ItemsControl control, T[] rows)
    {
        if (control.ItemsSource is not T[] previous || !previous.SequenceEqual(rows))
            control.ItemsSource = rows;
    }

    private static string PriorityLabel(ProcessPriorityClass? priority) => priority switch
    {
        ProcessPriorityClass.Normal => UiText.Get("Обычный"),
        ProcessPriorityClass.AboveNormal => UiText.Get("Выше обычного"),
        ProcessPriorityClass.BelowNormal => UiText.Get("Ниже обычного"),
        _ => priority?.ToString() ?? "—"
    };

    private static FocusMetric[] CreateFocusRows(FocusPhaseResult baseline, FocusPhaseResult? optimized)
    {
        FocusMetric Memory(string key, long? before, long? after) => new(UiText.Get(key),
            FormatBytes((ulong?)before), FormatBytes((ulong?)after),
            before.HasValue && after.HasValue ? FormatGrowth(after.Value - before.Value) : "—");

        return
        [
            new FocusMetric(UiText.Get("Средняя загрузка ЦП"), $"{baseline.CpuPercent:0.0}%",
                optimized is null ? "—" : $"{optimized.CpuPercent:0.0}%",
                optimized is null ? "—" : UiText.Format($"{optimized.CpuPercent - baseline.CpuPercent:+0.0;-0.0;0.0} п. п.")),
            Memory("Средний рабочий набор", baseline.AverageWorkingSet, optimized?.AverageWorkingSet),
            Memory("Пиковый рабочий набор", baseline.PeakWorkingSet, optimized?.PeakWorkingSet),
            Memory("Минимальный запас выделяемой памяти", (long?)baseline.MinimumCommitHeadroom, (long?)optimized?.MinimumCommitHeadroom)
        ];
    }

    private static FocusTargetMetric CreateTargetMetrics(FocusTargetResult target) => target is { Baseline: { } before, Optimized: { } after }
        ? new FocusTargetMetric(target.Label, UiText.Format($"{after.CpuPercent - before.CpuPercent:+0.0;-0.0;0.0} п. п."),
            FormatGrowth(after.AverageWorkingSet - before.AverageWorkingSet))
        : new FocusTargetMetric(target.Label, "—", "—");

    private void FocusFpsChanged(object sender, TextChangedEventArgs e) => UpdateFocusFps();

    private void UpdateFocusFps()
    {
        if (FocusFpsDelta is null || SaveFpsButton is null)
            return;

        double? before = ReadFps(BaselineFps.Text);
        double? after = ReadFps(OptimizedFps.Text);
        FocusFpsDelta.Text = before is { } baseline && after is { } optimized
            ? UiText.Format($"Изменение FPS: {(optimized / baseline - 1) * 100:+0.0;-0.0;0.0}%") : "";
        SaveFpsButton.Visibility = VisibleWhen(preferences.SaveFocusReports);
        SaveFpsButton.IsEnabled = before.HasValue && after.HasValue && !savingFocusHistory && displayedFocusReport is { } report &&
            (report.BeforeFps != before || report.AfterFps != after);
    }

    private async void SaveFocusFpsClick(object sender, RoutedEventArgs e)
    {
        if (!preferences.SaveFocusReports || savingFocusHistory || displayedFocusReport is not { } report ||
            ReadFps(BaselineFps.Text) is not { } baseline || ReadFps(OptimizedFps.Text) is not { } optimized)
            return;

        var updated = report with { BeforeFps = baseline, AfterFps = optimized };
        if (await SaveFocusReportAsync(updated) && displayedFocusReport == report)
        {
            displayedFocusReport = updated;
            UpdateFocusHistory();
            ShowFocusReportStatus("FPS сохранён в отчёте.");
            RenderFocusComparison();
        }
    }

    private static double? ReadFps(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && value is > 0 and <= 100_000
            ? value : null;

    private void CopyFocusReportClick(object sender, RoutedEventArgs e)
    {
        if (displayedFocusReport is not { } report)
            return;

        var lines = new List<string> { FocusTrialSummary.Text };
        lines.AddRange(CreateFocusRows(report.Baseline, report.Optimized)
            .Select(row => $"{row.Label}: {row.Baseline} → {row.Optimized} ({row.Delta})"));
        if (report.Targets is { Length: > 1 })
            lines.AddRange(report.Targets.Select(CreateTargetMetrics).Select(row => $"{row.Label}: {row.CpuDelta} / {row.MemoryDelta}"));
        if (FocusFpsDelta.Text.Length > 0)
            lines.Add($"{UiText.Get("FPS из внешнего оверлея")}: {BaselineFps.Text} → {OptimizedFps.Text} · {FocusFpsDelta.Text}");
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            ShowFocusReportStatus("Отчёт скопирован.");
        }
        catch (ExternalException ex)
        {
            Debug.WriteLine(ex);
            ShowFocusReportStatus("Буфер обмена занят. Попробуйте скопировать отчёт ещё раз.", true);
        }
    }

    private void ShowFocusReportStatus(string text, bool error = false)
    {
        FocusReportStatus.Visibility = Visibility.Visible;
        SetStatus(FocusReportStatus, text, error);
    }

    private async Task LoadFocusReportsAsync()
    {
        if (readingFocusHistory || savingFocusHistory || closing)
            return;

        readingFocusHistory = true;
        int revision = focusHistoryRevision;
        try
        {
            var stored = await RunInBackground(() => localState.Read<FocusComparisonReport[]>(FocusReportFile) ?? []);
            if (!closing && revision == focusHistoryRevision)
            {
                focusReports = stored.Where(report => report is { Baseline: not null, Optimized: not null }).ToArray();
                UpdateFocusHistory();
            }
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            ShowFocusReportStatus("Не удалось прочитать историю замеров.", true);
        }
        finally { readingFocusHistory = false; }
    }

    private async Task<bool> SaveFocusReportAsync(FocusComparisonReport report)
    {
        if (savingFocusHistory)
            return false;

        savingFocusHistory = true;
        focusHistoryRevision++;
        SaveFpsButton.IsEnabled = false;
        try
        {
            int limit = preferences.FocusReportLimit;
            focusReports = await RunInBackground(() =>
            {
                var previous = localState.Read<FocusComparisonReport[]>(FocusReportFile) ?? [];
                var saved = previous.Where(item => item is not null && (item.Date != report.Date || item.App != report.App))
                    .Prepend(report).OrderByDescending(item => item.Date).Take(limit).ToArray();
                localState.Write(FocusReportFile, saved);
                return saved;
            });
            if (!closing)
                UpdateFocusHistory();
            return true;
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            ShowFocusReportStatus("Не удалось сохранить отчёт.", true);
            return false;
        }
        finally
        {
            savingFocusHistory = false;
            UpdateFocusFps();
        }
    }

    private void UpdateFocusHistory()
    {
        updatingFocusHistory = true;
        FocusHistoryPicker.ItemsSource = focusReports;
        FocusHistoryPicker.SelectedItem = displayedFocusReport;
        updatingFocusHistory = false;
    }

    private void FocusHistoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFocusHistory || profileStop is not null || FocusHistoryPicker.SelectedItem is not FocusComparisonReport report)
            return;

        focusTrial = null;
        CloseFocusLiveWindow();
        displayedFocusReport = report;
        BaselineFps.Text = report.BeforeFps?.ToString(CultureInfo.CurrentCulture) ?? "";
        OptimizedFps.Text = report.AfterFps?.ToString(CultureInfo.CurrentCulture) ?? "";
        FocusReportStatus.Visibility = Visibility.Collapsed;
        RenderFocusComparison();
    }

    private void ResetFocusInputs()
    {
        BaselineFps.Clear();
        OptimizedFps.Clear();
    }
}
