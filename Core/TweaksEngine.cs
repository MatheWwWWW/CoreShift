using System.Globalization;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal sealed record WindowsTweak(
    string Id, string Name, string Description, string Risk,
    RegistryHive Hive, string Key, string ValueName, int AppliedValue, bool Configurable = false);

internal static class TweaksEngine
{
    internal static WindowsTweak BackgroundDownloadLimit { get; } = new(
        "background-download-limit", "Лимит фоновой загрузки обновлений",
        "Ограничивает фоновые загрузки Windows Update и других продуктов Microsoft через оптимизацию доставки.",
        "Обновления могут скачиваться дольше. Лимит не применяется к загрузкам с устройств в локальной сети.",
        RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
        "DOPercentageMaxBackgroundBandwidth", 20, Configurable: true);

    internal static IReadOnlyList<WindowsTweak> PerformanceCatalog { get; } =
    [
        new("game-dvr", "Запрет игрового захвата Windows",
            "Отключает запись игр и трансляцию через политику Windows 10 для настольных ПК.",
            "Пропадёт возможность использовать игровой захват Windows.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0)
    ];

    internal static IReadOnlyList<WindowsTweak> PrivacyCatalog { get; } =
    [
        new("telemetry", "Ограничение диагностических данных Windows",
            "Задаёт уровень «Диагностические данные отключены» для поддерживаемой редакции.",
            "Изменяет системную политику. Может ограничить диагностические данные об ошибках.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
        new("advertising-id", "Отключение рекламного идентификатора Windows",
            "Приложения теряют доступ к общему рекламному идентификатору Windows.",
            "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1),
        new("search-highlights", "Скрытие подборок в поиске Windows",
            "Убирает динамические подборки с главной страницы поиска Windows. Поиск файлов и приложений остаётся доступен.",
            "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", 0),
        new("recent-apps", "Скрытие недавно добавленных приложений в меню «Пуск»",
            "Скрывает список недавно установленных приложений у текущего пользователя.",
            "После применения или восстановления перезагрузите Windows.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecentlyAddedApps", 1),
        new("notification-center", "Скрытие центра уведомлений",
            "Убирает центр уведомлений с панели задач. Всплывающие уведомления продолжают появляться. Нужна перезагрузка.",
            "Пропущенные уведомления нельзя будет открыть через центр уведомлений.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1)
    ];

    internal static IEnumerable<WindowsTweak> Catalog => PrivacyCatalog.Concat(PerformanceCatalog);

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
        bool supportsClientPolicies = pro || enterprise || education || iot;
        bool supportsTaskbarPolicy = version.Build >= 19044 || version.Build is >= 19041 and <= 19043 && version.Revision >= 1202;
        bool supportsSearchHighlights = version.Build >= 22621 ||
            version.Build == 22000 && version.Revision >= 1761 ||
            version.Build is >= 19042 and < 22000 && (version.Build >= 19045 || version.Revision >= 1620);

        return id switch
        {
            "telemetry" when !enterprise && !education && !server => "Уровень 0 поддерживается в Enterprise, Education и Server.",
            "game-dvr" when server || version.Build >= 22000 || (!pro && !enterprise && !education && !iot)
                => "Политика применяется в поддерживаемых настольных редакциях Windows 10.",
            "notification-center" when !supportsTaskbarPolicy || (!pro && !enterprise && !education && !iot)
                => "Политика требует поддерживаемой версии Windows 10/11 Pro, Enterprise, Education или IoT Enterprise.",
            "advertising-id" when !supportsClientPolicies || version.Build < 14393
                => "Политика требует Windows 10 версии 1607 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise.",
            "search-highlights" when !supportsClientPolicies || !supportsSearchHighlights
                => "Политика требует обновлённой Windows 10 версии 20H2 или Windows 11 версии 21H2 либо новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise.",
            "recent-apps" when !supportsClientPolicies || version.Build < 15063
                => "Политика требует Windows 10 версии 1703 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise.",
            "background-download-limit" when !supportsClientPolicies || version.Build < 17134
                => "Политика требует Windows 10 версии 1803 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise.",
            _ => null
        };
    }

    internal static (int? Current, bool HasBackup) ReadSetting(WindowsTweak tweak)
    {
        using var hive = OpenHive(tweak.Hive);
        using var backups = hive.OpenSubKey(BackupPath);
        return (ReadDword(hive, tweak), backups?.GetValue(tweak.Id) is string);
    }

    internal static string DescribeState(WindowsTweak tweak)
    {
        string? unsupported = SupportIssue(tweak);
        if (unsupported is not null)
            return unsupported;

        using var hive = OpenHive(tweak.Hive);
        return DescribeState(tweak, ReadDword(hive, tweak));
    }

    internal static string DescribeState(WindowsTweak tweak, int? value) => value switch
    {
        null => "Политика в этой ветке реестра не задана",
        _ when value == tweak.AppliedValue => "В реестре задано значение твика",
        _ => $"В реестре задано другое значение ({value})"
    };

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
        string? originalValue = backup;
        int appliedValue = tweak.AppliedValue;
        int? previousAppliedValue = null;
        if (tweak.Configurable && !restore && tweak.AppliedValue is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(tweak), "Укажите лимит от 1 до 100%.");
        if (tweak.Configurable && backup is not null)
        {
            string[] values = backup.Split('|');
            if (values.Length is < 2 or > 3)
                throw new InvalidDataException("Сохранённое значение твика повреждено.");
            originalValue = values[0];
            appliedValue = int.Parse(values[1], CultureInfo.InvariantCulture);
            if (values.Length == 3)
                previousAppliedValue = int.Parse(values[2], CultureInfo.InvariantCulture);
        }

        int? original = originalValue is null or "absent" ? null : int.Parse(originalValue, CultureInfo.InvariantCulture);
        if (backup is not null && current != original && current != appliedValue &&
            (!previousAppliedValue.HasValue || current != previousAppliedValue))
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

        if (tweak.Configurable)
        {
            using var backupKey = hive.CreateSubKey(backupPath);
            string savedOriginal = originalValue ?? (current?.ToString(CultureInfo.InvariantCulture) ?? "absent");
            string saved = savedOriginal + "|" + tweak.AppliedValue.ToString(CultureInfo.InvariantCulture);
            // Preserve the previous setting until writing the new limit has succeeded.
            string pending = backup is not null && current is { } previous && current != original
                ? saved + "|" + previous.ToString(CultureInfo.InvariantCulture) : saved;
            backupKey.SetValue(tweak.Id, pending, RegistryValueKind.String);
            backupKey.Flush();
            WriteDword(hive, tweak, tweak.AppliedValue);
            if (pending != saved)
                backupKey.SetValue(tweak.Id, saved, RegistryValueKind.String);
            return true;
        }

        if (backup is null)
        {
            using var backupKey = hive.CreateSubKey(backupPath);
            string saved = current?.ToString(CultureInfo.InvariantCulture) ?? "absent";
            backupKey.SetValue(tweak.Id, saved, RegistryValueKind.String);
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
