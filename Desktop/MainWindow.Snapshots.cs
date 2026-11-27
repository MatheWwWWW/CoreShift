using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string SnapshotFile = "snapshots.json";
    private const int SnapshotLimit = 20;
    private bool workingWithSnapshots;
    private bool changingSnapshotSelection;
    private SystemSnapshot? comparisonBaseline;

    private sealed record ApplicationUsage(string Name, long WorkingSet);
    private sealed record SystemSnapshot(DateTimeOffset CapturedAt, string Name, ulong RamUsed, ulong CommitUsed, ulong CommitReserve,
        string? SystemVolume, ulong? FreeSpace, ApplicationUsage[] Applications)
    {
        public string Label
        {
            get
            {
                var culture = CultureInfo.GetCultureInfo(UiText.IsEnglish ? "en-US" : "ru-RU");
                string time = CapturedAt.ToLocalTime().ToString("G", culture);
                return string.IsNullOrWhiteSpace(Name) ? time : $"{Name} · {time}";
            }
        }
    }
    private sealed record SnapshotDifference(string Label, string Values, string Difference);
    private sealed record CapturedSnapshot(SystemSnapshot Point, SystemSnapshot[] History);

    private async Task LoadSnapshotsAsync()
    {
        if (workingWithSnapshots || closing)
            return;

        workingWithSnapshots = true;
        CaptureSnapshotButton.IsEnabled = false;
        try
        {
            var snapshots = await RunInBackground(ReadSavedSnapshots);
            if (!closing)
            {
                SetSnapshots(snapshots, selectLatest: false);
                if (comparisonBaseline is { } baseline)
                    SetComparisonPoints(snapshots, baseline, null);
            }
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            if (!closing)
                ShowSnapshotStatus(UiText.Get("Не удалось прочитать снимки системы."), error: true);
        }
        finally
        {
            workingWithSnapshots = false;
            CaptureSnapshotButton.IsEnabled = !closing;
        }
    }

    private async void CaptureSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (workingWithSnapshots || closing)
            return;

        string name = SnapshotName.Text.Trim();
        var baseline = comparisonBaseline;
        string step = UiText.Get(baseline is null ? "До" : "После");
        string label = name.Length > 0 ? $"{name} · {step}" : step;
        workingWithSnapshots = true;
        CaptureSnapshotButton.IsEnabled = false;
        ShowSnapshotStatus(UiText.Get("Снимаем показания…"));
        try
        {
            var capture = await RunInBackground(() => CaptureSnapshot(label));
            if (closing)
                return;

            comparisonBaseline = baseline is null ? capture.Point : null;
            SetComparisonPoints(capture.History, baseline ?? capture.Point, baseline is null ? null : capture.Point);
            if (baseline is not null)
            {
                SnapshotName.Clear();
                CompleteTaskSound();
            }
            ShowSnapshotStatus(UiText.Get(baseline is null
                ? "Точка «До» сохранена. Выполните действие и нажмите «Снять после»."
                : "Сравнение готово. Изменения показаны справа."));
        }
        catch (Exception ex) when (IsSystemError(ex) || AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            if (!closing)
                ShowSnapshotStatus(UiText.Get("Не удалось сохранить снимок системы."), error: true);
        }
        finally
        {
            workingWithSnapshots = false;
            CaptureSnapshotButton.IsEnabled = !closing;
            UpdateSnapshotFlow();
        }
    }

    private void UpdateSnapshotFlow()
    {
        bool pending = comparisonBaseline is not null;
        CaptureSnapshotButton.SetResourceReference(ContentControl.ContentProperty, pending ? "UI:Снять после" : "UI:Снять до");
        CancelSnapshotButton.Visibility = VisibleWhen(pending);
        SnapshotName.IsEnabled = !pending;
    }

    private void CancelSnapshotFlowClick(object sender, RoutedEventArgs e)
    {
        if (workingWithSnapshots)
            return;
        comparisonBaseline = null;
        UpdateSnapshotFlow();
        ShowSnapshotStatus(UiText.Get("Можно начать новое сравнение. Точка «До» осталась в истории."));
    }

    private void SetComparisonPoints(SystemSnapshot[] history, SystemSnapshot before, SystemSnapshot? after)
    {
        changingSnapshotSelection = true;
        SnapshotBefore.ItemsSource = history;
        SnapshotAfter.ItemsSource = history;
        SnapshotBefore.SelectedItem = history.FirstOrDefault(point => point.CapturedAt == before.CapturedAt);
        SnapshotAfter.SelectedItem = after is null ? null : history.FirstOrDefault(point => point.CapturedAt == after.CapturedAt);
        changingSnapshotSelection = false;
        RenderSnapshotComparison();
    }

    private SystemSnapshot[] ReadSavedSnapshots() => (localState.Read<SystemSnapshot[]>(SnapshotFile) ?? [])
        .Where(snapshot => snapshot is { Applications: not null }).OrderBy(snapshot => snapshot.CapturedAt).TakeLast(SnapshotLimit).ToArray();

    private CapturedSnapshot CaptureSnapshot(string name)
    {
        var memory = MemoryAndPagefileManager.ReadMemorySnapshot();
        SystemDrive? drive = null;
        try { drive = ReadSystemDrive(); }
        catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        var applications = ReadProcessMemory().Values.GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApplicationUsage(group.Key, group.Sum(process => process.Bytes))).ToArray();
        var snapshot = new SystemSnapshot(DateTimeOffset.UtcNow, name,
            memory.Physical.TotalBytes - Math.Min(memory.Physical.AvailableBytes, memory.Physical.TotalBytes),
            memory.Commit.CurrentBytes, memory.Commit.AvailableBytes, drive?.Name, drive?.FreeBytes, applications);
        using var mutex = new Mutex(false, @"Local\CoreShift.SnapshotStore");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Snapshot storage is busy.");
        }
        catch (AbandonedMutexException) { }
        try
        {
            var saved = ReadSavedSnapshots().Append(snapshot).OrderBy(item => item.CapturedAt).TakeLast(SnapshotLimit).ToArray();
            localState.Write(SnapshotFile, saved);
            return new CapturedSnapshot(snapshot, saved);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private void SetSnapshots(SystemSnapshot[] snapshots, bool selectLatest)
    {
        var before = SnapshotBefore.SelectedItem as SystemSnapshot;
        var after = SnapshotAfter.SelectedItem as SystemSnapshot;
        changingSnapshotSelection = true;
        SnapshotBefore.ItemsSource = snapshots;
        SnapshotAfter.ItemsSource = snapshots;
        SnapshotBefore.SelectedItem = !selectLatest && before is not null
            ? snapshots.FirstOrDefault(snapshot => snapshot.CapturedAt == before.CapturedAt) : null;
        SnapshotAfter.SelectedItem = !selectLatest && after is not null
            ? snapshots.FirstOrDefault(snapshot => snapshot.CapturedAt == after.CapturedAt) : null;
        if (SnapshotBefore.SelectedIndex < 0 && snapshots.Length >= 2)
            SnapshotBefore.SelectedIndex = snapshots.Length - 2;
        if (SnapshotAfter.SelectedIndex < 0)
            SnapshotAfter.SelectedIndex = snapshots.Length - 1;
        changingSnapshotSelection = false;
        RenderSnapshotComparison();
    }

    private void SnapshotComparisonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!changingSnapshotSelection)
            RenderSnapshotComparison();
    }

    private void FindSnapshotProcessClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
            OpenProcessSearch(name);
    }

    private void CopySnapshotClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotBefore.SelectedItem is not SystemSnapshot before || SnapshotAfter.SelectedItem is not SystemSnapshot after ||
            SnapshotRows.ItemsSource is not IEnumerable<SnapshotDifference> differences)
            return;

        var report = new List<string>
        {
            "CoreShift · " + UiText.Get("Сравнение снимков"),
            before.Label + " → " + after.Label,
            ""
        };
        report.AddRange(differences.Select(row => $"{row.Label}: {row.Values} ({row.Difference})"));
        if (SnapshotProcessRows.ItemsSource is IEnumerable<SnapshotDifference> apps)
            report.AddRange(apps.Select(app => $"{app.Label}: {app.Difference}"));

        SnapshotCopyStatus.Visibility = Visibility.Visible;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, report));
            SetStatus(SnapshotCopyStatus, "Отчёт скопирован.");
        }
        catch (ExternalException ex)
        {
            Debug.WriteLine(ex.Message);
            SetStatus(SnapshotCopyStatus, "Буфер обмена занят. Попробуйте скопировать отчёт ещё раз.", error: true);
        }
    }

    private void ShowSnapshotStatus(string message, bool error = false)
    {
        SnapshotStatus.Visibility = Visibility.Visible;
        SetStatus(SnapshotStatus, message, error);
    }

    private void RenderSnapshotComparison()
    {
        if (SnapshotBefore is null || SnapshotAfter is null || SnapshotRows is null || SnapshotCopyStatus is null)
            return;

        SnapshotCopyStatus.Visibility = Visibility.Collapsed;
        if (SnapshotBefore.SelectedItem is not SystemSnapshot before || SnapshotAfter.SelectedItem is not SystemSnapshot after ||
            before.CapturedAt == after.CapturedAt)
        {
            CopySnapshotButton.IsEnabled = false;
            SnapshotRows.ItemsSource = null;
            SnapshotProcessRows.ItemsSource = null;
            SnapshotProcessHeading.Visibility = Visibility.Collapsed;
            SnapshotEmpty.Text = UiText.Get(comparisonBaseline is not null ? "Снимите показатели после изменения." : SnapshotAfter.Items.Count < 2
                ? "Сохраните два снимка, чтобы увидеть разницу." : "Выберите два разных снимка.");
            SnapshotEmpty.Visibility = Visibility.Visible;
            return;
        }

        SnapshotDifference Compare(string label, ulong? oldValue, ulong? newValue) =>
            new(UiText.Get(label), $"{FormatBytes(oldValue)}  →  {FormatBytes(newValue)}",
                oldValue.HasValue && newValue.HasValue ? FormatGrowth((long)newValue.Value - (long)oldValue.Value) : "—");

        bool sameVolume = string.Equals(before.SystemVolume, after.SystemVolume, StringComparison.OrdinalIgnoreCase);
        CopySnapshotButton.IsEnabled = true;
        SnapshotEmpty.Visibility = Visibility.Collapsed;
        SnapshotRows.ItemsSource = new[]
        {
            Compare("Занято ОЗУ", before.RamUsed, after.RamUsed),
            Compare("Выделено Windows", before.CommitUsed, after.CommitUsed),
            Compare("Запас до лимита", before.CommitReserve, after.CommitReserve),
            Compare("Свободно на диске Windows", sameVolume ? before.FreeSpace : null, sameVolume ? after.FreeSpace : null)
        };

        var deltas = before.Applications.Select(item => (item.Name, Bytes: -item.WorkingSet))
            .Concat(after.Applications.Select(item => (item.Name, Bytes: item.WorkingSet)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Delta: group.Sum(item => item.Bytes)))
            .Where(item => item.Delta != 0)
            .OrderByDescending(item => Math.Abs(item.Delta))
            .Take(8)
            .Select(item => new SnapshotDifference(item.Name, "", FormatGrowth(item.Delta)))
            .ToArray();
        SnapshotProcessRows.ItemsSource = deltas;
        SnapshotProcessHeading.Visibility = VisibleWhen(deltas.Length > 0);
    }
}
