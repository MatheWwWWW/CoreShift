using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private sealed record SettingChoice(WindowsTweak Tweak, int OnValue, string? SupportIssue = null);
    private sealed record SettingRow(SettingChoice Choice, string Name, string Detail, string Status,
        bool CanEnable, bool CanDisable, bool CanRestore);
    private sealed record WallpaperBackup(string Original, string Applied);

    private static SettingChoice UserSetting(string id, string name, string detail, string key, string property, int on = 1,
        string? supportIssue = null) => new(new WindowsTweak(id, name, "", detail, RegistryHive.CurrentUser,
            key, property, on, Configurable: true, MinimumValue: 0, MaximumValue: 1), on, supportIssue);

    private static SettingChoice[] AppearanceChoices =>
    [
        UserSetting("apps-dark-mode", "Тёмная тема приложений Windows", "Приложения, использующие системную тему, сменят оформление.", ThemeKey, "AppsUseLightTheme", 0),
        UserSetting("system-dark-mode", "Тёмная тема оболочки Windows", "Меняет оформление меню «Пуск» и панели задач.", ThemeKey, "SystemUsesLightTheme", 0),
        UserSetting("system-transparency", "Прозрачность Windows", "Меняет прозрачность элементов оболочки для текущего пользователя.", ThemeKey, "EnableTransparency")
    ];

    private static SettingChoice[] TaskbarChoices =>
    [
        UserSetting("taskbar-end-task", "Завершить задачу на панели задач", "Добавляет команду принудительного завершения приложения в меню его значка.",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask",
            supportIssue: OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22631) ? null : "Требуется Windows 11 версии 23H2 или новее.")
    ];

    private static SettingChoice[] ProtectionChoices =>
    [
        new(new WindowsTweak("windows-uac", "Контроль учётных записей (UAC)", "",
                "Отключение убирает запросы повышения прав и ослабляет защиту. Для вступления в силу нужна перезагрузка.",
                RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1,
                Configurable: true, MinimumValue: 0, MaximumValue: 1), 1),
        new(new WindowsTweak("explorer-smartscreen", "SmartScreen для приложений и файлов", "",
                "Отключение снимает проверку репутации скачанных файлов в оболочке Windows. Настройки Edge применяются отдельно.",
                RegistryHive.LocalMachine, @"Software\Policies\Microsoft\Windows\System", "EnableSmartScreen", 1,
                Configurable: true, MinimumValue: 0, MaximumValue: 1), 1)
    ];

    private bool changingWindowsSetting;

    private void LoadWindowsSettings()
    {
        var groups = PrivacyNav.IsChecked == true
            ? new[] { (WindowsProtectionItems, ProtectionChoices) }
            : new[] { (WindowsAppearanceItems, AppearanceChoices), (WindowsTaskbarItems, TaskbarChoices) };
        foreach (var (target, choices) in groups)
            target.ItemsSource = choices.Select(ReadChoice).ToArray();
        if (PersonalizationNav.IsChecked == true)
        {
            WallpaperRestoreButton.IsEnabled = ReadWallpaperBackup() is not null;
            try { StickyShortcutState.Text = UiText.Get((ReadSticky().Flags & 4) != 0 ? "Включено" : "Отключено"); }
            catch (Win32Exception ex) { StickyShortcutState.Text = ex.Message; }
        }
    }

    private static SettingRow ReadChoice(SettingChoice choice)
    {
        var tweak = choice.Tweak;
        string? issue = choice.SupportIssue;
        int? value = null;
        bool backup = false;
        try
        {
            (value, backup) = TweaksEngine.ReadSetting(tweak);
            if (tweak.Id == "explorer-smartscreen") issue ??= TweaksEngine.SupportIssue(tweak);
        }
        catch (Exception ex) when (IsSystemError(ex)) { issue = ex.Message; }
        string status = issue ?? UiText.Get(value == choice.OnValue ? "Включено" : value == 1 - choice.OnValue ? "Отключено" : "Настройка управляется Windows");
        return new(choice, UiText.Get(tweak.Name), UiText.Get(tweak.Risk), status,
            issue is null && value != choice.OnValue, issue is null && value != 1 - choice.OnValue, backup);
    }

    private async void ChangeWindowsSettingClick(object sender, RoutedEventArgs e)
    {
        if (closing || changingWindowsSetting || sender is not Button { Tag: SettingRow row } button)
            return;
        string operation = AutomationOperation(button);
        bool restore = operation == "restore";
        bool enable = operation == "enable";
        if (restore ? !row.CanRestore : enable ? !row.CanEnable : !row.CanDisable)
            return;
        string action = UiText.Get(restore ? "Восстановить" : enable ? "Включить" : "Отключить");
        if (!Confirm(row.Name, row.Detail, action)) return;

        var status = PrivacyNav.IsChecked == true ? WindowsProtectionStatus : WindowsAppearanceStatus;
        changingWindowsSetting = true;
        try
        {
            var tweak = row.Choice.Tweak with { AppliedValue = enable ? row.Choice.OnValue : 1 - row.Choice.OnValue };
            await TrackOperation(RunInBackground(() =>
            {
                bool changed = TweaksEngine.Change(tweak, restore);
                if (changed && tweak.Hive == RegistryHive.CurrentUser)
                    _ = SendMessageTimeout(new IntPtr(0xffff), 0x001a, 0, "ImmersiveColorSet", 2, 150, out _);
                return changed;
            }));
            if (!closing && tweak.Hive == RegistryHive.CurrentUser) UpdateAppearance();
            SetStatus(status, UiText.Get("Параметр Windows сохранён."));
        }
        catch (Exception ex) when (IsSystemError(ex)) { SetStatus(status, ex.Message, error: true); }
        finally { changingWindowsSetting = false; if (!closing) LoadWindowsSettings(); }
    }

    private static string AutomationOperation(Button button) => System.Windows.Automation.AutomationProperties.GetAutomationId(button);

    private WallpaperBackup? ReadWallpaperBackup()
    {
        try { return localState.Read<WallpaperBackup>("wallpaper-original.json"); }
        catch (Exception ex) when (AppPreferences.IsFileError(ex)) { SetStatus(WindowsAppearanceStatus, ex.Message, error: true); return null; }
    }

    private void WallpaperClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { CheckFileExists = true, Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            string original = ReadWallpaper();
            var previous = ReadWallpaperBackup();
            if (previous is not null && !string.Equals(original, previous.Applied, StringComparison.OrdinalIgnoreCase))
                previous = null;
            SetWallpaper(picker.FileName);
            try
            {
                string applied = ReadWallpaper();
                localState.Write("wallpaper-original.json", new WallpaperBackup(previous?.Original ?? original, applied));
            }
            catch (Exception ex) when (AppPreferences.IsFileError(ex) || ex is Win32Exception)
            {
                SetWallpaper(original);
                throw;
            }
            SetStatus(WindowsAppearanceStatus, UiText.Get("Обои рабочего стола изменены."));
            WallpaperRestoreButton.IsEnabled = true;
        }
        catch (Exception ex) when (IsSystemError(ex)) { SetStatus(WindowsAppearanceStatus, ex.Message, error: true); }
    }

    private void RestoreWallpaperClick(object sender, RoutedEventArgs e)
    {
        if (ReadWallpaperBackup() is not { } backup) return;
        try
        {
            if (!string.Equals(ReadWallpaper(), backup.Applied, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiText.Get("Обои изменились вне CoreShift. Сохранённый фон оставлен без изменений."));
            SetWallpaper(backup.Original);
            System.IO.File.Delete(System.IO.Path.Combine(localState.DirectoryPath, "wallpaper-original.json"));
            WallpaperRestoreButton.IsEnabled = false;
            SetStatus(WindowsAppearanceStatus, UiText.Get("Исходные обои восстановлены."));
        }
        catch (Exception ex) when (IsSystemError(ex)) { SetStatus(WindowsAppearanceStatus, ex.Message, error: true); }
    }

    private static string ReadWallpaper()
    {
        var path = new StringBuilder(32768);
        if (!SystemParametersInfoRead(0x0073, (uint)path.Capacity, path, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    private static void SetWallpaper(string path)
    {
        if (!SystemParametersInfoWrite(0x0014, 0, path, 3))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void StickyShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        try
        {
            var keys = ReadSticky();
            keys.Flags = mode == "on" ? keys.Flags | 4u : keys.Flags & ~4u;
            if (!SystemParametersInfoSticky(0x003b, keys.Size, ref keys, 3))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            StickyShortcutState.Text = UiText.Get((keys.Flags & 4) != 0 ? "Включено" : "Отключено");
        }
        catch (Win32Exception ex) { SetStatus(WindowsAppearanceStatus, ex.Message, error: true); }
    }

    private static StickyKeys ReadSticky()
    {
        var keys = new StickyKeys { Size = (uint)Marshal.SizeOf<StickyKeys>() };
        if (!SystemParametersInfoSticky(0x003a, keys.Size, ref keys, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return keys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StickyKeys { public uint Size; public uint Flags; }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoRead(uint action, uint length, StringBuilder value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoWrite(uint action, uint size, string value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoSticky(uint action, uint size, ref StickyKeys value, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
