using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal static class PagefileIncrease
{
    private const string SettingsPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string BackupName = "pagefile-increase";
    private const string PagingFiles = "PagingFiles";
    private const ulong MiB = 1024 * 1024;

    internal sealed record Target(string Path, uint Allocated, uint Initial, uint Maximum, string[] OtherEntries, int Position, bool Automatic);
    internal sealed record State(Target? File, bool HasBackup, uint Minimum, uint SuggestedInitial, uint SuggestedMaximum,
        uint Capacity, ulong FreeBytes, string? Issue)
    {
        internal uint AllowedMaximum => Math.Max(Capacity, File?.Maximum ?? 0);

        internal bool Accepts(uint initial, uint maximum) =>
            initial >= Minimum && initial <= Capacity && maximum >= Math.Max(initial, File?.Maximum ?? 0) && maximum <= AllowedMaximum;
    }
    private sealed record SavedSettings(string[]? Original, string[] Applied);

    internal static State Read()
    {
        using var hive = OpenMachineHive();
        using var settings = hive.OpenSubKey(SettingsPath);
        using var backups = hive.OpenSubKey(TweaksEngine.BackupPath);
        bool saved = backups?.GetValue(BackupName) is string;
        try { return ReadState(ReadEntries(settings), saved); }
        catch (InvalidDataException ex) { return new State(null, saved, 0, 0, 0, 0, 0, ex.Message); }
    }

    private static State ReadState(string[]? entries, bool saved)
    {
        string windowsDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Target file = SelectTarget(entries, windowsDrive, MemoryAndPagefileManager.ReadLivePagefiles());
        string driveRoot = Path.GetPathRoot(file.Path)!;
        var drive = new DriveInfo(driveRoot);
        if (drive.DriveType != DriveType.Fixed)
            throw new InvalidDataException("Файл подкачки должен находиться на локальном диске.");

        ulong free = (ulong)drive.AvailableFreeSpace;
        uint capacity = (uint)Math.Min(uint.MaxValue, free / MiB + file.Allocated);
        uint baseline = Math.Max(file.Initial, file.Allocated);
        uint minimum = checked(baseline + 1);
        ulong ramMiB = (MemoryAndPagefileManager.ReadMemorySnapshot().Physical.TotalBytes + MiB - 1) / MiB;
        var dump = MemoryAndPagefileManager.ReadCrashDumpSettings();
        if (driveRoot.Equals(windowsDrive, StringComparison.OrdinalIgnoreCase) && dump is { Mode: 1, HasDedicatedFile: false })
        {
            ulong? installed = MemoryAndPagefileManager.ReadInstalledRam();
            if (installed is > 0)
                ramMiB = (installed.Value + MiB - 1) / MiB;
            minimum = Math.Max(minimum, checked((uint)(ramMiB + 257)));
        }

        if (capacity < minimum)
            return new State(file, saved, minimum, 0, 0, capacity, free,
                "На диске недостаточно места для увеличения файла подкачки с учётом текущих настроек и полного дампа памяти.");

        uint initial = (uint)Math.Min(capacity, Math.Max(minimum, (ulong)baseline + 4096));
        // Preserve the previous manual maximum or estimate the ordinary system-managed growth limit.
        ulong previousMaximum = file.Automatic
            ? Math.Min(Math.Max(3 * ramMiB, 4096), (ulong)drive.TotalSize / (8 * MiB))
            : file.Maximum;
        uint maximum = Math.Max(file.Maximum, (uint)Math.Min(capacity, Math.Max(initial, previousMaximum)));
        return new State(file, saved, minimum, initial, maximum, capacity, free, null);
    }

    internal static Target SelectTarget(string[]? entries, string windowsDrive, IReadOnlyList<PagefileUsage> activeFiles)
    {
        const string automaticPath = @"?:\pagefile.sys";
        var configured = new List<(string Entry, string Path, uint Initial, uint Maximum)>();
        foreach (string entry in entries ?? [])
        {
            if (entry.Equals(automaticPath, StringComparison.OrdinalIgnoreCase))
            {
                configured.Add((entry, automaticPath, 0, 0));
                continue;
            }

            // Split the size fields from the end so a path may contain spaces.
            string value = entry.TrimEnd();
            int maximumAt = value.LastIndexOf(' ');
            string prefix = maximumAt > 0 ? value[..maximumAt].TrimEnd() : "";
            int initialAt = prefix.LastIndexOf(' ');
            if (initialAt < 1 || !uint.TryParse(prefix.AsSpan(initialAt + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint initial) ||
                !uint.TryParse(value.AsSpan(maximumAt + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint maximum))
                throw new InvalidDataException("Windows использует формат файла подкачки, который CoreShift не может изменить.");
            string path = prefix[..initialAt].TrimEnd();
            if ((!IsLocalFile(path) && !path.Equals(automaticPath, StringComparison.OrdinalIgnoreCase)) ||
                maximum < initial || (initial == 0 && maximum > 0))
                throw new InvalidDataException("Windows использует конфигурацию файла подкачки, которую CoreShift не может изменить.");
            configured.Add((entry, path, initial, maximum));
        }

        if (configured.Any(file => file.Path.Equals(automaticPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (configured.Count != 1 || activeFiles.Any(file => !IsLocalFile(file.Name)))
                throw new InvalidDataException("Текущие автоматические расположения файлов подкачки не удалось определить.");
            var activePaths = activeFiles.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string path = activePaths.FirstOrDefault(file => OnDrive(file, windowsDrive)) ?? activePaths.FirstOrDefault()
                ?? Path.Combine(windowsDrive, "pagefile.sys");
            string[] others = activePaths.Where(file => !file.Equals(path, StringComparison.OrdinalIgnoreCase))
                .Select(file => $"{file} 0 0").ToArray();
            int position = Array.FindIndex(activePaths, file => file.Equals(path, StringComparison.OrdinalIgnoreCase));
            return new Target(path, Allocation(path, activeFiles), 0, 0, others, Math.Max(0, position), Automatic: true);
        }

        int index = configured.FindIndex(file => OnDrive(file.Path, windowsDrive));
        if (index < 0 && configured.Count > 0)
            index = 0;
        if (index < 0)
        {
            string path = Path.Combine(windowsDrive, "pagefile.sys");
            return new Target(path, Allocation(path, activeFiles), 0, 0, [], 0, Automatic: false);
        }

        var selected = configured[index];
        if (configured.Count(file => file.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("В параметрах Windows найдено несколько записей для одного файла подкачки.");
        configured.RemoveAt(index);
        return new Target(selected.Path, Allocation(selected.Path, activeFiles), selected.Initial, selected.Maximum,
            configured.Select(file => file.Entry).ToArray(), index, selected.Initial == 0);
    }

    internal static bool Change(uint initial, uint maximum, bool restore, string? expectedPath)
    {
        using var mutex = new Mutex(false, @"Global\SmartResourceDispatcher.PagefileIncrease");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Другая операция с файлом подкачки ещё выполняется.");
        }
        catch (AbandonedMutexException) { }

        try
        {
            using var hive = OpenMachineHive();
            using var settings = hive.OpenSubKey(SettingsPath, writable: true)
                ?? throw new InvalidOperationException("Параметры файла подкачки Windows недоступны.");
            using var backups = hive.CreateSubKey(TweaksEngine.BackupPath);
            string[]? current = ReadEntries(settings);
            if (restore)
                return UpdateConfiguration(settings, backups, current, null);

            if (backups.GetValue(BackupName) is string)
                throw new InvalidOperationException("Сначала восстановите исходные параметры файла подкачки.");
            State state = ReadState(current, saved: false);
            if (state.Issue is not null)
                throw new InvalidOperationException(state.Issue);
            Target file = state.File!;
            if (!file.Path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Расположение файла подкачки изменилось. Проверьте обновлённые параметры.");
            if (!state.Accepts(initial, maximum))
                throw new ArgumentException("Размеры файла подкачки не подходят для текущих параметров и свободного места. Проверьте обновлённые пределы.");

            string entry = FormattableString.Invariant($"{file.Path} {initial} {maximum}");
            return UpdateConfiguration(settings, backups, current,
                [.. file.OtherEntries.Take(file.Position), entry, .. file.OtherEntries.Skip(file.Position)]);
        }
        finally { mutex.ReleaseMutex(); }
    }

    // A single registry value contains the complete page file configuration.
    internal static bool UpdateConfiguration(RegistryKey settings, RegistryKey backups, string[]? expected, string[]? requested)
    {
        string[]? current = ReadEntries(settings);
        if (!Same(current, expected))
            throw new InvalidOperationException("Параметры файла подкачки изменились. Обновите состояние и повторите действие.");

        string? saved = backups.GetValue(BackupName) as string;
        if (requested is not null)
        {
            if (saved is not null)
                throw new InvalidOperationException("Сначала восстановите исходные параметры файла подкачки.");
            backups.SetValue(BackupName, JsonSerializer.Serialize(new SavedSettings(current, requested)), RegistryValueKind.String);
            backups.Flush();
            WriteEntries(settings, requested);
            return true;
        }

        if (saved is null)
            return false;
        SavedSettings backup;
        try { backup = JsonSerializer.Deserialize<SavedSettings>(saved) ?? throw new InvalidDataException("Резервная копия пуста."); }
        catch (JsonException ex) { throw new InvalidDataException("Не удалось прочитать копию параметров файла подкачки.", ex); }
        if (backup.Applied is not { Length: > 0 })
            throw new InvalidDataException("Резервная копия параметров файла подкачки повреждена.");
        if (Same(current, backup.Applied))
            WriteEntries(settings, backup.Original);
        else if (!Same(current, backup.Original))
            throw new InvalidOperationException("Параметры файла подкачки изменены в Windows. Резервная копия сохранена.");
        backups.DeleteValue(BackupName);
        return true;
    }

    private static bool IsLocalFile(string path) => path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static bool OnDrive(string path, string drive) => path.StartsWith(drive, StringComparison.OrdinalIgnoreCase);

    private static uint Allocation(string path, IReadOnlyList<PagefileUsage> files) =>
        files.FirstOrDefault(file => file.Name.Equals(path, StringComparison.OrdinalIgnoreCase))?.AllocatedMiB ?? 0;

    private static string[]? ReadEntries(RegistryKey? settings) => settings?.GetValue(PagingFiles) switch
    {
        null => null,
        string[] entries => entries,
        _ => throw new InvalidDataException("Параметры файла подкачки имеют неожиданный тип.")
    };

    private static void WriteEntries(RegistryKey settings, string[]? entries)
    {
        if (entries is null)
            settings.DeleteValue(PagingFiles, throwOnMissingValue: false);
        else
            settings.SetValue(PagingFiles, entries, RegistryValueKind.MultiString);
        settings.Flush();
    }

    private static bool Same(string[]? left, string[]? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private static RegistryKey OpenMachineHive() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
}
