using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core;

internal static class SearchIndexExclusions
{
    private const string BackupPath = TweaksEngine.BackupPath + @"\SearchIndexExclusions";
    private static readonly Guid SearchManagerClass = new("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39");

    internal static string[] ReadSavedFolders()
    {
        using var hive = OpenMachineHive();
        using var backups = hive.OpenSubKey(BackupPath);
        return backups?.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }

    internal static bool Change(string folder, bool restore)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string root = Path.GetPathRoot(folder)!;
        if (root.Length != 3 || root[1] != ':' || folder.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            (!restore && (!Directory.Exists(folder) || new DriveInfo(root).DriveType != DriveType.Fixed)))
            throw new ArgumentException(UiText.Get("Выберите существующую папку на локальном диске."));

        using var mutex = new Mutex(false, @"Global\SmartResourceDispatcher.SearchIndexExclusions");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(UiText.Get("Другая операция с индексом Windows ещё выполняется."));
        }
        catch (AbandonedMutexException)
        {
            // Read the saved folder and current rule again before making any change.
        }

        try
        {
            using var hive = OpenMachineHive();
            using var backups = hive.OpenSubKey(BackupPath, writable: true);
            bool saved = backups?.GetValue(folder) is int;
            if (restore && !saved)
                return false;

            var manager = (ISearchManager)Activator.CreateInstance(Type.GetTypeFromCLSID(SearchManagerClass, throwOnError: true)!)!;
            try
            {
                manager.GetCatalog("SystemIndex", out var catalog);
                try
                {
                    catalog.GetCrawlScopeManager(out var scopes);
                    try { return ChangeRule(hive, backups, scopes, folder, saved, restore); }
                    finally { Marshal.ReleaseComObject(scopes); }
                }
                finally { Marshal.ReleaseComObject(catalog); }
            }
            finally { Marshal.ReleaseComObject(manager); }
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool ChangeRule(RegistryKey hive, RegistryKey? backups, ISearchCrawlScopeManager scopes,
        string folder, bool saved, bool restore)
    {
        string url = $"file:///{folder}\\";
        string pattern = url + "*";
        ScopeRule? existing = FindUserRule(scopes, pattern);
        if (restore)
        {
            if (existing is { Included: true } or { FollowFlags: not 0 })
                throw new InvalidOperationException(UiText.Get("Правило индексации изменено в Windows. Сохранена текущая настройка."));
            if (existing is not null)
            {
                scopes.RemoveScopeRule(existing.Pattern);
                scopes.SaveAll();
            }
            backups!.DeleteValue(folder);
            return true;
        }

        if (existing is not null || !scopes.IncludedInCrawlScope(url))
            return false;

        if (!saved)
        {
            using var backupKey = hive.CreateSubKey(BackupPath);
            backupKey.SetValue(folder, 1, RegistryValueKind.DWord);
            backupKey.Flush();
        }
        scopes.AddUserScopeRule(pattern, include: false, overrideChildren: false, followFlags: 0);
        scopes.SaveAll();
        return true;
    }

    private sealed record ScopeRule(string Pattern, bool Included, uint FollowFlags);

    private static ScopeRule? FindUserRule(ISearchCrawlScopeManager scopes, string pattern)
    {
        scopes.EnumerateScopeRules(out var rules);
        try
        {
            while (true)
            {
                uint fetched = 0;
                Marshal.ThrowExceptionForHR(rules.Next(1, out var rule, ref fetched));
                if (fetched == 0)
                    return null;

                try
                {
                    string current = rule.GetPatternOrUrl();
                    if (!rule.IsDefault() && current.Replace('/', '\\').Equals(pattern.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                        return new ScopeRule(current, rule.IsIncluded(), rule.GetFollowFlags());
                }
                finally { Marshal.ReleaseComObject(rule); }
            }
        }
        finally { Marshal.ReleaseComObject(rules); }
    }

    private static RegistryKey OpenMachineHive() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    // Unused COM slots follow the Windows SDK SearchAPI.h declarations.
    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchManager
    {
        void _VtblGap1_7();
        void GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string name, out ISearchCatalogManager catalog);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCatalogManager
    {
        void _VtblGap1_25();
        void GetCrawlScopeManager(out ISearchCrawlScopeManager scopes);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF55"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCrawlScopeManager
    {
        void _VtblGap1_5();
        void AddUserScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pattern, [MarshalAs(UnmanagedType.Bool)] bool include,
            [MarshalAs(UnmanagedType.Bool)] bool overrideChildren, uint followFlags);
        void RemoveScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pattern);
        void EnumerateScopeRules(out IEnumSearchScopeRules rules);
        void _VtblGap2_2();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IncludedInCrawlScope([MarshalAs(UnmanagedType.LPWStr)] string url);
        void _VtblGap3_2();
        void SaveAll();
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSearchScopeRules
    {
        [PreserveSig]
        int Next(uint count, out ISearchScopeRule rule, ref uint fetched);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF53"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchScopeRule
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetPatternOrUrl();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsIncluded();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsDefault();
        uint GetFollowFlags();
    }
}
