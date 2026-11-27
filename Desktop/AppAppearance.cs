using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

internal static class AppAppearance
{
    private const int ImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int SystemBackdropType = 38;
    private const string WindowsPersonalization = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static Application? translationApplication;
    private static ResourceDictionary? translations;
    private static AppLanguage? translationLanguage;

    internal static bool Apply(AppPreferences preferences)
    {
        var application = Application.Current;
        var resources = application.Resources;
        if (translationApplication != application || translationLanguage != preferences.Language)
        {
            UiText.IsEnglish = preferences.Language == AppLanguage.English ||
                preferences.Language == AppLanguage.System && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru";
            var localized = new ResourceDictionary();
            foreach (string text in UiText.Keys)
                localized["UI:" + text] = UiText.Get(text);
            resources.MergedDictionaries.Add(localized);
            if (translations is not null)
                resources.MergedDictionaries.Remove(translations);
            translations = localized;
            translationApplication = application;
            translationLanguage = preferences.Language;
        }
        bool light = preferences.Theme == AppTheme.Light ||
            preferences.Theme == AppTheme.System && WindowsSettingEnabled("AppsUseLightTheme", fallback: false);

        (string day, string night, string companionDay, string companionNight) = preferences.Accent switch
        {
            AppAccent.Violet => ("#61478D", "#BFABEF", "#335E8C", "#8AB9DA"),
            AppAccent.Copper => ("#874722", "#E8A67D", "#4B6D5E", "#9DC9B2"),
            AppAccent.Blue => ("#2E5B93", "#94BFF3", "#486C72", "#8AC9C7"),
            AppAccent.Graphite => ("#425468", "#C5D4E5", "#4D665A", "#93B3A3"),
            AppAccent.Amber => ("#755116", "#F2CD7C", "#53588B", "#AEB5EE"),
            AppAccent.Berry => ("#8C4164", "#ECB0CA", "#435F8A", "#9DBEF0"),
            AppAccent.Lagoon => ("#176C78", "#88E0E6", "#566D3B", "#BBD394"),
            _ => ("#1C6856", "#A2E5C4", "#3F6492", "#88B8EB")
        };
        Color accent = Hex(light ? day : night);
        Color companion = Hex(light ? companionDay : companionNight);
        Color canvas = Tone("#EFF3F5", "#0A0E15", light ? .026 : .013);
        Color panel = Tone("#FFFFFF", "#151D29", light ? .02 : .036);
        Color line = Tone("#CCD7DF", "#303C4B", .12);
        Color raised = Tone("#EAF0F4", "#1C2632", .06);
        bool translucency = WindowsSettingEnabled("EnableTransparency", fallback: true) && !SystemParameters.HighContrast;

        Brush("Canvas", canvas);
        Brush("Panel", panel);
        Brush("Rail", Tone("#F5F8FA", "#101825", .055));
        Brush("Raised", raised);
        Brush("Line", line);
        Brush("Text", Tone("#182034", "#F0F2F9", .06));
        Brush("Muted", Tone("#495668", "#A5B4C8", .07));
        Brush("Violet", companion);
        Brush("Warning", Hex(light ? "#982E45" : "#F5A5B4"));
        Brush("Hover", Mix(raised, accent, light ? .10 : .17));
        Brush("ScrollGrip", Tone("#9BA3B4", "#566178", .25));
        Brush("Accent", accent);
        Brush("AccentText", Hex(light ? "#FFFFFF" : "#111827"));
        Brush("AccentHover", Mix(accent, Hex(light ? "#000000" : "#FFFFFF"), .13));
        Brush("Selected", Mix(panel, accent, light ? .15 : .22));
        Brush("CloseHover", Hex(light ? "#94304A" : "#81354E"));
        Brush("Scrim", Hex(light ? "#6D202938" : "#B0080C16"));
        Brush("GlassTint", WithAlpha(canvas, light ? (byte)202 : (byte)152));
        Brush("GlassCard", WithAlpha(panel, translucency ? (light ? (byte)207 : (byte)182) : (byte)255));
        Brush("GlassRail", WithAlpha(raised, translucency ? (byte)205 : (byte)255));
        Brush("BoardSurface", Tone("#EBF2F1", "#101826", light ? .065 : .085));
        Brush("CircuitTrace", WithAlpha(accent, light ? (byte)34 : (byte)37));
        Brush("HeroGrid", line);
        Brush("ArtInk", WithAlpha(accent, light ? (byte)172 : (byte)155));
        Brush("Standby", Hex(light ? "#80501F" : "#E8BA83"));
        resources["HeroStart"] = WithAlpha(Tone("#FBFCFF", "#1F3734", light ? .13 : .11), translucency ? (byte)216 : (byte)255);
        resources["HeroEnd"] = WithAlpha(panel, translucency ? (byte)190 : (byte)255);
        resources["BackdropHalo"] = WithAlpha(accent, light ? (byte)27 : (byte)37);
        resources["BackdropHaloClear"] = WithAlpha(accent, 0);
        resources["BackdropSide"] = WithAlpha(companion, light ? (byte)26 : (byte)34);
        resources["BackdropSideClear"] = WithAlpha(companion, 0);
        resources["AccentColor"] = accent;
        resources["AccentClear"] = WithAlpha(accent, 0);
        resources["UiScale"] = preferences.Density switch { AppDensity.Compact => 13.0, AppDensity.Spacious => 16.0, _ => 14.0 };
        resources["ArtworkVisibility"] = preferences.ShowHardwareArtwork ? Visibility.Visible : Visibility.Collapsed;
        resources["AnimateTransitions"] = preferences.AnimateTransitions;
        resources["HoverEffects"] = preferences.HoverEffects;
        resources["ReduceEffectsOnBattery"] = preferences.ReduceEffectsOnBattery;
        resources["CardPadding"] = new Thickness(preferences.Density switch
        {
            AppDensity.Compact => 13,
            AppDensity.Spacious => 24,
            _ => 16
        });

        return light;

        Color Tone(string lightBase, string darkBase, double tint) => Mix(Hex(light ? lightBase : darkBase), accent, tint);

        void Brush(string key, Color value)
        {
            var brush = new SolidColorBrush(value);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color from, Color to, double ratio)
    {
        byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * ratio);
        return Color.FromRgb(Blend(from.R, to.R), Blend(from.G, to.G), Blend(from.B, to.B));
    }

