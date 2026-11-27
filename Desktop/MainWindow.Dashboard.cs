using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private BoardPart boardSelection;
    private BoardViewport? boardViewport;
    private bool boardRequested;
    private (Color Accent, bool Light)? boardPalette;
    private (string Name, float? Load, float? Temperature, DateTimeOffset ReadAt)? gpuReading;
    private GraphicsInfo? dashboardGraphics;
    private bool readingBoardGpu;
    private long lastGpuProbe;

    private void ToggleBoardClick(object sender, RoutedEventArgs e)
    {
        boardRequested = !boardRequested;
        UpdateBoardView();
        FrameworkElement focus = boardRequested ? BoardResetButton : BoardModeToggle;
        focus.Focus();
    }

    private void ResetBoardClick(object sender, RoutedEventArgs e) => boardViewport?.ResetView();

    private void UpdateBoardView()
    {
        bool allowed = preferences.ShowHardwareArtwork;
        bool show = allowed && boardRequested;
        BoardModeToggle.Visibility = VisibleWhen(allowed);
        BoardResetButton.Visibility = BoardHost.Visibility = BoardKeys.Visibility = VisibleWhen(show);
        BoardPlan.Visibility = VisibleWhen(!show);
        BoardModeToggle.SetResourceReference(ContentControl.ContentProperty, show ? "UI:Вернуться к 2D" : "UI:Открыть 3D");
        BoardHint.SetResourceReference(TextBlock.TextProperty, show
            ? "UI:Перетащите для поворота, прокрутите для масштаба."
            : "UI:Выберите компонент, чтобы увидеть показания.");

        if (!show || webReady || !initialized || closing || !IsVisible || !OverviewPage.IsVisible || WindowState == WindowState.Minimized)
        {
            ReleaseBoardView();
            return;
        }

        var palette = (Accent: (Color)FindResource("AccentColor"), Light: lightTheme);
        if (boardViewport is not null && boardPalette == palette)
            return;
        ReleaseBoardView();
        boardViewport = new BoardViewport(palette.Accent, palette.Light, boardSelection);
        boardViewport.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,
            "UI:Интерактивная схема компьютера");
        boardViewport.SetResourceReference(System.Windows.Automation.AutomationProperties.HelpTextProperty,
            "UI:Стрелки поворачивают схему, плюс и минус меняют масштаб, Home сбрасывает вид.");
        boardViewport.PartSelected += SelectBoardPart;
        boardPalette = palette;
        BoardHost.Child = boardViewport;
    }

    private void ReleaseBoardView()
    {
        if (boardViewport is not { } view)
            return;
        view.StopEffects();
        view.ReleaseMouseCapture();
        view.PartSelected -= SelectBoardPart;
        BoardHost.Child = null;
        boardViewport = null;
        boardPalette = null;
    }

    private void BoardPartChecked(object sender, RoutedEventArgs e)
    {
        if (BoardSelectionLabel is not null && sender is RadioButton { Tag: string id } &&
            Enum.TryParse<BoardPart>(id, out var part) && part != boardSelection)
            SelectBoardPart(part);
    }

    private void BoardKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && Enum.TryParse<BoardPart>(id, out var part))
            SelectBoardPart(part);
    }

    private uint? boardDriveNumber;

    private void SelectBoardDrive(BoardDrive drive)
    {
        boardDriveNumber = drive.Number;
        SelectBoardPart(BoardPart.Storage);
    }

    private BoardDrive? CurrentBoardDrive => componentInventory?.Drives.FirstOrDefault(drive => drive.Number == boardDriveNumber)
        ?? componentInventory?.Drives.FirstOrDefault();

    private void SelectBoardPart(BoardPart part)
    {
        bool changed = boardSelection != part;
        boardSelection = part;
        boardViewport?.Select(part);
        (part switch
        {
            BoardPart.Graphics => BoardGraphics,
            BoardPart.Memory => BoardMemory,
            BoardPart.Storage => BoardStorage,
            _ => BoardCpu
        }).IsChecked = true;
        UpdateBoardReadings();
        if (changed && !webReady)
            UiMotion.Enter(BoardSelectionReading);
        if (part == BoardPart.Graphics)
            _ = RefreshBoardGpuAsync();
    }

    private void OpenBoardDetailsClick(object sender, RoutedEventArgs e)
    {
        if (boardSelection == BoardPart.Memory)
            MemoryNav.IsChecked = true;
        else
            PcInfoNav.IsChecked = true;
    }

    private void UpdateBoardReadings()
    {
        if (BoardSelectionLabel is null)
            return;
        var data = componentInventory;
        var (label, name, metric, detail) = boardSelection switch
        {
            BoardPart.Graphics => GraphicsReadings(),
            BoardPart.Memory => ("Оперативная память", RamUsage.Text, RamPercent.Text, RamAvailable.Text),
            BoardPart.Storage when CurrentBoardDrive is { } drive => (drive.System ? "Системный диск" : "Накопитель", drive.Name,
                drive.Capacity, drive.Volumes),
            BoardPart.Storage => ("Системный диск", SystemDiskVolumeText.Text, SystemDiskFreeText.Text, UiText.Get("Свободно на диске Windows")),
            _ => ("Процессор", data?.Cpu, CpuLoadText.Text,
                data is { Cores: { } cores, Threads: { } threads } ? UiText.Format($"{cores} ядер · {threads} потоков") : null)
        };
        SetChanged(BoardSelectionLabel, UiText.Get(label));
        SetChanged(BoardSelectionName, string.IsNullOrWhiteSpace(name) ? "—" : name);
        SetChanged(BoardSelectionReading, metric);
        SetChanged(BoardSelectionDetail, detail ?? "");
    }

    private (string, string?, string, string?) GraphicsReadings()
    {
        string memory = FormatBytes(dashboardGraphics?.VideoMemory);
        if (dashboardGraphics?.Name is not { } name || !preferences.ShowHardwareSensors)
            return ("Видеокарта", dashboardGraphics?.Name, memory,
                readingBoardGpu ? UiText.Get("Получаем показания видеокарты…") : null);
        if (gpuReading is { } sample && sample.Name == name)
        {
            string? readings = JoinDetails(sample.Load is { } load ? UiText.Format($"{load:0}% загрузки") : null,
                sample.Temperature is { } temperature ? $"{temperature:0} °C" : null);
            return ("Видеокарта", name, memory, readings is not null
                ? $"{readings} · {sample.ReadAt.ToLocalTime():HH:mm:ss}" : UiText.Get("Данные датчиков недоступны"));
        }
        return ("Видеокарта", name, memory, UiText.Get("Получаем показания видеокарты…"));
    }

    private async Task RefreshBoardGpuAsync(bool force = false)
    {
        if (closing || readingBoardGpu || boardSelection != BoardPart.Graphics ||
            !OverviewPage.IsVisible || WindowState == WindowState.Minimized)
            return;
        if (!force && (dashboardGraphics is not null && !preferences.ShowHardwareSensors ||
            lastGpuProbe != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastGpuProbe) < TimeSpan.FromSeconds(15)))
            return;

        readingBoardGpu = true;
        lastGpuProbe = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateBoardReadings();
        try
        {
            if (force || dashboardGraphics is null)
                dashboardGraphics = await RunInBackground(() => ReadGraphicsInfo().OrderByDescending(adapter => adapter.VideoMemory).FirstOrDefault());
            if (!closing && preferences.ShowHardwareSensors && boardSelection == BoardPart.Graphics &&
                OverviewPage.IsVisible && WindowState != WindowState.Minimized && dashboardGraphics?.Name is { } name)
            {
                var (load, temperature) = await RunInBackground(() => ReadDashboardGpu(name));
                gpuReading = (name, load, temperature, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            readingBoardGpu = false;
            if (!closing && OverviewPage.IsVisible && boardSelection == BoardPart.Graphics)
                UpdateBoardReadings();
        }
    }

    private static void SetChanged(TextBlock element, string text)
    {
        if (element.Text != text)
            element.Text = text;
    }
}
