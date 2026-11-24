using System;
using SmartResourceDispatcher.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Умный диспетчер ресурсов ПК — Проверка ядра ===\n");

// 1. ОЗУ и файл подкачки
var rec = MemoryAndPagefileManager.GetRecommendation();
Console.WriteLine($"Оперативная память: {rec.InstalledRamGb} ГБ");
MemoryAndPagefileManager.PrintPagefileUsage();
Console.WriteLine($"Рекомендуемый файл подкачки: {rec.RecommendedMinMb}–{rec.RecommendedMaxMb} МБ");
Console.WriteLine($"Пояснение: {rec.Reasoning}\n");

// 2. Твики Windows
Console.WriteLine("--- Настройки и твики Windows ---");
foreach (var tweak in TweaksEngine.ПолучитьСписокТвиков())
{
    string статус = tweak.Отключено ? "[ОТКЛЮЧЕНО]" : "[ВКЛЮЧЕНО]";
    Console.WriteLine($"{статус} {tweak.Название} (Риск: {tweak.Риск})");
    Console.WriteLine($"   Простыми словами: {tweak.ПростымиСловами}");
    Console.WriteLine($"   Зачем отключать:  {tweak.ЗачемОтключать}");
    Console.WriteLine($"   Для профи:        {tweak.ДляПрофи}");
}
Console.WriteLine();

// 3. Честная оценка Temp и очистка
var scan = DiskCleaner.ScanTempFolders();
Console.WriteLine($"Всего в папках Temp: {DiskCleaner.FormatBytes(scan.TotalBytes)}");
Console.WriteLine($"Можно удалить сейчас: {DiskCleaner.FormatBytes(scan.CleanableBytes)} (занято программами файлов: {scan.LockedFilesCount})");

Console.Write("Очистить доступные временные файлы сейчас? (y/n): ");
if (Console.ReadLine()?.Trim().ToLower() == "y")
{
    var cleanRes = DiskCleaner.CleanAllTempFolders();
    Console.WriteLine($"Удалено файлов: {cleanRes.DeletedFiles}, пропущено занятых: {cleanRes.FailedFiles}, освобождено: {DiskCleaner.FormatBytes(cleanRes.FreedBytes)}");
}

// 4. Память процессов
int trimmed = MemoryOptimizer.TrimBackgroundProcesses();
Console.WriteLine($"Очищен рабочий набор у фоновых процессов: {trimmed}");
