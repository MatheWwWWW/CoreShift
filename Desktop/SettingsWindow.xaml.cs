using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class SettingsWindow : Window
{
    private static readonly int[] Intervals = [1, 2, 5];
    private static readonly int[] PhaseSeconds = [10, 20, 30, 60];
    private static readonly int[] ReportLimits = [5, 15, 30];

    private readonly MainWindow workspace;
    private readonly bool projected;
    private AppPreferences preferences;
    private bool loading;

    internal SettingsWindow(MainWindow workspace, AppPreferences preferences, bool projected = false)
    {
        this.workspace = workspace;
        this.projected = projected;
        this.preferences = preferences;
        InitializeComponent();
        LoadControls();
        SectionPicker.SelectedIndex = 0;
        SourceInitialized += (_, _) => UpdateMaterial(workspace.IsLightTheme);
        Loaded += (_, _) => UiMotion.Enter(SettingsScroll);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private FrameworkElement[] Sections => [AppearancePanel, EffectsPanel, WorkspacePanel, FocusPanel, SystemPanel, DataPanel];
    private Window DialogOwner => projected ? workspace : this;
    internal string[] WebSectionNames => SectionPicker.Items.OfType<ListBoxItem>().Select(item => item.Content?.ToString() ?? "").ToArray();
    internal int WebSectionIndex => Math.Max(0, SectionPicker.SelectedIndex);
    internal FrameworkElement WebSection => Sections[WebSectionIndex];
    internal FrameworkElement WebStatus => Status;
    internal void SelectWebSection(int index) => SectionPicker.SelectedIndex = index;

    private void LoadControls()
    {
        loading = true;
        ThemePicker.SelectedIndex = (int)preferences.Theme;
        PalettePicker.SelectedIndex = (int)preferences.Accent;
        LanguagePicker.SelectedIndex = (int)preferences.Language;
        DensityPicker.SelectedIndex = (int)preferences.Density;
        BackgroundPicker.SelectedIndex = (int)preferences.Background;
        NativeInterfaceToggle.IsChecked = preferences.UseNativeInterface;
        ArtworkToggle.IsChecked = preferences.ShowHardwareArtwork;
        BoardOnOverviewToggle.IsChecked = preferences.OpenBoardIn3D;
        BoardOnOverviewToggle.IsEnabled = preferences.ShowHardwareArtwork;
        MotionToggle.IsChecked = preferences.AnimateTransitions;
        HoverToggle.IsChecked = preferences.HoverEffects;
        ReducedEffectsToggle.IsChecked = preferences.ReduceEffectsOnBattery;
        TaskSoundToggle.IsChecked = preferences.TaskSounds;
        SensorsToggle.IsChecked = preferences.ShowHardwareSensors;
        StartPagePicker.SelectedIndex = (int)preferences.StartPage;
        IntervalPicker.SelectedIndex = Array.IndexOf(Intervals, preferences.OverviewInterval);
        RestorePageToggle.IsChecked = preferences.RestoreLastPage;
        StartPagePicker.IsEnabled = !preferences.RestoreLastPage;
        TrayToggle.IsChecked = preferences.CloseToTray;
        MinimizedToggle.IsChecked = preferences.StartMinimized;
        AutoUpdateToggle.IsChecked = preferences.CheckUpdatesAutomatically;
        CheckUpdateButton.IsEnabled = AppUpdater.IsInstalledCopy();
        UpdateAvailability.Visibility = AppUpdater.IsInstalledCopy() ? Visibility.Collapsed : Visibility.Visible;
        DurationPicker.SelectedIndex = Array.IndexOf(PhaseSeconds, preferences.FocusPhaseSeconds);
        ReportLimitPicker.SelectedIndex = Array.IndexOf(ReportLimits, preferences.FocusReportLimit);
        FocusCompareToggle.IsChecked = preferences.CompareFocusByDefault;
        FocusPowerToggle.IsChecked = preferences.UsePowerPlanByDefault;
        FocusSaveToggle.IsChecked = preferences.SaveFocusReports;
        ReportLimitPicker.IsEnabled = preferences.SaveFocusReports;
        VersionText.Text = UiText.Format($"Версия {AppUpdater.CurrentVersion.ToString(3)}");
        loading = false;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (loading || !IsLoaded && !projected || IntervalPicker.SelectedIndex < 0 || DurationPicker.SelectedIndex < 0 ||
            ReportLimitPicker.SelectedIndex < 0 || ThemePicker.SelectedIndex < 0 || PalettePicker.SelectedIndex < 0 ||
            LanguagePicker.SelectedIndex < 0 || DensityPicker.SelectedIndex < 0 || BackgroundPicker.SelectedIndex < 0 ||
            StartPagePicker.SelectedIndex < 0)
            return;

        Commit(preferences with
        {
            Theme = (AppTheme)ThemePicker.SelectedIndex,
            Accent = (AppAccent)PalettePicker.SelectedIndex,
            Language = (AppLanguage)LanguagePicker.SelectedIndex,
            Density = (AppDensity)DensityPicker.SelectedIndex,
            Background = (AppBackground)BackgroundPicker.SelectedIndex,
            UseNativeInterface = NativeInterfaceToggle.IsChecked == true,
            ShowHardwareArtwork = ArtworkToggle.IsChecked == true,
            OpenBoardIn3D = BoardOnOverviewToggle.IsChecked == true,
            AnimateTransitions = MotionToggle.IsChecked == true,
            HoverEffects = HoverToggle.IsChecked == true,
            ReduceEffectsOnBattery = ReducedEffectsToggle.IsChecked == true,
            TaskSounds = TaskSoundToggle.IsChecked == true,
            ShowHardwareSensors = SensorsToggle.IsChecked == true,
            StartPage = (WorkspacePage)StartPagePicker.SelectedIndex,
            RestoreLastPage = RestorePageToggle.IsChecked == true,
            CloseToTray = TrayToggle.IsChecked == true,
            StartMinimized = MinimizedToggle.IsChecked == true,
            CheckUpdatesAutomatically = AutoUpdateToggle.IsChecked == true,
            OverviewInterval = Intervals[IntervalPicker.SelectedIndex],
            CompareFocusByDefault = FocusCompareToggle.IsChecked == true,
            UsePowerPlanByDefault = FocusPowerToggle.IsChecked == true,
            FocusPhaseSeconds = PhaseSeconds[DurationPicker.SelectedIndex],
            SaveFocusReports = FocusSaveToggle.IsChecked == true,
            FocusReportLimit = ReportLimits[ReportLimitPicker.SelectedIndex]
        });
    }

    private bool Commit(AppPreferences next)
    {
        bool saved = false;
        try
        {
            workspace.SavePreferences(next);
            preferences = next;
            Status.Visibility = Visibility.Collapsed;
            saved = true;
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            ShowError(UiText.Get("Не удалось сохранить настройки."));
        }
        LoadControls();
        UpdateMaterial(workspace.IsLightTheme);
        return saved;
    }

    internal void UpdateMaterial(bool light)
    {
        bool glass = AppAppearance.SetWindowMaterial(this, light);
        Surface.SetResourceReference(Panel.BackgroundProperty, glass ? "GlassTint" : "Canvas");
    }

    private void SectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsScroll is null)
            return;

        FrameworkElement[] panels = Sections;
        for (int i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == SectionPicker.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ScrollToTop();
        if (IsLoaded && SectionPicker.SelectedIndex >= 0)
            UiMotion.Enter(panels[SectionPicker.SelectedIndex]);
    }

    private void ToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tool })
            OpenTarget(tool.StartsWith("ms-settings:", StringComparison.Ordinal) ? tool : Path.Combine(Environment.SystemDirectory, tool));
    }

    private void OpenDataClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(workspace.StateDirectory);
            OpenTarget(workspace.StateDirectory);
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            ShowError(UiText.Format($"Не удалось открыть инструмент Windows. {ex.Message}"));
        }
    }

    private void OpenTarget(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            ShowError(UiText.Format($"Не удалось открыть инструмент Windows. {ex.Message}"));
        }
    }

    private void ReleaseNotesClick(object sender, RoutedEventArgs e) => new ReleaseNotesWindow { Owner = DialogOwner }.ShowDialog();

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            ShowStatus(UiText.Get("Проверяем обновления…"));
            ShowStatus(UiText.Get(await workspace.CheckForUpdatesAsync()));
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CoreShift (*.json)|*.json",
            FileName = "CoreShift-settings.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(DialogOwner) != true)
            return;

        try
        {
            StateFor(dialog.FileName).Write(Path.GetFileName(dialog.FileName), preferences);
            ShowStatus(UiText.Get("Настройки экспортированы."));
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            ShowError(UiText.Get("Не удалось экспортировать настройки."));
        }
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CoreShift (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(DialogOwner) != true)
            return;

        try
        {
            if (new FileInfo(dialog.FileName).Length > 512 * 1024 ||
                StateFor(dialog.FileName).Read<AppPreferences>(Path.GetFileName(dialog.FileName)) is not { } imported)
            {
                ShowError(UiText.Get("Не удалось импортировать настройки."));
                return;
            }

            if (Confirm(UiText.Get("Импортировать настройки"),
                UiText.Get("Заменить текущие настройки CoreShift выбранным файлом?"), UiText.Get("Применить")) && Commit(imported.Normalize()))
                ShowStatus(UiText.Get("Настройки импортированы."));
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex);
            ShowError(UiText.Get("Не удалось импортировать настройки."));
        }
    }

    private static LocalState StateFor(string fileName) => new(Path.GetDirectoryName(fileName)!);

    private void PlayTaskSoundClick(object sender, RoutedEventArgs e)
    {
        if (!CompletionSound.Play())
            ShowError(UiText.Get("Не удалось воспроизвести сигнал CoreShift."));
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (Confirm(UiText.Get("Сбросить настройки CoreShift"),
            UiText.Get("Восстановить стандартные настройки интерфейса и профиля? Снимки и отчёты сохранятся."),
            UiText.Get("Восстановить")))
            Commit(new AppPreferences());
    }

    private bool Confirm(string title, string message, string action) => projected
        ? workspace.Confirm(title, message, action)
        : new ConfirmDialog(title, message, action) { Owner = this }.ShowDialog() == true;

    private void ShowError(string message) => ShowStatus(message, error: true);

    private void ShowStatus(string message, bool error = false)
    {
        Status.Text = message;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Warning" : "Muted");
        Status.Visibility = Visibility.Visible;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
