using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using SmartResourceDispatcher.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

try
{
    return args switch
    {
        [] or ["status"] => PrintStatus(),
        ["--help"] or ["-h"] or ["help"] => PrintHelp(),
        ["clean"] or ["clean", "--dry-run"] => Clean(apply: false),
        ["clean", "--apply"] => Clean(apply: true),
        ["memory", "trim", var pid] when int.TryParse(pid, out int processId) => Trim(processId),
        ["tweak", "list"] => PrintTweaks(),
        ["tweak", "apply", var id] => UpdateTweak(id, restore: false),
        ["tweak", "restore", var id] => UpdateTweak(id, restore: true),
        ["profile", "watch", var name] => WatchProfile(name, "above-normal"),
        ["profile", "watch", var name, var priority] => WatchProfile(name, priority),
        _ => PrintHelp(invalid: true)
    };
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
    or UnauthorizedAccessException or SecurityException or Win32Exception)
{
    Console.Error.WriteLine($"Ошибка: {ex.Message}");
    return 1;
}

static int PrintHelp(bool invalid = false)
{
    if (invalid)
        Console.Error.WriteLine("Неизвестная команда или неверные аргументы.");

    Console.WriteLine("""
        Smart Resource Dispatcher
          status                                    Диагностика памяти и политик Windows
          clean [--dry-run]                         Предпросмотр очистки Temp
          clean --apply                             Очистка найденных файлов после подтверждения
          memory trim <PID>                         Сброс рабочего набора выбранного процесса
          tweak list                                Список твиков и состояние политик
          tweak apply <ID> | tweak restore <ID>     Применение или восстановление твика
          profile watch <EXE> [above-normal|normal|below-normal]
                                                     Приоритет приложения, пока оно в фокусе
        """);
    return invalid ? 2 : 0;
}

static int PrintStatus()
{
    ulong? ram = ReadDiagnostic(MemoryAndPagefileManager.ReadInstalledRam);
    Console.WriteLine($"Оперативная память: {(ram is { } bytes ? DiskCleaner.FormatBytes((long)bytes) : "не удалось определить")}");

    var pagefiles = ReadDiagnostic(MemoryAndPagefileManager.ReadPagefiles);
    if (pagefiles is null)
        Console.WriteLine("Использование файла подкачки: данные недоступны.");
    else if (pagefiles.Count == 0)
        Console.WriteLine("WMI не обнаружил активных файлов подкачки.");
    else
        foreach (var file in pagefiles)
            Console.WriteLine($"Файл подкачки {file.Name}: выделено {Size(file.AllocatedMiB)}, используется {Size(file.CurrentMiB)}, пик {Size(file.PeakMiB)}.");

    var commit = ReadDiagnostic(MemoryAndPagefileManager.ReadCommit);
    if (commit is not null)
        Console.WriteLine($"Выделенная память: пик с загрузки {DiskCleaner.FormatBytes((long)commit.PeakBytes)}, текущий лимит {DiskCleaner.FormatBytes((long)commit.LimitBytes)}.");

    var dump = ReadDiagnostic(MemoryAndPagefileManager.ReadCrashDumpSettings);
    Console.WriteLine("Рекомендация: оставить управление размером файла подкачки Windows.");
    Console.WriteLine(MemoryAndPagefileManager.ExplainManualSizing(ram, commit, dump));
    Console.WriteLine();
    return PrintTweaks();
}

static T? ReadDiagnostic<T>(Func<T> read)
{
    try { return read(); }
    catch (Exception ex) when (ex is ManagementException or COMException or Win32Exception
        or UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Источник диагностики недоступен: {ex.Message}");
        return default;
    }
}

static string Size(uint? mib) => mib.HasValue ? $"{mib} Mb" : "неизвестно";

static int Clean(bool apply)
{
    var scan = DiskCleaner.ScanTempFolders();
    Console.WriteLine($"Папки Temp: {string.Join(", ", scan.Roots)}");
    Console.WriteLine($"Найдено {DiskCleaner.FormatBytes(scan.TotalBytes)}. Кандидаты старше 24 часов: {scan.Candidates.Count} файлов ({DiskCleaner.FormatBytes(scan.Candidates.Sum(file => file.Bytes))}).");
    Console.WriteLine($"Пропущено: свежих {scan.RecentFiles}, занятых {scan.BusyFiles}, недоступных файлов {scan.UnavailableFiles}, недоступных папок {scan.UnavailableFolders}, ссылок {scan.SkippedLinks}.");
    foreach (var file in scan.Candidates)
        Console.WriteLine($"  {file.Path}");

    if (!apply || scan.Candidates.Count == 0)
        return 0;

    Console.Write("Удалить найденные файлы? (y/N): ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        return 0;

    var result = DiskCleaner.Clean(scan.Candidates);
    Console.WriteLine($"Удалено файлов: {result.DeletedFiles} ({DiskCleaner.FormatBytes(result.DeletedBytes)}). Занято: {result.BusyFiles}. Изменились или недоступны: {result.SkippedFiles}.");
    return result.BusyFiles > 0 || result.SkippedFiles > 0 ? 1 : 0;
}

static int Trim(int pid)
{
    MemoryOptimizer.TrimWorkingSet(pid);
    Console.WriteLine($"Сброшен рабочий набор процесса {pid}.");
    return 0;
}

static int PrintTweaks()
{
    foreach (var tweak in TweaksEngine.Catalog)
    {
        string? state = ReadDiagnostic(() => TweaksEngine.DescribeState(tweak));
        Console.WriteLine($"{tweak.Id} — {tweak.Name}. {(state ?? "Состояние неизвестно").TrimEnd('.')}.");
        Console.WriteLine($"  {tweak.Description} {tweak.Risk}");
    }

    return 0;
}

static int UpdateTweak(string id, bool restore)
{
    var tweak = TweaksEngine.Catalog.FirstOrDefault(tweak => tweak.Id == id)
        ?? throw new ArgumentException($"Неизвестный твик «{id}». Список доступен через tweak list.");
    bool changed = TweaksEngine.Change(tweak, restore);
    Console.WriteLine((restore, changed) switch
    {
        (true, true) => "Исходное значение политики восстановлено.",
        (true, false) => "Сохранённое значение для этого твика отсутствует.",
        (false, true) => $"Политика записана. {tweak.Description}",
        _ => "Значение твика уже задано."
    });
    return 0;
}

static int WatchProfile(string processName, string priorityName)
{
    processName = ContextProfileManager.NormalizeProcessName(processName);

    var priority = priorityName switch
    {
        "above-normal" => ProcessPriorityClass.AboveNormal,
        "normal" => ProcessPriorityClass.Normal,
        "below-normal" => ProcessPriorityClass.BelowNormal,
        _ => throw new ArgumentException("Допустимый приоритет: above-normal, normal или below-normal.")
    };

    using var stop = new CancellationTokenSource();
    void Cancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        stop.Cancel();
    }

    var profile = new ContextProfileManager(processName, priority);
    Console.CancelKeyPress += Cancel;
    try
    {
        bool restored = profile.Watch(stop.Token,
            () => Console.WriteLine($"Профиль для {processName}, приоритет {priority}. Для выхода нажмите Ctrl+C."),
            Console.WriteLine);
        return restored ? 0 : 1;
    }
    finally
    {
        Console.CancelKeyPress -= Cancel;
    }
}
