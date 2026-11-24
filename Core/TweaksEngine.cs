using System.Globalization;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal sealed record WindowsTweak(
    string Id, string Name, string Description, string Risk,
    RegistryHive Hive, string Key, string ValueName, int AppliedValue);

internal static class TweaksEngine
{
    internal static IReadOnlyList<WindowsTweak> Catalog { get; } =
    [
        new("telemetry", "Ограничение диагностических данных Windows",
            "Задаёт уровень «Диагностические данные отключены» для поддерживаемой редакции.",
            "Изменяет системную политику. Может ограничить диагностические данные об ошибках.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
        new("game-dvr", "Запрет игрового захвата Windows",
            "Отключает запись игр и трансляцию через политику Windows 10 для настольных ПК.",
            "Пропадёт возможность использовать игровой захват Windows.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0),
        new("notification-center", "Скрытие центра уведомлений",
            "Убирает центр уведомлений с панели задач. Всплывающие уведомления продолжают появляться. Нужна перезагрузка.",
            "Пропущенные уведомления нельзя будет открыть через центр уведомлений.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1)
    ];

    internal const string BackupPath = @"SOFTWARE\SmartResourceDispatcher\TweakBackups";

    internal static string? SupportIssue(WindowsTweak tweak)
    {
        using var machine = OpenHive(RegistryHive.LocalMachine);
        using var version = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var os = Environment.OSVersion.Version;
        var build = new Version(os.Major, os.Minor, os.Build, version?.GetValue("UBR") as int? ?? 0);
        return SupportIssue(tweak.Id, version?.GetValue("EditionID") as string, build);
    }

    internal static string? SupportIssue(string id, string? edition, Version version)
    {
        if (version.Major < 10 || string.IsNullOrWhiteSpace(edition))
            return "Не удалось подтвердить поддержку версии или редакции Windows.";

        bool enterprise = edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase);
        bool education = edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase);
        bool server = edition.StartsWith("Server", StringComparison.OrdinalIgnoreCase);
        bool pro = edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase);
        bool iot = edition.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase);
        bool supportsTaskbarPolicy = version.Build >= 19044 || version.Build is >= 19041 and <= 19043 && version.Revision >= 1202;

        return id switch
        {
            "telemetry" when !enterprise && !education && !server => "Уровень 0 поддерживается в Enterprise, Education и Server.",
            "game-dvr" when server || version.Build >= 22000 || (!pro && !enterprise && !education && !iot)
                => "Политика применяется в поддерживаемых настольных редакциях Windows 10.",
            "notification-center" when !supportsTaskbarPolicy || (!pro && !enterprise && !education && !iot)
                => "Политика требует поддерживаемой версии Windows 10/11 Pro, Enterprise, Education или IoT Enterprise.",
            _ => null
        };
    }

    internal static string DescribeState(WindowsTweak tweak)
    {
        string? unsupported = SupportIssue(tweak);
        if (unsupported is not null)
            return unsupported;

        using var hive = OpenHive(tweak.Hive);
        int? value = ReadDword(hive, tweak);
        if (!value.HasValue)
            return "Политика в этой ветке реестра не задана";

        return value == tweak.AppliedValue
            ? "В реестре задано значение твика"
            : $"В реестре задано другое значение ({value})";
    }

    internal static bool Change(WindowsTweak tweak, bool restore)
    {
        using var mutex = new Mutex(false, @"Global\SmartResourceDispatcher.Tweaks");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
            throw new InvalidOperationException("Другая операция с твиками ещё выполняется.");

        try
        {
            if (!restore && SupportIssue(tweak) is { } reason)
                throw new InvalidOperationException(reason);

            using var hive = OpenHive(tweak.Hive);
            return Change(hive, tweak, BackupPath, restore);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static bool Change(RegistryKey hive, WindowsTweak tweak, string backupPath, bool restore)
    {
        using var backups = hive.OpenSubKey(backupPath, writable: true);
        string? backup = backups?.GetValue(tweak.Id) as string;
        if (restore && backup is null)
            return false;

        int? current = ReadDword(hive, tweak);
        int? original = backup is null or "absent" ? null : int.Parse(backup, CultureInfo.InvariantCulture);
        if (backup is not null && current != original && current != tweak.AppliedValue)
            throw new InvalidOperationException("Значение политики изменилось извне. Сохранённое значение оставлено для проверки.");

        if (restore)
        {
            if (current != original)
                WriteDword(hive, tweak, original);
            backups!.DeleteValue(tweak.Id);
            return true;
        }

        if (current == tweak.AppliedValue)
            return false;

        if (backup is null)
        {
            using var backupKey = hive.CreateSubKey(backupPath);
            backupKey.SetValue(tweak.Id, current?.ToString(CultureInfo.InvariantCulture) ?? "absent", RegistryValueKind.String);
            backupKey.Flush();
        }

        WriteDword(hive, tweak, tweak.AppliedValue);
        return true;
    }

    private static RegistryKey OpenHive(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    private static int? ReadDword(RegistryKey hive, WindowsTweak tweak)
    {
        using var key = hive.OpenSubKey(tweak.Key);
        return key?.GetValue(tweak.ValueName) switch
        {
            null => null,
            int value => value,
            _ => throw new InvalidDataException("Значение политики имеет неожиданный тип.")
        };
    }

    private static void WriteDword(RegistryKey hive, WindowsTweak tweak, int? value)
    {
        using var key = value.HasValue ? hive.CreateSubKey(tweak.Key) : hive.OpenSubKey(tweak.Key, writable: true);
        if (value.HasValue)
            key!.SetValue(tweak.ValueName, value.Value, RegistryValueKind.DWord);
        else
            key?.DeleteValue(tweak.ValueName, throwOnMissingValue: false);
        key?.Flush();
    }
}
