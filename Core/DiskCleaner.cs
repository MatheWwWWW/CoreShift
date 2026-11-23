using System;
using System.IO;

namespace SmartResourceDispatcher.Core
{
    public record TempScanReport(
        long TotalBytes,
        long CleanableBytes,
        int LockedFilesCount
    );

    public record CleanResult(
        int DeletedFiles,
        int FailedFiles,
        long FreedBytes
    );

    public class DiskCleaner
    {
        public static string[] GetTargetTempPaths()
        {
            return new[]
            {
                Path.GetTempPath(),
                @"C:\Windows\Temp"
            };
        }

        // Сканирует Temp: сколько всего весит и сколько реально можно удалить прямо сейчас
        public static TempScanReport ScanTempFolders()
        {
            long totalBytes = 0;
            long cleanableBytes = 0;
            int lockedFiles = 0;

            foreach (var path in GetTargetTempPaths())
            {
                if (!Directory.Exists(path)) continue;
                ScanDirectory(new DirectoryInfo(path), ref totalBytes, ref cleanableBytes, ref lockedFiles);
            }

            return new TempScanReport(totalBytes, cleanableBytes, lockedFiles);
        }

        private static void ScanDirectory(DirectoryInfo dir, ref long totalBytes, ref long cleanableBytes, ref int lockedFiles)
        {
            try
            {
                foreach (FileInfo file in dir.EnumerateFiles())
                {
                    long len = 0;
                    try { len = file.Length; } catch { }
                    totalBytes += len;

                    if (IsFileUnlocked(file))
                    {
                        cleanableBytes += len;
                    }
                    else
                    {
                        lockedFiles++;
                    }
                }

                foreach (DirectoryInfo subDir in dir.EnumerateDirectories())
                {
                    ScanDirectory(subDir, ref totalBytes, ref cleanableBytes, ref lockedFiles);
                }
            }
            catch
            {
                // Нет доступа к папке — пропускаем
            }
        }

        private static bool IsFileUnlocked(FileInfo file)
        {
            try
            {
                using FileStream fs = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static CleanResult CleanAllTempFolders()
        {
            int deletedFiles = 0;
            int failedFiles = 0;
            long freedBytes = 0;

            foreach (var path in GetTargetTempPaths())
            {
                if (!Directory.Exists(path)) continue;

                var (del, fail, freed) = CleanDirectory(new DirectoryInfo(path));
                deletedFiles += del;
                failedFiles += fail;
                freedBytes += freed;
            }

            return new CleanResult(deletedFiles, failedFiles, freedBytes);
        }

        private static (int deleted, int failed, long freed) CleanDirectory(DirectoryInfo dir)
        {
            int deleted = 0;
            int failed = 0;
            long freed = 0;

            foreach (FileInfo file in dir.EnumerateFiles())
            {
                try
                {
                    long len = file.Length;
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                    freed += len;
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }

            foreach (DirectoryInfo subDir in dir.EnumerateDirectories())
            {
                try
                {
                    long subSize = GetDirectorySize(subDir);
                    var subResult = CleanDirectory(subDir);
                    deleted += subResult.deleted;
                    failed += subResult.failed;
                    freed += subResult.freed;

                    subDir.Attributes = FileAttributes.Normal;
                    subDir.Delete(false);
                    freed += Math.Max(0, subSize - subResult.freed);
                }
                catch
                {
                    failed++;
                }
            }

            return (deleted, failed, freed);
        }

        private static long GetDirectorySize(DirectoryInfo dir)
        {
            long size = 0;
            try
            {
                foreach (var fi in dir.EnumerateFiles())
                {
                    try { size += fi.Length; } catch { }
                }

                foreach (var di in dir.EnumerateDirectories())
                {
                    size += GetDirectorySize(di);
                }
            }
            catch { }
            return size;
        }

        public static string FormatBytes(long bytes)
        {
            string[] sizes = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}