    internal static bool SetWindowMaterial(Window window, bool light)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            PresentationSource.FromVisual(window) is not HwndSource source)
            return false;

        int darkMode = light ? 0 : 1;
        _ = DwmSetWindowAttribute(source.Handle, ImmersiveDarkMode, in darkMode, sizeof(int));
        int rounded = 2;
        _ = DwmSetWindowAttribute(source.Handle, WindowCornerPreference, in rounded, sizeof(int));
        bool enabled = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int material = !SystemParameters.HighContrast &&
                WindowsSettingEnabled("EnableTransparency", fallback: true) ? 3 : 1;
            enabled = DwmSetWindowAttribute(source.Handle, SystemBackdropType, in material, sizeof(int)) == 0 && material == 3;
        }

        if (WindowChrome.GetWindowChrome(window) is { } chrome)
            chrome.GlassFrameThickness = enabled ? new Thickness(-1) : new Thickness(0);
        if (enabled)
        {
            window.Background = Brushes.Transparent;
            if (source.CompositionTarget is { } composition)
                composition.BackgroundColor = Colors.Transparent;
        }
        else
        {
            window.SetResourceReference(Control.BackgroundProperty, "Canvas");
        }
        return enabled;
    }

    private static bool WindowsSettingEnabled(string name, bool fallback)
    {
        try
        {
            return Registry.GetValue(WindowsPersonalization, name, null) is int value ? value != 0 : fallback;
        }
        catch (Exception ex) when (AppPreferences.IsFileError(ex))
        {
            Debug.WriteLine(ex.Message);
            return fallback;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);
}
