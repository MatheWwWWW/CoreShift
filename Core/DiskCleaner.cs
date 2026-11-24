using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SmartResourceDispatcher.Core;

internal enum CleanupCategory { Temp, ExplorerThumbnails, ExplorerIcons, DirectXShaders, WerArchive, WerQueue }

internal sealed record TempFile(string Path, long Bytes, DateTime LastWriteUtc);

internal sealed record TempScanReport(
    IReadOnlyList<string> Roots, IReadOnlyList<TempFile> Candidates, long TotalBytes,
    int BusyFiles, int UnavailableFiles, int UnavailableFolders, int SkippedLinks, int RecentFiles);

internal sealed record CleanResult(int DeletedFiles, long DeletedBytes, int BusyFiles, int SkippedFiles);

internal static class DiskCleaner
{
    private static readonly TempScanReport UnavailableRoot = new([], [], 0, 0, 0, 1, 0, 0);

    internal static TempScanReport ScanTempFolders() =>
        ScanCategory(CleanupCategory.Temp);

    internal static TempScanReport ScanCategory(CleanupCategory category)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (category is CleanupCategory.WerArchive or CleanupCategory.WerQueue)
            return ScanWindowsReports(category,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), nowUtc);

        DateTime cutoffUtc = nowUtc.AddDays(-1);
        if (category == CleanupCategory.Temp)
            return Scan(GetTargetTempPaths(), cutoffUtc);

        return ScanLocalCache(category,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), cutoffUtc);
    }

    internal static TempScanReport ScanLocalCache(CleanupCategory category, string localAppData, DateTime cutoffUtc)
    {
        if (!Path.IsPathFullyQualified(localAppData))
            return UnavailableRoot;

        switch (category)
        {
            case CleanupCategory.ExplorerThumbnails:
            case CleanupCategory.ExplorerIcons:
                string prefix = category == CleanupCategory.ExplorerThumbnails ? "thumbcache_" : "iconcache_";
                return Scan([Path.Combine(localAppData, "Microsoft", "Windows", "Explorer")], cutoffUtc, prefix);
            case CleanupCategory.DirectXShaders:
                return Scan([Path.Combine(localAppData, "D3DSCache")], cutoffUtc);
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }
    }

    internal static TempScanReport ScanWindowsReports(CleanupCategory category, string programData, DateTime nowUtc)
    {
        if (!Path.IsPathFullyQualified(programData))
            return UnavailableRoot;

        string folder = category switch
        {
            CleanupCategory.WerArchive => "ReportArchive",
            CleanupCategory.WerQueue => "ReportQueue",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
        return Scan([Path.Combine(programData, "Microsoft", "Windows", "WER", folder)], nowUtc.AddDays(-30));
    }

    private static IEnumerable<string> GetTargetTempPaths()
    {
        yield return Path.GetTempPath();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
            yield return Path.Combine(windows, "Temp");
    }

    internal static TempScanReport Scan(IEnumerable<string> folders, DateTime cutoffUtc)
        => Scan(folders, cutoffUtc, explorerPrefix: null);

    private static TempScanReport Scan(IEnumerable<string> folders, DateTime cutoffUtc, string? explorerPrefix)
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
                        if (explorerPrefix is not null &&
                            (entry is not FileInfo ||
                             !entry.Name.StartsWith(explorerPrefix, StringComparison.OrdinalIgnoreCase) ||
                             !entry.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
                            continue;

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
                        DateTime lastWriteUtc = file.LastWriteTimeUtc;
                        if (lastWriteUtc >= cutoffUtc)
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
                        candidates.Add(new TempFile(file.FullName, size, lastWriteUtc));
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

    internal static CleanResult Clean(IEnumerable<TempFile> candidates)
    {
        int deleted = 0, busy = 0, skipped = 0;
        long bytes = 0;
        foreach (var candidate in candidates)
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
