<<<<<<< HEAD
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
=======
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SmartResourceDispatcher.Core;

internal sealed record TempFile(string Path, long Bytes, DateTime LastWriteUtc);

internal sealed record TempScanReport(
    IReadOnlyList<string> Roots, IReadOnlyList<TempFile> Candidates, long TotalBytes,
    int BusyFiles, int UnavailableFiles, int UnavailableFolders, int SkippedLinks, int RecentFiles);

internal sealed record CleanResult(int DeletedFiles, long DeletedBytes, int BusyFiles, int SkippedFiles);

internal static class DiskCleaner
{
    internal static TempScanReport ScanTempFolders() =>
        Scan(GetTargetTempPaths(), DateTime.UtcNow.AddDays(-1));

    private static IEnumerable<string> GetTargetTempPaths()
    {
        yield return Path.GetTempPath();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
            yield return Path.Combine(windows, "Temp");
    }

    internal static TempScanReport Scan(IEnumerable<string> folders, DateTime cutoffUtc)
    {
        string[] roots = folders.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<TempFile>();
        long total = 0;
        int busy = 0, unavailableFiles = 0, unavailableFolders = 0, links = 0, recent = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();

        foreach (string root in roots)
        {
            if (Path.GetDirectoryName(root) is null)
                unavailableFolders++;
            else
                pending.Push(root);
        }

        while (pending.TryPop(out string? folder))
        {
            if (!visited.Add(folder))
                continue;

            try
            {
                if (HasLinkedAncestor(folder))
                {
                    links++;
                    continue;
                }

                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    try
                    {
                        FileAttributes attributes = entry.Attributes;
                        if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            links++;
                            continue;
                        }

                        if (entry is DirectoryInfo)
                        {
                            pending.Push(entry.FullName);
                            continue;
                        }

                        var file = (FileInfo)entry;
                        long size = file.Length;
                        total += size;
                        if (file.LastWriteTimeUtc > cutoffUtc)
                        {
                            recent++;
                            continue;
                        }

                        if (attributes.HasFlag(FileAttributes.ReadOnly))
                        {
                            unavailableFiles++;
                            continue;
                        }

                        CheckFileSharing(file);
                        candidates.Add(new TempFile(file.FullName, size, file.LastWriteTimeUtc));
                    }
                    catch (IOException ex) when (IsSharingViolation(ex))
                    {
                        busy++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unavailableFiles++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unavailableFolders++;
            }
        }

        return new TempScanReport(roots, candidates, total, busy, unavailableFiles, unavailableFolders, links, recent);
    }

    internal static CleanResult Clean(TempScanReport scan)
    {
        int deleted = 0, busy = 0, skipped = 0;
        long bytes = 0;
        foreach (var candidate in scan.Candidates)
        {
            try
            {
                if (HasLinkedAncestor(Path.GetDirectoryName(candidate.Path)!))
                {
                    skipped++;
                    continue;
                }

                switch (DeleteCandidate(candidate))
                {
                    case DeleteOutcome.Deleted:
                        deleted++;
                        bytes += candidate.Bytes;
                        break;
                    case DeleteOutcome.Busy:
                        busy++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return new CleanResult(deleted, bytes, busy, skipped);
    }

    private enum DeleteOutcome { Skipped, Busy, Deleted }

    private static DeleteOutcome DeleteCandidate(TempFile candidate)
    {
        const uint deleteAccess = 0x00010000;
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        string expectedPath = candidate.Path.StartsWith(@"\\?\", StringComparison.Ordinal) ? candidate.Path
            : candidate.Path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + candidate.Path[2..]
            : @"\\?\" + candidate.Path;

        // Validate and delete through the same handle so the checked path is never reopened for deletion.
        using var handle = CreateFileW(expectedPath, deleteAccess | readAttributes, 0, 0, openExisting, openReparsePoint, 0);
        if (handle.IsInvalid)
            return IsSharingViolation(Marshal.GetLastWin32Error()) ? DeleteOutcome.Busy : DeleteOutcome.Skipped;

        var openedPath = new StringBuilder(expectedPath.Length + 1);
        uint length = GetFinalPathNameByHandleW(handle, openedPath, (uint)openedPath.Capacity, 0);
        if (length == 0 || length >= openedPath.Capacity ||
            !openedPath.ToString().Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.Directory)) != 0 ||
            ((long)info.SizeHigh << 32 | info.SizeLow) != candidate.Bytes ||
            ((long)info.LastWriteHigh << 32 | info.LastWriteLow) != candidate.LastWriteUtc.ToFileTimeUtc())
            return DeleteOutcome.Skipped;

        const int fileDispositionInfo = 4;
        byte deleteFile = 1;
        return SetFileInformationByHandle(handle, fileDispositionInfo, ref deleteFile, sizeof(byte))
            ? DeleteOutcome.Deleted : DeleteOutcome.Skipped;
    }

    private static bool HasLinkedAncestor(string folder)
    {
        for (string? path = folder; path is not null; path = Path.GetDirectoryName(path))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return true;
        }

        return false;
    }

    private static void CheckFileSharing(FileInfo file)
    {
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
    }

    private static bool IsSharingViolation(IOException ex) => IsSharingViolation(ex.HResult & 0xffff);

    private static bool IsSharingViolation(int windowsError) => windowsError is 32 or 33;

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            unit++;
            size /= 1024;
        }

        return $"{size:0.##} {units[unit]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public uint CreationLow, CreationHigh, LastAccessLow, LastAccessHigh, LastWriteLow, LastWriteHigh;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, LinkCount, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, uint shareMode, nint securityAttributes, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte deleteFile, uint size);
}
>>>>>>> 2cd29586cd2c9474ab19fa9ec1baf883efe66a39
