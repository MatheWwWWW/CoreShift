using System;
using System.Management;

namespace SmartResourceDispatcher.Core
{
    public record PagefileRecommendation(
        double InstalledRamGb,
        int RecommendedMinMb,
        int RecommendedMaxMb,
        string Reasoning
    );

    public class MemoryAndPagefileManager
    {
        public static double GetTotalPhysicalMemoryGb()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["TotalPhysicalMemory"] != null && ulong.TryParse(obj["TotalPhysicalMemory"].ToString(), out ulong bytes))
                    {
                        return Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 2);
                    }
                }
            }
            catch { }

            return 0;
        }

        public static void PrintPagefileUsage()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
                bool foundAny = false;

                foreach (ManagementObject obj in searcher.Get())
                {
                    foundAny = true;
                    string name = obj["Name"]?.ToString() ?? "Неизвестно";
                    string allocated = obj["AllocatedBaseSize"]?.ToString() ?? "0";
                    string current = obj["CurrentUsage"]?.ToString() ?? "0";
                    string peak = obj["PeakUsage"]?.ToString() ?? "0";

                    Console.WriteLine($"Файл подкачки: {name} | Выделено: {allocated} МБ | Текущее: {current} МБ | Пик: {peak} МБ");
                }

                if (!foundAny)
                {
                    Console.WriteLine("Активные файлы подкачки не обнаружены (возможно, отключены).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось прочитать данные файла подкачки: {ex.Message}");
            }
        }

        public static PagefileRecommendation GetRecommendation()
        {
            double ramGb = GetTotalPhysicalMemoryGb();

            if (ramGb <= 8.5)
            {
                return new PagefileRecommendation(
                    InstalledRamGb: ramGb,
                    RecommendedMinMb: 4096,
                    RecommendedMaxMb: 8192,
                    Reasoning: "При объёме ОЗУ до 8 ГБ нужен запас файла подкачки, чтобы избежать нехватки памяти в тяжёлых приложениях."
                );
            }

            if (ramGb <= 16.5)
            {
                return new PagefileRecommendation(
                    InstalledRamGb: ramGb,
                    RecommendedMinMb: 2048,
                    RecommendedMaxMb: 4096,
                    Reasoning: "При 16 ГБ ОЗУ фиксированный небольшой файл подкачки снижает риск скачков при динамическом расширении и оставляет место для дампов."
                );
            }

            return new PagefileRecommendation(
                InstalledRamGb: ramGb,
                RecommendedMinMb: 1024,
                RecommendedMaxMb: 2048,
                Reasoning: "При объёме ОЗУ от 24 ГБ и выше основной объём задач помещается в оперативную память; небольшой файл подкачки нужен для стабильности и системных дампов."
            );
        }
    }
}
