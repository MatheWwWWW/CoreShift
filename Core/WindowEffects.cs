using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal static class WindowEffects
{
    private const string PersonalizePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string TransparencyValue = "EnableTransparency";
    private const string BackupName = "window-effects";
    private const uint GetClientAreaAnimation = 0x1042, SetClientAreaAnimation = 0x1043;
    private const uint GetMenuAnimation = 0x1002, SetMenuAnimation = 0x1003;
    private const uint GetTooltipAnimation = 0x1016, SetTooltipAnimation = 0x1017;
    private const uint GetWindowAnimation = 0x0048, SetWindowAnimation = 0x0049;
    private const uint SaveAndNotify = 0x01 | 0x02; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
    private static readonly Effects Disabled = new(false, false, false, false, 0);

    private sealed record Effects(bool ClientArea, bool MinimizeAndRestore, bool Menus, bool Tooltips, int? Transparency);

    internal static (bool Optimized, bool HasBackup) Read()
    {
        using var user = OpenUserHive();
        using var backups = user.OpenSubKey(TweaksEngine.BackupPath);
        return (ReadEffects(user) == Disabled, backups?.GetValue(BackupName) is string);
    }

    internal static bool Change(bool restore)
    {
        using var mutex = new Mutex(false, @"Global\SmartResourceDispatcher.WindowEffects");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(UiText.Get("Другая операция с визуальными эффектами ещё выполняется."));
        }
        catch (AbandonedMutexException) { }

        try
        {
            using var user = OpenUserHive();
            using var backups = user.OpenSubKey(TweaksEngine.BackupPath, writable: true);
            string? saved = backups?.GetValue(BackupName) as string;
            if (restore && saved is null)
                return false;

            Effects current = ReadEffects(user);
            if (restore)
            {
                Effects original;
                try { original = JsonSerializer.Deserialize<Effects>(saved!) ?? throw new InvalidDataException(UiText.Get("Резервная копия пуста.")); }
                catch (JsonException ex) { throw new InvalidDataException(UiText.Get("Не удалось прочитать копию визуальных настроек."), ex); }
                UpdateEffects(user, current, Disabled, original);
                backups!.DeleteValue(BackupName);
                return true;
            }

            if (current == Disabled)
                return false;
            if (saved is not null)
                throw new InvalidOperationException(UiText.Get("Сначала восстановите исходные визуальные эффекты."));

            using var backupKey = user.CreateSubKey(TweaksEngine.BackupPath);
            backupKey.SetValue(BackupName, JsonSerializer.Serialize(current), RegistryValueKind.String);
            backupKey.Flush();
            UpdateEffects(user, current, current, Disabled);
            return true;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static Effects ReadEffects(RegistryKey user)
    {
        var animation = new AnimationInfo { Size = AnimationInfo.SizeInBytes };
        Check(SystemParametersInfo(GetWindowAnimation, animation.Size, ref animation, 0));
        using var key = user.OpenSubKey(PersonalizePath);
        int? transparency = key?.GetValue(TransparencyValue) switch
        {
            null => null,
            int value => value,
            _ => throw new InvalidDataException(UiText.Get("Настройка прозрачности имеет неожиданный тип."))
        };
        return new Effects(ReadSwitch(GetClientAreaAnimation), animation.MinimizeAndRestore != 0,
            ReadSwitch(GetMenuAnimation), ReadSwitch(GetTooltipAnimation), transparency);
    }

    private static void UpdateEffects(RegistryKey user, Effects current, Effects from, Effects to)
    {
        if (from.ClientArea != to.ClientArea && current.ClientArea == from.ClientArea)
            WriteSwitch(SetClientAreaAnimation, to.ClientArea);
        if (from.MinimizeAndRestore != to.MinimizeAndRestore && current.MinimizeAndRestore == from.MinimizeAndRestore)
        {
            var animation = new AnimationInfo { Size = AnimationInfo.SizeInBytes, MinimizeAndRestore = to.MinimizeAndRestore ? 1 : 0 };
            Check(SystemParametersInfo(SetWindowAnimation, animation.Size, ref animation, SaveAndNotify));
        }
        if (from.Menus != to.Menus && current.Menus == from.Menus)
            WriteSwitch(SetMenuAnimation, to.Menus);
        if (from.Tooltips != to.Tooltips && current.Tooltips == from.Tooltips)
            WriteSwitch(SetTooltipAnimation, to.Tooltips);
        if (from.Transparency != to.Transparency && current.Transparency == from.Transparency)
        {
            if (to.Transparency is { } value)
            {
                using var key = user.CreateSubKey(PersonalizePath);
                key.SetValue(TransparencyValue, value, RegistryValueKind.DWord);
            }
            else
            {
                using var key = user.OpenSubKey(PersonalizePath, writable: true);
                key?.DeleteValue(TransparencyValue, throwOnMissingValue: false);
            }

            SendMessageTimeout(0xffff, 0x001a, 0, "ImmersiveColorSet", 0x0002, 100, out _);
        }
    }

    private static bool ReadSwitch(uint action)
    {
        Check(SystemParametersInfo(action, 0, out int enabled, 0));
        return enabled != 0;
    }

    private static void WriteSwitch(uint action, bool enabled) =>
        Check(SystemParametersInfo(action, 0, enabled ? (nint)1 : 0, SaveAndNotify));

    private static void Check(bool success)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static RegistryKey OpenUserHive() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,
        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        internal static readonly uint SizeInBytes = (uint)Marshal.SizeOf<AnimationInfo>();
        public uint Size;
        public int MinimizeAndRestore;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, nint value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref AnimationInfo value, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, string category, uint flags, uint timeout, out nint result);
}
