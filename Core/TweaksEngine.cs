using System.Globalization;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal sealed record WindowsTweak(
    string Id, string Name, string Description, string Risk,
    RegistryHive Hive, string Key, string ValueName, int AppliedValue, bool Configurable = false,
    int MinimumValue = 1, int MaximumValue = 100);

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

    internal static WindowsTweak DiagnosticData { get; } = new(
        "telemetry", "Ограничение диагностических данных Windows",
            "Задаёт уровень «Диагностические данные отключены» для поддерживаемой редакции.",
            "Изменяет системную политику. Может ограничить диагностические данные об ошибках.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0,
            Configurable: true, MinimumValue: 0, MaximumValue: 3);

    internal static IReadOnlyList<WindowsTweak> DiagnosticPrivacyCatalog { get; } =
    [
        new("error-reporting", "Отключение отчётов об ошибках Windows",
            "Управляет отправкой отчётов о сбоях Windows Error Reporting.",
            "Отчёты о сбоях помогают разработчикам диагностировать ошибки.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
        new("feedback-prompts", "Скрытие запросов обратной связи",
            "Windows перестаёт показывать предложения оставить отзыв.",
            "Настройки диагностических данных управляются отдельно.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1),
        new("diagnostic-logs", "Дополнительные диагностические журналы",
            "Отключает сбор дополнительных журналов Windows для разбора сбоев.",
            "Действует при включённых необязательных диагностических данных.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "LimitDiagnosticLogCollection", 1),
        new("diagnostic-dumps", "Ограничение отправляемых дампов",
            "Ограничивает отчёты минидампами ядра и сокращёнными дампами приложений.",
            "Действует при отправке необязательных диагностических данных.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "LimitDumpCollection", 1),
        new("app-diagnostics", "Доступ приложений к диагностике",
            "Запрещает приложениям Windows получать диагностические сведения о других приложениях по умолчанию.",
            "Исключения отдельных приложений сохраняются. Открытые приложения потребуется перезапустить.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsGetDiagnosticInfo", 2)
    ];

    internal static IReadOnlyList<WindowsTweak> RecommendationPrivacyCatalog { get; } =
    [
        new("advertising-id", "Отключение рекламного идентификатора Windows",
            "Приложения теряют доступ к общему рекламному идентификатору Windows.",
            "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1),
        new("tailored-diagnostics", "Персональные предложения по диагностике",
            "Запрещает Windows подбирать советы и предложения на основе диагностических данных.",
            "Применяется для текущего пользователя.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
        new("spotlight-partners", "Предложения партнёров в Windows Spotlight",
            "Убирает предложения сторонних поставщиков из Windows Spotlight.",
            "Действует для текущего пользователя, когда Spotlight включён.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions", 1),
        new("windows-spotlight", "Отключение Windows Spotlight",
            "Отключает подборки Spotlight, в том числе смену изображений на экране блокировки.",
            "Применяется для текущего пользователя.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", 1),
        new("consumer-suggestions", "Рекомендации приложений Windows",
            "Отключает потребительские предложения Windows, включая рекомендации приложений в меню «Пуск».",
            "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
        new("settings-online-tips", "Онлайн-подсказки в параметрах Windows",
            "Отключает сетевую загрузку подсказок и справки в приложении «Параметры».",
            "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowOnlineTips", 0)
    ];

    internal static IReadOnlyList<WindowsTweak> CloudPrivacyCatalog { get; } =
    [
        new("online-speech", "Облачное распознавание речи",
            "Отключает облачное распознавание речи Windows и приложений, которые используют эту службу.",
            "Голосовой ввод, зависящий от онлайн-распознавания, станет недоступен.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0),
        new("cloud-search", "Облачные результаты в поиске",
            "Убирает результаты подключённых облачных служб из поиска Windows.",
            "Поиск локальных файлов и приложений остаётся доступен.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", 0),
        new("search-location", "Местоположение в поиске",
            "Запрещает поиску Windows использовать местоположение устройства.",
            "Разрешения на геолокацию в других приложениях управляются отдельно.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowSearchToUseLocation", 0),
        new("clipboard-sync", "Синхронизация буфера обмена",
            "Предотвращает передачу элементов буфера обмена между устройствами через Windows.",
            "Локальный буфер обмена остаётся доступен.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard", 0),
        new("clipboard-history", "История буфера обмена",
            "Отключает хранение истории буфера обмена Windows.", "Список Win+V станет недоступен. Копирование и вставка останутся доступны.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowClipboardHistory", 0),
        new("activity-feed", "Лента действий Windows",
            "Отключает публикацию действий приложений и их перенос между устройствами.", "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0)
    ];

    internal static IReadOnlyList<WindowsTweak> AppPrivacyCatalog { get; } =
    [
        AppPermission("app-location", "Геолокация для приложений", "Карты и местные прогнозы могут потерять автоматическое определение места.", "LetAppsAccessLocation"),
        AppPermission("app-camera", "Камера для приложений", "Видеозвонки и съёмка в приложениях могут перестать работать.", "LetAppsAccessCamera"),
        AppPermission("app-microphone", "Микрофон для приложений", "Голосовые чаты и запись звука в приложениях могут перестать работать.", "LetAppsAccessMicrophone"),
        AppPermission("app-account", "Данные учётной записи для приложений", "Приложения могут перестать получать имя и изображение профиля.", "LetAppsAccessAccountInfo")
    ];

    private static WindowsTweak AppPermission(string id, string name, string effect, string valueName) =>
        new(id, name, "По умолчанию запрещает доступ пакетным приложениям Windows. Исключения для отдельных приложений сохраняются.", effect,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", valueName, 2);

    internal static IReadOnlyList<WindowsTweak> PrivacyCatalog { get; } =
        [.. DiagnosticPrivacyCatalog, .. RecommendationPrivacyCatalog, .. CloudPrivacyCatalog, .. AppPrivacyCatalog];

    private const string ExplorerPreferences = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    internal static IReadOnlyList<WindowsTweak> ExplorerCatalog { get; } =
    [
        new("file-extensions", "Расширения файлов",
            "Показывает расширения известных типов файлов в Проводнике.", "Изменение действует для текущего пользователя. Может потребоваться заново открыть Проводник.",
            RegistryHive.CurrentUser, ExplorerPreferences, "HideFileExt", 0),
        new("hidden-files", "Скрытые файлы и папки",
            "Показывает скрытые файлы и папки в Проводнике.", "Изменение действует для текущего пользователя. Может потребоваться заново открыть Проводник.",
            RegistryHive.CurrentUser, ExplorerPreferences, "Hidden", 1)
    ];

    internal static IReadOnlyList<WindowsTweak> TaskbarCatalog { get; } =
    [
        new("hide-task-view", "Скрыть кнопку «Представление задач»",
            "Убирает кнопку представления задач с панели задач Windows 11.", "Для текущего пользователя. Представление задач остаётся доступно по Win+Tab.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideTaskViewButton", 1),
        new("hide-start-recommended", "Скрыть раздел «Рекомендуем»",
            "Скрывает рекомендации файлов и приложений в меню «Пуск» Windows 11.", "Изменение действует для текущего пользователя. Может потребоваться повторный вход.",
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1),
        new("hide-widgets", "Отключить виджеты Windows 11",
            "Отключает панель виджетов и её кнопку на панели задач.", "Политика действует на всех пользователей компьютера.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)
    ];

    internal static IReadOnlyList<WindowsTweak> PersonalizationCatalog { get; } =
    [
        new("clear-logon-background", "Чёткий фон на экране входа",
            "Убирает размытие фона при вводе пароля или PIN-кода.",
            "Политика применяется ко всем пользователям при следующем входе.",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", 1),
        new("verbose-logon", "Подробные сообщения при запуске и завершении работы",
            "Windows показывает этапы входа, выхода и выключения компьютера.",
            "Действует при следующем запуске или завершении работы.",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1),
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

    internal static IEnumerable<WindowsTweak> Catalog => PrivacyCatalog.Concat(PersonalizationCatalog)
        .Concat(ExplorerCatalog).Concat(TaskbarCatalog).Concat(PerformanceCatalog).Append(DiagnosticData);

    internal const string BackupPath = @"SOFTWARE\SmartResourceDispatcher\TweakBackups";

    internal static string? SupportIssue(WindowsTweak tweak)
    {
        using var machine = OpenHive(RegistryHive.LocalMachine);
        using var version = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var os = Environment.OSVersion.Version;
        var build = new Version(os.Major, os.Minor, os.Build, version?.GetValue("UBR") as int? ?? 0);
        return SupportIssue(tweak.Id, version?.GetValue("EditionID") as string, build, tweak.AppliedValue);
    }

    internal static string? SupportIssue(string id, string? edition, Version version, int appliedValue = 0)
    {
        if (version.Major < 10 || string.IsNullOrWhiteSpace(edition))
            return UiText.Get("Не удалось подтвердить поддержку версии или редакции Windows.");

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
            "explorer-smartscreen" when !supportsClientPolicies || version.Build < 15063
                => UiText.Get("Политика требует Windows 10 версии 1703 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "telemetry" when appliedValue == 0 && !enterprise && !education && !server => UiText.Get("Уровень 0 поддерживается в Enterprise, Education и Server."),
            "feedback-prompts" when !supportsClientPolicies => UiText.Get("Для этой редакции откройте параметры диагностики Windows."),
            "diagnostic-logs" or "diagnostic-dumps" when !supportsClientPolicies || version.Build < 18363
                => UiText.Get("Требуется Windows 10 версии 1909 или новее в редакции Pro, Enterprise, Education или IoT Enterprise."),
            "tailored-diagnostics" or "app-diagnostics" when !supportsClientPolicies || version.Build < 15063
                => UiText.Get("Политика требует Windows 10 версии 1703 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "spotlight-partners" when !supportsClientPolicies || version.Build < 14393
                => UiText.Get("Политика требует Windows 10 версии 1607 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "windows-spotlight" when !(enterprise || education || iot) || version.Build < 14393
                => UiText.Get("Требуется Windows 10 версии 1607 или новее в редакции Enterprise, Education или IoT Enterprise."),
            "consumer-suggestions" when !(enterprise || education || iot) || version.Build < 17134
                => UiText.Get("Требуется Windows 10 версии 1803 или новее в редакции Enterprise, Education или IoT Enterprise."),
            "cloud-search" or "settings-online-tips" when !supportsClientPolicies || version.Build < 16299
                => UiText.Get("Требуется Windows 10 версии 1709 или новее в редакции Pro, Enterprise, Education или IoT Enterprise."),
            "clipboard-sync" or "clipboard-history" when !supportsClientPolicies || version.Build < 17763
                => UiText.Get("Требуется Windows 10 версии 1809 или новее в редакции Pro, Enterprise, Education или IoT Enterprise."),
            "activity-feed" when !supportsClientPolicies || version.Build < 16299
                => UiText.Get("Требуется Windows 10 версии 1709 или новее в редакции Pro, Enterprise, Education или IoT Enterprise."),
            "app-location" or "app-camera" or "app-microphone" or "app-account" when !supportsClientPolicies || version.Build < 14393
                => UiText.Get("Политика требует Windows 10 версии 1607 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "hide-task-view" or "hide-start-recommended" when !supportsClientPolicies || version.Build < 22621
                => UiText.Get("Требуется Windows 11 версии 22H2 или новее в редакции Pro, Enterprise, Education или IoT Enterprise."),
            "hide-widgets" when !supportsClientPolicies || version.Build < 22000
                => UiText.Get("Требуется Windows 11 в редакции Pro, Enterprise, Education или IoT Enterprise."),
            "online-speech" or "search-location" when !supportsClientPolicies
                => UiText.Get("Политика требует редакцию Windows Pro, Enterprise, Education или IoT Enterprise."),
            "game-dvr" when server || version.Build >= 22000 || (!pro && !enterprise && !education && !iot)
                => UiText.Get("Политика применяется в поддерживаемых настольных редакциях Windows 10."),
            "notification-center" when !supportsTaskbarPolicy || (!pro && !enterprise && !education && !iot)
                => UiText.Get("Политика требует поддерживаемой версии Windows 10/11 Pro, Enterprise, Education или IoT Enterprise."),
            "clear-logon-background" when !supportsClientPolicies || version.Build < 18362
                => UiText.Get("Политика требует Windows 10 версии 1903 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "advertising-id" when !supportsClientPolicies || version.Build < 14393
                => UiText.Get("Политика требует Windows 10 версии 1607 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "search-highlights" when !supportsClientPolicies || !supportsSearchHighlights
                => UiText.Get("Политика требует обновлённой Windows 10 версии 20H2 или Windows 11 версии 21H2 либо новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "recent-apps" when !supportsClientPolicies || version.Build < 15063
                => UiText.Get("Политика требует Windows 10 версии 1703 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
            "background-download-limit" when !supportsClientPolicies || version.Build < 17134
                => UiText.Get("Политика требует Windows 10 версии 1803 или новее в поддерживаемой редакции Pro, Enterprise, Education или IoT Enterprise."),
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
        null => UiText.Get("Политика в этой ветке реестра не задана"),
        _ when value == tweak.AppliedValue => UiText.Get("В реестре задано значение твика"),
        _ => UiText.Format($"В реестре задано другое значение ({value})")
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
            throw new InvalidOperationException(UiText.Get("Другая операция с твиками ещё выполняется."));

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
        if (tweak.Configurable && !restore && (tweak.AppliedValue < tweak.MinimumValue || tweak.AppliedValue > tweak.MaximumValue ||
            tweak.Id == "telemetry" && tweak.AppliedValue == 2))
            throw new ArgumentOutOfRangeException(nameof(tweak), tweak.Id == "telemetry"
                ? UiText.Get("Выберите поддерживаемый уровень диагностики.") : UiText.Get("Укажите лимит от 1 до 100%."));
        if (tweak.Configurable && backup is not null)
        {
            string[] values = backup.Split('|');
            if (values.Length == 1 && tweak.Id == "telemetry")
                appliedValue = 0;
            else if (values.Length is < 2 or > 3)
                throw new InvalidDataException(UiText.Get("Сохранённое значение твика повреждено."));
            else
                appliedValue = int.Parse(values[1], CultureInfo.InvariantCulture);
            originalValue = values[0];
            if (values.Length == 3)
                previousAppliedValue = int.Parse(values[2], CultureInfo.InvariantCulture);
        }

        int? original = originalValue is null or "absent" ? null : int.Parse(originalValue, CultureInfo.InvariantCulture);
        if (backup is not null && current != original && current != appliedValue &&
            (!previousAppliedValue.HasValue || current != previousAppliedValue))
            throw new InvalidOperationException(UiText.Get("Значение политики изменилось извне. Сохранённое значение оставлено для проверки."));

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
            _ => throw new InvalidDataException(UiText.Get("Значение политики имеет неожиданный тип."))
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
