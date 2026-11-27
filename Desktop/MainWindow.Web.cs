using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string WebOrigin = "https://coreshift.invalid";
    private const string WebEntry = WebOrigin + "/index.html";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private WebView2? webView;
    private WebControlProjection? webProjection;
    private SettingsWindow? webSettings;
    private DispatcherTimer? webTimer;
    private DispatcherTimer? webReleaseTimer;
    private string? lastWebState;
    private bool preparingWeb;
    private bool webReady;
    private bool webFailed;
    private bool webRequested;
    private string? webScrollTarget;
    private DispatcherFrame? webConfirmationFrame;
    private WebConfirmation? webConfirmation;
    private bool webConfirmed;
    private int webConfirmationId;
    private SearchAction[] webSearchActions = [];

    private sealed record WebRequest(string? Op, string? Id, JsonElement Value);
    private sealed record WebDisplay(string Id, string Title, string Group);
    private sealed record WebConfirmation(int Id, string Title, string Message, string Action, string Cancel);

    internal void EnableWebInterface()
    {
        webRequested = true;
        Loaded += (_, _) => PrepareWebWhenVisible();
        IsVisibleChanged += (_, _) => SyncWebVisibility();
        StateChanged += (_, _) => SyncWebVisibility();
    }

    private bool WebForeground => IsLoaded && IsVisible && WindowState != WindowState.Minimized && !closing;

    private void SyncWebVisibility()
    {
        if (!WebForeground)
        {
            webTimer?.Stop();
            CompleteWebConfirmation(false);
            SetWebMemoryTarget(CoreWebView2MemoryUsageTargetLevel.Low);
            if (webReady && !closing)
            {
                if (webReleaseTimer is null)
                {
                    webReleaseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
                    webReleaseTimer.Tick += (_, _) =>
                    {
                        webReleaseTimer.Stop();
                        if (!WebForeground && !closing)
                            RestoreNativeSurface();
                    };
                }
                webReleaseTimer.Start();
            }
            return;
        }
        webReleaseTimer?.Stop();
        PrepareWebWhenVisible();
        if (webReady)
        {
            SetWebMemoryTarget(CoreWebView2MemoryUsageTargetLevel.Normal);
            webTimer?.Start();
            SendWebState();
        }
    }

    private void SetWebMemoryTarget(CoreWebView2MemoryUsageTargetLevel level)
    {
        if (!webReady || webView?.CoreWebView2 is not { } core)
            return;
        try { core.MemoryUsageTargetLevel = level; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotImplementedException)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private async void PrepareWebWhenVisible()
    {
        if (!webRequested || !WebForeground || preparingWeb || webReady || webFailed)
            return;
        preparingWeb = true;
        try
        {
            // This uses the installed Edge runtime with a profile dedicated to this frontend.
            string profile = Path.Combine(localState.DirectoryPath, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (closing)
                return;
            var view = new WebView2
            {
                DefaultBackgroundColor = lightTheme ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(15, 20, 23)
            };
            webView = view;
            WebHost.Child = view;
            WebHost.Visibility = Visibility.Visible;
            await view.EnsureCoreWebView2Async(environment);
            if (closing || !ReferenceEquals(webView, view))
                return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != WebEntry;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += ServeWebAsset;
            core.WebMessageReceived += ReceiveWebMessage;
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                    _ = Dispatcher.BeginInvoke(() => { if (ReferenceEquals(webView, view)) FallBackToNative(); });
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                    _ = Dispatcher.BeginInvoke(() => { if (ReferenceEquals(webView, view)) FallBackToNative(); });
            };
            webProjection = new WebControlProjection(this);
            core.Navigate(WebEntry);
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (!webReady && !closing && ReferenceEquals(webView, view))
                FallBackToNative();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or
            InvalidOperationException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        {
            Debug.WriteLine(ex);
            FallBackToNative();
        }
        finally { preparingWeb = false; }
    }

    private void ServeWebAsset(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = webView?.CoreWebView2?.Environment;
        if (environment is null)
            return;
        var (file, mime) = e.Request.Uri switch
        {
            WebEntry => ("index.html", "text/html; charset=utf-8"),
            WebOrigin + "/style.css" => ("style.css", "text/css; charset=utf-8"),
            WebOrigin + "/app.js" => ("app.js", "text/javascript; charset=utf-8"),
            WebOrigin + "/board.js" => ("board.js", "text/javascript; charset=utf-8"),
            _ => (null, null)
        };
        if (file is null || e.Request.Method != "GET" ||
            typeof(MainWindow).Assembly.GetManifestResourceStream("CoreShift.Web." + file) is not { } source)
        {
            e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            return;
        }
        using (source)
        {
            var body = new MemoryStream();
            source.CopyTo(body);
            body.Position = 0;
            const string csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
            e.Response = environment.CreateWebResourceResponse(body, 200, "OK",
                $"Content-Type: {mime}\r\nContent-Security-Policy: {csp}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store");
        }
    }

    private void ReceiveWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != WebEntry || closing || e.WebMessageAsJson.Length > 8192)
            return;
        try
        {
            var request = JsonSerializer.Deserialize<WebRequest>(e.WebMessageAsJson, WebJson);
            if (request?.Op is { } operation)
                _ = Dispatcher.BeginInvoke(() => DispatchWebRequest(operation, request), DispatcherPriority.Input);
        }
        catch (JsonException ex) { Debug.WriteLine(ex.Message); }
    }

    private void DispatchWebRequest(string operation, WebRequest request)
    {
        if (closing || webView is null || webFailed)
            return;
        if (!webReady)
        {
            if (operation != "ready")
                return;
            webReady = true;
            WindowSurface.Opacity = 0;
            WindowSurface.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(WindowSurface, KeyboardNavigationMode.None);
            WindowChrome.GetWindowChrome(this)?.SetCurrentValue(WindowChrome.CaptionHeightProperty, 0d);
            ReleaseBoardView();
            webTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(850) };
            webTimer.Tick += (_, _) => SendWebState();
            SyncWebVisibility();
            webView.Focus();
            return;
        }

        if (webConfirmation is { } confirmation)
        {
            if (operation == "confirm" && request.Id == confirmation.Id.ToString(CultureInfo.InvariantCulture) &&
                request.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                CompleteWebConfirmation(request.Value.GetBoolean());
            return;
        }

        try
        {
            switch (operation)
            {
                case "navigate" when TryWebIndex(request.Value, SectionButtons.Length, out int page):
                    webSettings = null;
                    SectionButtons[page].IsChecked = true;
                    break;
                case "control" when request.Id is { Length: <= 196 } id:
                    webProjection?.Invoke(id, request.Value);
                    break;
                case "settings":
                    webSettings ??= new SettingsWindow(this, preferences, projected: true);
                    break;
                case "assistant":
                    OpenAssistant();
                    break;
                case "settingsClose":
                    webSettings = null;
                    break;
                case "settingsSection" when webSettings is { } settings &&
                    TryWebIndex(request.Value, settings.WebSectionNames.Length, out int section):
                    settings.SelectWebSection(section);
                    break;
                case "search" when TryWebIndex(request.Value, webSearchActions.Length, out int result):
                    webSettings = null;
                    webSearchActions[result].Execute();
                    break;
                case "refresh" when OverviewNav.IsChecked == true && RefreshComponentsButton.IsEnabled:
                    RefreshComponentsClick(RefreshComponentsButton, new RoutedEventArgs());
                    break;
                case "part" when OverviewNav.IsChecked == true && TryWebIndex(request.Value, 4, out int part):
                    SelectBoardPart((BoardPart)part);
                    break;
                case "boardDrive" when OverviewNav.IsChecked == true && componentInventory is { } inventory &&
                    TryWebIndex(request.Value, inventory.Drives.Length, out int drive):
                    SelectBoardDrive(inventory.Drives[drive]);
                    break;
                case "partDetails" when OverviewNav.IsChecked == true:
                    OpenBoardDetailsClick(this, new RoutedEventArgs());
                    break;
                case "minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "maximize":
                    MaximizeClick(this, new RoutedEventArgs());
                    break;
                case "drag" when Mouse.LeftButton == MouseButtonState.Pressed && WindowState == WindowState.Normal:
                    DragMove();
                    break;
                case "close":
                    Close();
                    break;
            }
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
        }
        SendWebState();
    }

    private void SendWebState()
    {
        if (webProjection is not { } projection || webView?.CoreWebView2 is not { } core || !webReady || !WebForeground)
            return;
        int page = Math.Max(0, Array.FindIndex(SectionButtons, button => button.IsChecked == true));
        FrameworkElement[] panels = [OverviewPage, PcInfoPage, MemoryPage, CleanPage, TweaksPage, PrivacyPage,
            ProfilePage, SnapshotsPage, PackagesPage, PersonalizationPage];
        projection.Begin();
        webSearchActions = SearchActions();
        var body = page switch
        {
            0 => projection.Project("overview", OverviewDetails),
            1 => ProjectPcInfo(projection),
            _ => projection.Project($"page{page}", panels[page])
        };
        var palette = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in new[] { "Canvas", "Panel", "Raised", "Line", "Text", "Muted", "Accent",
            "AccentText", "Selected", "Hover", "Warning", "Violet", "BoardSurface", "Standby" })
        {
            if (TryFindResource(key) is SolidColorBrush brush)
                palette[key.ToLowerInvariant()] = $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";
        }
        var labels = new[]
        {
            "Обзор системы", "Настройки", "Шифтер", "Поиск разделов и действий", "Поисковый запрос", "Совпадений нет",
            "Свернуть", "Развернуть", "Закрыть", "Оперативная память", "Загрузка процессора", "Свободно на диске Windows",
            "Процессор", "Видеокарта", "Память", "Накопитель", "Подробнее", "Обновить", "До и после",
            "Схема компьютера", "Открыть 3D", "Вернуться к 2D", "Сбросить вид", "Последняя минута", "Нагрузка и ресурсы",
            "Перетащите для поворота, прокрутите для масштаба.", "Выберите компонент, чтобы увидеть показания."
        }.ToDictionary(key => key, UiText.Get, StringComparer.Ordinal);
        string wire = JsonSerializer.Serialize(new
        {
            page, body, palette, labels, scrollTarget = webScrollTarget,
            confirmation = webConfirmation,
            english = UiText.IsEnglish, light = lightTheme, density = (int)preferences.Density,
            background = (int)preferences.Background,
            glass = !SystemParameters.HighContrast,
            animation = UiMotion.CanAnimate(WindowSurface), hover = preferences.HoverEffects && UiMotion.CanAnimate(WindowSurface),
            artwork = preferences.ShowHardwareArtwork, initial3D = preferences.OpenBoardIn3D,
            navigation = SectionButtons.Select((button, index) => new WebDisplay(index.ToString(CultureInfo.InvariantCulture),
                button.Content?.ToString() ?? "", index switch { 0 or 1 or 2 => UiText.Get("Система"),
                    6 or 7 => UiText.Get("Сеансы"), _ => UiText.Get("Обслуживание") })).ToArray(),
            search = webSearchActions.Select(action => new { action.Title, action.Description, action.Category, action.Keywords }),
            banner = projection.Project("update", UpdateBanner),
            settings = webSettings is not { } settings ? null : new
            {
                sections = settings.WebSectionNames, index = settings.WebSectionIndex,
                body = projection.Project($"settings{settings.WebSectionIndex}", settings.WebSection, settings.WebStatus)
            },
            overview = page != 0 ? null : new
            {
                host = HostNameText.Text, windows = ComponentWindows.Text, refresh = RefreshStatus.Text, interval = preferences.OverviewInterval,
                warning = lastOverview?.HasErrors == true,
                cpu = CpuLoadText.Text, ram = RamPercent.Text, ramDetail = RamUsage.Text,
                disk = SystemDiskFreeText.Text, diskDetail = SystemDiskVolumeText.Text, diskFill = SystemDiskMeter.Value,
                cpuHistory = cpuHistory.Select(value => Math.Round(value, 1)), ramHistory = ramHistory.Select(value => Math.Round(value, 1)),
                part = (int)boardSelection, partTitle = BoardSelectionLabel.Text, partName = BoardSelectionName.Text,
                drives = componentInventory?.Drives,
                driveIndex = componentInventory is { } hardware ? Math.Max(0, Array.FindIndex(hardware.Drives, item => item.Number == CurrentBoardDrive?.Number)) : 0,
                partValue = BoardSelectionReading.Text, partDetail = BoardSelectionDetail.Text,
                components = new[] { componentInventory?.Cpu, dashboardGraphics?.Name, RamPercent.Text, SystemDiskFreeText.Text },
                focus = OverviewFocusButton.Content?.ToString()
            }
        }, WebJson);
        if (wire != lastWebState)
        {
            lastWebState = wire;
            try { core.PostWebMessageAsJson(wire); }
            catch (InvalidOperationException ex) { Debug.WriteLine(ex); }
        }
        webScrollTarget = null;
    }

    private static bool TryWebIndex(JsonElement value, int count, out int index)
    {
        index = -1;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out index) && index >= 0 && index < count;
    }

    internal bool Confirm(string title, string message, string action)
    {
        if (!webReady || !WebForeground)
            return new ConfirmDialog(title, message, action) { Owner = this }.ShowDialog() == true;
        if (webConfirmationFrame is not null)
            return false;

        // The existing operations expect a synchronous choice. Web messages are dispatched outside the COM callback.
        var frame = new DispatcherFrame();
        webConfirmationFrame = frame;
        webConfirmation = new(++webConfirmationId, title, message, action, UiText.Get("Отмена"));
        webConfirmed = false;
        try
        {
            SendWebState();
            Dispatcher.PushFrame(frame);
            return webConfirmed && !closing;
        }
        finally
        {
            webConfirmation = null;
            webConfirmationFrame = null;
            SendWebState();
        }
    }

    private void CompleteWebConfirmation(bool confirmed)
    {
        if (webConfirmationFrame is not { } frame)
            return;
        webConfirmed = confirmed;
        frame.Continue = false;
    }

    private void FallBackToNative()
    {
        if (closing || webFailed)
            return;
        webFailed = true;
        RestoreNativeSurface();
    }

    private void RestoreNativeSurface()
    {
        DisposeWebInterface();
        WebHost.Visibility = Visibility.Collapsed;
        WindowSurface.Opacity = 1;
        WindowSurface.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(WindowSurface, KeyboardNavigationMode.Continue);
        WindowChrome.GetWindowChrome(this)?.SetCurrentValue(WindowChrome.CaptionHeightProperty, 64d);
        UpdateBoardView();
    }

    private void DisposeWebInterface()
    {
        CompleteWebConfirmation(false);
        webReleaseTimer?.Stop();
        webTimer?.Stop();
        webTimer = null;
        webProjection = null;
        webSettings = null;
        webReady = false;
        lastWebState = null;
        var view = webView;
        webView = null;
        WebHost.Child = null;
        view?.Dispose();
    }
}
