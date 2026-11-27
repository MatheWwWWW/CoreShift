using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CoreShift.Desktop;

internal enum AppTheme { Dark, Light, System }
internal enum AppAccent { Mint, Violet, Copper, Blue, Graphite, Amber, Berry, Lagoon }
internal enum AppBackground { Circuit, Blueprint, Plain }
internal enum AppLanguage { Russian, English, System }
internal enum AppDensity { Comfortable, Compact, Spacious }
internal enum WorkspacePage { Overview, Hardware, Memory, Cleanup, Performance, Privacy, Focus, Snapshots, Packages, Personalization }

internal sealed record AppPreferences(AppTheme Theme = AppTheme.Dark, AppAccent Accent = AppAccent.Mint,
    AppLanguage Language = AppLanguage.Russian)
{
    public AppDensity Density { get; init; } = AppDensity.Comfortable;
    public AppBackground Background { get; init; } = AppBackground.Circuit;
    public bool UseNativeInterface { get; init; }
    public WorkspacePage StartPage { get; init; } = WorkspacePage.Overview;
    public WorkspacePage LastPage { get; init; } = WorkspacePage.Overview;
    public bool RestoreLastPage { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool StartMinimized { get; init; }
    public bool CheckUpdatesAutomatically { get; init; } = true;
    public bool ShowHardwareArtwork { get; init; } = true;
    public bool OpenBoardIn3D { get; init; }
    public bool AnimateTransitions { get; init; } = true;
    public bool HoverEffects { get; init; } = true;
    public bool ReduceEffectsOnBattery { get; init; } = true;
    public bool TaskSounds { get; init; }
    public bool ShowHardwareSensors { get; init; } = true;
    public int OverviewInterval { get; init; } = 1;
    public bool CompareFocusByDefault { get; init; } = true;
    public bool UsePowerPlanByDefault { get; init; }
    public int FocusPhaseSeconds { get; init; } = 20;
    public bool SaveFocusReports { get; init; } = true;
    public int FocusReportLimit { get; init; } = 15;

    internal static AppPreferences Load(LocalState state)
    {
        try
        {
            var settings = state.Read<AppPreferences>("preferences.json");
            if (settings is not null)
                return settings.Normalize();
            if (ReferenceEquals(state, LocalState.UserProfile))
            {
                var defaults = Path.Combine(AppContext.BaseDirectory, "first-run.ini");
                if (File.Exists(defaults))
                    return ReadInstallerDefaults(File.ReadLines(defaults));
            }
        }
        catch (Exception ex) when (IsFileError(ex)) { Debug.WriteLine(ex.Message); }

        return new AppPreferences();
    }

    private static AppPreferences ReadInstallerDefaults(IEnumerable<string> lines)
    {
        var preferences = new AppPreferences();
        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');
            if (separator < 0)
                continue;
            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            preferences = key switch
            {
                "Language" when int.TryParse(value, out int language) && Enum.IsDefined((AppLanguage)language)
                    => preferences with { Language = (AppLanguage)language },
                "StartMinimized" when value is "0" or "1" => preferences with { StartMinimized = value == "1" },
                "CheckUpdates" when value is "0" or "1" => preferences with { CheckUpdatesAutomatically = value == "1" },
                "Theme" when int.TryParse(value, out int theme) && Enum.IsDefined((AppTheme)theme)
                    => preferences with { Theme = (AppTheme)theme },
                "ReduceMotion" when value is "0" or "1" => preferences with { AnimateTransitions = value == "0", HoverEffects = value == "0" },
                _ => preferences
            };
        }
        return preferences;
    }

    internal AppPreferences Normalize() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.Dark,
        Accent = Enum.IsDefined(Accent) ? Accent : AppAccent.Mint,
        Language = Enum.IsDefined(Language) ? Language : AppLanguage.Russian,
        Density = Enum.IsDefined(Density) ? Density : AppDensity.Comfortable,
        Background = Enum.IsDefined(Background) ? Background : AppBackground.Circuit,
        StartPage = Enum.IsDefined(StartPage) ? StartPage : WorkspacePage.Overview,
        LastPage = Enum.IsDefined(LastPage) ? LastPage : WorkspacePage.Overview,
        OverviewInterval = OverviewInterval is 1 or 2 or 5 ? OverviewInterval : 1,
        FocusPhaseSeconds = FocusPhaseSeconds is 10 or 20 or 30 or 60 ? FocusPhaseSeconds : 20,
        FocusReportLimit = FocusReportLimit is 5 or 15 or 30 ? FocusReportLimit : 15
    };

    internal void Save(LocalState state) => state.Write("preferences.json", this);

    internal static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException;
}

internal sealed class LocalState(string directory)
{
    internal string DirectoryPath => directory;
    internal static LocalState UserProfile { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreShift"));

    internal T? Read<T>(string name) where T : class
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, name));
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal void Write<T>(string name, T value)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
                JsonSerializer.Serialize(stream, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
