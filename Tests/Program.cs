using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

if (args is ["--idle"])
{
    Console.ReadLine();
    return;
}

if (args is ["--cache-preview"])
{
    CheckExplorerCache();
    CheckDirectXCache();
    CheckNvidiaCaches();
    CheckWindowsReports();
    CheckFontCache();
    return;
}

if (args is ["--performance"])
{
    CheckTweakRestore();
    CheckPagefileIncrease();
    CheckContextProfile();
    CheckContextProfileStopsOnSelectedExit();
    return;
}

CheckTempCleanup();
CheckTempDeleteAccess();
CheckExplorerCache();
CheckDirectXCache();
CheckNvidiaCaches();
CheckWindowsReports();
CheckFontCache();
CheckTweakRestore();
CheckPagefileIncrease();
CheckPagefileAdvice();
CheckContextProfile();
CheckContextProfileStopsOnSelectedExit();
CheckProcessMemoryTrim();

static void CheckTempCleanup()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        string root = scratch.CreateSubdirectory("temp").FullName;
        string stale = Path.Combine(root, "old.tmp");
        string busy = Path.Combine(root, "busy.tmp");
        string changed = Path.Combine(root, "changed.tmp");
        string recent = Path.Combine(root, "new.tmp");
        string newlyBusy = Path.Combine(root, "newly-busy.tmp");
        foreach (string path in new[] { stale, busy, changed, recent, newlyBusy })
        {
            File.WriteAllText(path, "test");
            if (path != recent)
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }

        using var busyHandle = File.Open(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scan = DiskCleaner.Scan([root, root, Path.Combine(root, "missing")], DateTime.UtcNow.AddDays(-1));
        Require(scan.Candidates.Count == 3 && scan.BusyFiles == 1 && scan.RecentFiles == 1 && scan.UnavailableFolders == 1,
            "Temp preview should distinguish old, busy and recent files and missing roots.");
        Require(File.Exists(stale), "Temp preview removed a file.");

        File.AppendAllText(changed, " changed");
        using var newlyBusyHandle = File.Open(newlyBusy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var clean = DiskCleaner.Clean(scan.Candidates);
        Require(clean.DeletedFiles == 1 && clean.DeletedBytes == 4 && clean.BusyFiles == 1 && clean.SkippedFiles == 1,
            $"Unexpected cleanup result: {clean}.");
        Require(!File.Exists(stale) && File.Exists(changed) && File.Exists(recent) && File.Exists(busy) && File.Exists(newlyBusy),
            "Cleanup should leave changed, recent and busy files alone.");
        Console.WriteLine("PASS Temp preview and cleanup");
        CheckTempLink(root, scratch.CreateSubdirectory("other").FullName);
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
}

static void CheckTempDeleteAccess()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-acl-check-");
    try
    {
        var folder = scratch.CreateSubdirectory("temp");
        var protectedFile = new FileInfo(Path.Combine(folder.FullName, "protected.tmp"));
        string removable = Path.Combine(folder.FullName, "removable.tmp");
        DateTime cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (string path in new[] { protectedFile.FullName, removable })
        {
            File.WriteAllText(path, "test");
            File.SetLastWriteTimeUtc(path, cutoff.AddDays(-1));
        }

        var before = DiskCleaner.Scan([folder.FullName], cutoff);
        Require(before.Candidates.Count == 2, "Both test files should initially be removable.");

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var folderAcl = folder.GetAccessControl(AccessControlSections.Access);
        var fileAcl = protectedFile.GetAccessControl(AccessControlSections.Access);
        byte[] originalFolderAcl = folderAcl.GetSecurityDescriptorBinaryForm();
        byte[] originalFileAcl = fileAcl.GetSecurityDescriptorBinaryForm();
        folderAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
        fileAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Delete, AccessControlType.Deny));
        try
        {
            folder.SetAccessControl(folderAcl);
            protectedFile.SetAccessControl(fileAcl);
            Require(File.ReadAllText(protectedFile.FullName) == "test", "The protected test file should still be readable.");

            var after = DiskCleaner.Scan([folder.FullName], cutoff);
            Require(after.Candidates.Count == 1 && after.Candidates[0].Path == removable && after.UnavailableFiles == 1,
                "A readable file without DELETE access must not show up as removable.");

            var result = DiskCleaner.Clean(before.Candidates);
            Require(result.DeletedFiles == 1 && result.DeniedFiles == 1 &&
                File.Exists(protectedFile.FullName) && !File.Exists(removable),
                $"Cleanup must report access revoked after preview and still remove the other file: {result}.");
        }
        finally
        {
            // Reloading each snapshot marks its access rules as modified for SetAccessControl.
            try
            {
                folderAcl.SetSecurityDescriptorBinaryForm(originalFolderAcl, AccessControlSections.Access);
                folder.SetAccessControl(folderAcl);
            }
            finally
            {
                if (File.Exists(protectedFile.FullName))
                {
                    fileAcl.SetSecurityDescriptorBinaryForm(originalFileAcl, AccessControlSections.Access);
                    protectedFile.SetAccessControl(fileAcl);
                }
            }
        }
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
    Console.WriteLine("PASS Windows delete access in Temp preview and cleanup");
}

static void CheckExplorerCache()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        string root = Directory.CreateDirectory(Path.Combine(scratch.FullName, "Microsoft", "Windows", "Explorer")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        string thumbnail = Path.Combine(root, "THUMBCACHE_96.DB");
        string icon = Path.Combine(root, "iCoNcAcHe_32.dB");
        string recent = Path.Combine(root, "thumbcache_recent.db");
        DateTime now = DateTime.UtcNow;

        foreach (string path in new[]
        {
            thumbnail, icon,
            Path.Combine(root, "thumbcache_other.txt"),
            Path.Combine(root, "iconcache_backup.db.bak"),
            Path.Combine(root, "unrelated.db"),
            Path.Combine(root, "not_thumbcache_96.db"),
            Path.Combine(nested, "thumbcache_nested.db"),
            Path.Combine(nested, "iconcache_nested.db")
        })
        {
            File.WriteAllText(path, "test");
            File.SetLastWriteTimeUtc(path, now.AddDays(-2));
        }
        File.WriteAllText(recent, "test");
        File.SetLastWriteTimeUtc(recent, now);

        DateTime cutoff = now.AddDays(-1);
        var thumbnails = DiskCleaner.ScanLocalCache(CleanupCategory.ExplorerThumbnails, scratch.FullName, cutoff);
        var icons = DiskCleaner.ScanLocalCache(CleanupCategory.ExplorerIcons, scratch.FullName, cutoff);
        Require(thumbnails.Candidates.Count == 1 && thumbnails.Candidates[0].Path == thumbnail && thumbnails.RecentFiles == 1,
            "Thumbnail preview should include only old top-level thumbnail databases and count the recent match.");
        Require(icons.Candidates.Count == 1 && icons.Candidates[0].Path == icon && icons.RecentFiles == 0,
            "Icon preview should include only old top-level icon databases.");
        Require(File.Exists(thumbnail) && File.Exists(icon), "Explorer preview removed a file.");
        Console.WriteLine("PASS Explorer cache preview");
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
}

static void CheckDirectXCache()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        var cache = scratch.CreateSubdirectory("D3DSCache");
        string oldShader = Path.Combine(cache.CreateSubdirectory("game").FullName, "old.bin");
        string freshShader = Path.Combine(cache.FullName, "fresh.bin");
        string vendorShader = Path.Combine(scratch.CreateSubdirectory("NVIDIA/DXCache").FullName, "outside.bin");
        DateTime now = DateTime.UtcNow;
        foreach (string path in new[] { oldShader, freshShader, vendorShader })
        {
            File.WriteAllText(path, "shader");
            File.SetLastWriteTimeUtc(path, path == freshShader ? now : now.AddDays(-2));
        }

        var preview = DiskCleaner.ScanLocalCache(CleanupCategory.DirectXShaders, scratch.FullName, now.AddDays(-1));
        Require(preview.Candidates.Count == 1 && preview.Candidates[0].Path == oldShader && preview.RecentFiles == 1,
            "DirectX preview should include old shaders inside D3DSCache, not recent files or vendor caches.");
        Require(File.Exists(oldShader) && File.Exists(freshShader) && File.Exists(vendorShader),
            "DirectX preview must not remove files.");
        Console.WriteLine("PASS DirectX shader cache preview");
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
}

static void CheckNvidiaCaches()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        var nvidia = scratch.CreateSubdirectory("NVIDIA");
        var dx = nvidia.CreateSubdirectory("DXCache");
        var gl = nvidia.CreateSubdirectory("GLCache");
        string dxShader = Path.Combine(dx.FullName, "dx.bin");
        string glShader = Path.Combine(gl.CreateSubdirectory("game").FullName, "gl.bin");
        string freshShader = Path.Combine(gl.FullName, "fresh.bin");
        string otherNvidia = Path.Combine(nvidia.CreateSubdirectory("OtherCache").FullName, "other.bin");
        string directX = Path.Combine(scratch.CreateSubdirectory("D3DSCache").FullName, "directx.bin");
        DateTime now = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string path in new[] { dxShader, glShader, freshShader, otherNvidia, directX })
        {
            File.WriteAllText(path, "shader");
            File.SetLastWriteTimeUtc(path, now.AddDays(path == freshShader ? -1 : -2));
        }

        var preview = DiskCleaner.ScanLocalCache(CleanupCategory.NvidiaShaders, scratch.FullName, now.AddDays(-1));
        Require(preview.Candidates.Count == 2 && preview.Candidates.Any(file => file.Path == dxShader) &&
            preview.Candidates.Any(file => file.Path == glShader) && preview.RecentFiles == 1,
            "NVIDIA preview must include old DXCache and GLCache files only, not fresh files, sibling NVIDIA folders, or D3DSCache.");
        Require(File.Exists(dxShader) && File.Exists(glShader), "NVIDIA preview must not remove shaders.");
        Console.WriteLine("PASS NVIDIA shader cache preview");
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
}

static void CheckWindowsReports()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        string wer = Path.Combine(scratch.FullName, "Microsoft", "Windows", "WER");
        string archived = Path.Combine(Directory.CreateDirectory(Path.Combine(wer, "ReportArchive", "old")).FullName, "Report.wer");
        string queued = Path.Combine(Directory.CreateDirectory(Path.Combine(wer, "ReportQueue", "old")).FullName, "dump.mdmp");
        string recent = Path.Combine(Directory.CreateDirectory(Path.Combine(wer, "ReportQueue", "recent")).FullName, "Report.wer");
        string outside = Path.Combine(Directory.CreateDirectory(Path.Combine(wer, "Temp")).FullName, "unrelated.mdmp");
        DateTime now = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string path in new[] { archived, queued, recent, outside })
        {
            File.WriteAllText(path, "report");
            File.SetLastWriteTimeUtc(path, now.AddDays(path == recent ? -30 : -31));
        }

        var archive = DiskCleaner.ScanWindowsReports(CleanupCategory.WerArchive, scratch.FullName, now);
        var queue = DiskCleaner.ScanWindowsReports(CleanupCategory.WerQueue, scratch.FullName, now);
        Require(archive.Candidates.Count == 1 && archive.Candidates[0].Path == archived,
            "Archived report preview must not include queued reports or other WER folders.");
        Require(queue.Candidates.Count == 1 && queue.Candidates[0].Path == queued && queue.RecentFiles == 1,
            "Queued report preview must exclude the archive, other WER folders, and files not older than 30 days.");
        Require(File.Exists(archived) && File.Exists(queued), "WER preview must not remove reports.");
        Console.WriteLine("PASS Windows error report previews");
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
}

static void CheckFontCache()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        string windows = scratch.FullName;
        var serviceCache = Directory.CreateDirectory(Path.Combine(windows, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"));
        var system = scratch.CreateSubdirectory("System32");
        string serviceFont = Path.Combine(serviceCache.FullName, "fOnTcAcHe-System.dAt");
        string systemFont = Path.Combine(system.FullName, "FNTCACHE.DAT");
        string locked = Path.Combine(serviceCache.FullName, "FontCache-locked.dat");
        string[] excluded =
        [
            Path.Combine(serviceCache.FullName, "FontCache-System.dat.bak"),
            Path.Combine(serviceCache.FullName, "unrelated.dat"),
            Path.Combine(serviceCache.CreateSubdirectory("nested").FullName, "FontCache-nested.dat"),
            Path.Combine(system.FullName, "FontCache-other.dat"),
            Path.Combine(system.CreateSubdirectory("nested").FullName, "FNTCACHE.DAT"),
            Path.Combine(scratch.CreateSubdirectory("Fonts").FullName, "example.ttf")
        ];
        foreach (string path in excluded)
            File.WriteAllText(path, "leave intact");
        File.WriteAllText(serviceFont, "cache");
        File.WriteAllText(systemFont, "cache");
        File.WriteAllText(locked, "cache");

        using var openCache = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var preview = DiskCleaner.ScanWindowsFontCache(windows);
        Require(preview.Candidates.Count == 2 && preview.Candidates.Any(file => file.Path == systemFont) &&
            preview.Candidates.Any(file => file.Path == serviceFont),
            "Font cache preview must include only the exact system cache and unlocked top-level service cache databases.");
        Require(File.Exists(serviceFont) && File.Exists(systemFont) && excluded.All(File.Exists),
            "Font cache preview must not remove files.");
        Console.WriteLine("PASS Windows font cache preview");
    }
    finally
    {
        scratch.Delete(recursive: true);
    }
}

static void CheckTempLink(string root, string target)
{
    string link = Path.Combine(root, "link");
    try
    {
        Directory.CreateSymbolicLink(link, target);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        Console.WriteLine($"SKIP Temp link check: {ex.Message}");
        return;
    }

    try
    {
        string file = Path.Combine(target, "old.tmp");
        File.WriteAllText(file, "outside the cleanup root");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        var scan = DiskCleaner.Scan([root], DateTime.UtcNow.AddDays(-1));
        Require(scan.SkippedLinks == 1 && scan.Candidates.Count == 0 && File.Exists(file),
            "Temp preview should skip a directory link.");
        Console.WriteLine("PASS Temp directory link");
    }
    finally
    {
        Directory.Delete(link);
    }
}

static void CheckTweakRestore()
{
    string path = $@"SOFTWARE\CoreShift.Checks.{Guid.NewGuid():N}";
    try
    {
        using var testHive = Registry.CurrentUser.CreateSubKey(path);
        var tweak = new WindowsTweak("test", "", "", "", RegistryHive.CurrentUser, "Policy", "Setting", 0);
        using var policy = testHive.CreateSubKey(tweak.Key);

        Require(TweaksEngine.Change(testHive, tweak, "Backups", restore: false), "Could not apply to an absent value.");
        Require(!TweaksEngine.Change(testHive, tweak, "Backups", restore: false), "Repeated apply should keep the original backup.");
        Require(TweaksEngine.Change(testHive, tweak, "Backups", restore: true) && policy.GetValue(tweak.ValueName) is null,
            "Restore should remove a value that was originally absent.");

        policy.SetValue(tweak.ValueName, 3);
        Require(TweaksEngine.Change(testHive, tweak, "Backups", restore: false), "Could not apply over the original value.");
        policy.SetValue(tweak.ValueName, 2);
        try
        {
            TweaksEngine.Change(testHive, tweak, "Backups", restore: true);
            throw new Exception("Restore overwrote an external change.");
        }
        catch (InvalidOperationException)
        {
            Require(policy.GetValue(tweak.ValueName) is 2, "The external registry value changed.");
        }

        policy.SetValue(tweak.ValueName, 0);
        Require(TweaksEngine.Change(testHive, tweak, "Backups", restore: true) && policy.GetValue(tweak.ValueName) is 3,
            "Restore should recreate the exact original DWORD.");

        var limit = tweak with { Id = "limit", AppliedValue = 20, Configurable = true };
        Require(TweaksEngine.Change(testHive, limit, "Backups", restore: false) && policy.GetValue(limit.ValueName) is 20,
            "The configurable policy should apply the requested limit.");
        Require(TweaksEngine.Change(testHive, limit with { AppliedValue = 55 }, "Backups", restore: false) &&
            policy.GetValue(limit.ValueName) is 55, "The applied limit should be editable.");
        Require(TweaksEngine.Change(testHive, limit, "Backups", restore: true) && policy.GetValue(limit.ValueName) is 3,
            "Restore should use the saved applied limit and retain the original value across edits.");

        using (var backups = testHive.OpenSubKey("Backups", writable: true)!)
            backups.SetValue(limit.Id, "3|55|20");
        policy.SetValue(limit.ValueName, 20);
        Require(TweaksEngine.Change(testHive, limit, "Backups", restore: true) && policy.GetValue(limit.ValueName) is 3,
            "Restore should recover when a limit change was interrupted before writing the new value.");
        Require(TweaksEngine.SupportIssue("telemetry", "Professional", new Version(10, 0, 22631)) is not null,
            "Telemetry level 0 should be restricted by edition.");
        Require(TweaksEngine.SupportIssue("game-dvr", "Professional", new Version(10, 0, 22631)) is not null,
            "The Game DVR policy should be restricted to Windows 10.");
        Require(TweaksEngine.SupportIssue("notification-center", "Professional", new Version(10, 0, 19043, 1201)) is not null &&
            TweaksEngine.SupportIssue("notification-center", "Professional", new Version(10, 0, 19043, 1202)) is null,
            "The older Windows 10 builds require the taskbar policy update.");
        Require(TweaksEngine.SupportIssue("background-download-limit", "Professional", new Version(10, 0, 17134)) is null &&
            TweaksEngine.SupportIssue("background-download-limit", "Core", new Version(10, 0, 26100)) is not null,
            "The download limit policy should respect the documented build and edition.");
        Require(TweaksEngine.SupportIssue("search-highlights", "Professional", new Version(10, 0, 22000, 1760)) is not null &&
            TweaksEngine.SupportIssue("search-highlights", "Professional", new Version(10, 0, 22000, 1761)) is null,
            "The original Windows 11 release needs the Search Highlights update.");
        Console.WriteLine("PASS registry apply, conflicts and restore");
    }
    finally
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }
}

static void CheckPagefileIncrease()
{
    const string windowsDrive = @"C:\";
    const string systemFile = @"C:\pagefile.sys";
    const string otherFile = @"D:\pagefile.sys";
    const string setting = "PagingFiles";
    string[] automatic = [@"?:\pagefile.sys"];
    var active = new PagefileUsage[] { new(otherFile, 1024, 0, 0), new(systemFile, 4096, 0, 0) };
    var target = PagefileIncrease.SelectTarget(automatic, windowsDrive, active);
    Require(target.Automatic && target.Path == systemFile && target.Allocated == 4096 && target.Position == 1 &&
        target.OtherEntries.SequenceEqual([$"{otherFile} 0 0"]), "The automatic configuration must retain other active paging files.");

    string[] manual = [$"{otherFile} 512 2048", $"{systemFile} 2048 16384"];
    target = PagefileIncrease.SelectTarget(manual, windowsDrive, active);
    Require(!target.Automatic && target.Maximum == 16384 && target.Position == 1 &&
        target.OtherEntries.SequenceEqual([manual[0]]), "The system volume is preferred and other manual entries must be preserved.");
    var state = new PagefileIncrease.State(target, false, 4097, 8192, 16384, 12288, 0, null);
    Require(state.Accepts(8192, 16384) && !state.Accepts(4096, 16384) && !state.Accepts(8192, 12288) &&
        !state.Accepts(12289, 16384), "The new initial size must fit and increase, while the old maximum is preserved.");

    string path = $@"SOFTWARE\CoreShift.PagefileChecks.{Guid.NewGuid():N}";
    try
    {
        using var hive = Registry.CurrentUser.CreateSubKey(path);
        using var settings = hive.CreateSubKey("Settings");
        using var backups = hive.CreateSubKey("Backups");
        settings.SetValue(setting, automatic, RegistryValueKind.MultiString);
        Require(PagefileIncrease.UpdateConfiguration(settings, backups, automatic, manual), "The new configuration should be written.");

        string[] external = [$"{systemFile} 8192 16384"];
        settings.SetValue(setting, external, RegistryValueKind.MultiString);
        try
        {
            PagefileIncrease.UpdateConfiguration(settings, backups, external, null);
            throw new Exception("Restore overwrote an external change.");
        }
        catch (InvalidOperationException)
        {
            Require(((string[])settings.GetValue(setting)!).SequenceEqual(external), "The external configuration must be retained.");
        }

        settings.SetValue(setting, manual, RegistryValueKind.MultiString);
        Require(PagefileIncrease.UpdateConfiguration(settings, backups, manual, null) &&
            ((string[])settings.GetValue(setting)!).SequenceEqual(automatic), "Restore must recover the exact automatic configuration.");
        settings.DeleteValue(setting);
        Require(PagefileIncrease.UpdateConfiguration(settings, backups, null, manual) &&
            PagefileIncrease.UpdateConfiguration(settings, backups, manual, null) && settings.GetValue(setting) is null,
            "Restore must remove a value that was originally absent.");
        Console.WriteLine("PASS pagefile sizing, conflicts and restore");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
}

static void CheckPagefileAdvice()
{
    const ulong gib = 1024 * 1024 * 1024;
    var commit = new MemoryCommit(10 * gib, 12 * gib, 16 * gib);
    var dump = new CrashDumpSettings(1, false);
    string advice = MemoryAndPagefileManager.ExplainManualSizing(8 * gib, commit, dump);
    Require(advice.Contains("4096") && advice.Contains("8449"), "Manual advice must include the observed workload and full dump requirements.");
    Require(commit.AvailableBytes == 6 * gib && (commit with { CurrentBytes = 17 * gib }).AvailableBytes == 0,
        "Commit headroom must use current commit, and clamp at zero.");
    advice = MemoryAndPagefileManager.ExplainManualSizing(null, commit, dump);
    Require(!advice.Contains("4096") && advice.Contains("нужны объём ОЗУ"), "Unknown RAM should be reported as unknown.");
    Console.WriteLine("PASS pagefile advice");
}

static void CheckProcessMemoryTrim()
{
    using var child = StartIdleChild();
    try
    {
        DateTime started = child.StartTime.ToUniversalTime();
        bool rejected = false;
        try { MemoryOptimizer.TrimWorkingSet(child.Id, started.AddSeconds(-1)); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "The trim operation must reject a reused PID.");

        var (before, after) = MemoryOptimizer.TrimWorkingSet(child.Id, started);
        Require(before > 0 && after.HasValue && !child.HasExited,
            "Trim must measure the working set of the same process before and after, while it remains running.");
        Console.WriteLine("PASS process memory trim and identity");
    }
    finally
    {
        if (!child.HasExited)
            child.Kill();
    }
}

static void CheckContextProfile()
{
    using var child = StartIdleChild();
    try
    {
        _ = ContextProfileManager.ReadForegroundProcessId();
        var initial = child.PriorityClass;
        var desired = initial == ProcessPriorityClass.AboveNormal ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.AboveNormal;
        var profile = new ContextProfileManager(child.ProcessName, desired);
        var log = new List<string>();
        profile.Observe(Environment.ProcessId, log.Add);
        (int Pid, DateTime CreatedUtc)? focused = null;
        profile.Observe(child.Id, log.Add, (pid, startedUtc) => focused = (pid, startedUtc));
        child.Refresh();
        Require(focused == (child.Id, child.StartTime.ToUniversalTime()),
            "The focus profile must report the identity of the process that entered focus.");
        Require(child.PriorityClass == desired, string.Join(Environment.NewLine, log));

        profile.Observe(Environment.ProcessId, log.Add);
        child.Refresh();
        Require(child.PriorityClass == initial, "Losing focus did not restore the priority.");

        profile.Observe(child.Id, log.Add);
        Require(profile.Restore(log.Add), "Stopping the profile failed to restore priority.");
        child.Refresh();
        Require(child.PriorityClass == initial, "Stopping the profile did not restore priority.");
        MemoryOptimizer.TrimWorkingSet(child.Id);

        profile.Observe(null, log.Add);
        profile.Observe(child.Id, log.Add);
        child.StandardInput.Close();
        Require(child.WaitForExit(5000), "The verification child did not stop.");
        profile.Observe(null, log.Add);
        Require(profile.Restore(log.Add), "The profile should recover when the tracked process exits.");
        Console.WriteLine("PASS priority on focus change, stop and process exit");
    }
    finally
    {
        if (!child.HasExited)
            child.Kill();
    }
}

static void CheckContextProfileStopsOnSelectedExit()
{
    using var selected = StartIdleChild();
    using var other = StartIdleChild();
    using var mutex = new Mutex();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<ContextProfileManager.WatchResult>? watching = null;
    try
    {
        var identity = new ContextProfileManager.SelectedProcess(selected.Id, selected.StartTime.ToUniversalTime());
        var stale = new ContextProfileManager(selected.ProcessName, ProcessPriorityClass.AboveNormal,
            selected: identity with { StartedUtc = identity.StartedUtc.AddSeconds(-1) });
        var staleResult = stale.Watch(mutex, stop.Token, () => throw new InvalidOperationException("The PID belonged to a different process."),
            _ => { });
        Require(staleResult is { Restored: true, ProcessExited: true }, "A stale selection must be treated as closed.");

        var profile = new ContextProfileManager(selected.ProcessName, ProcessPriorityClass.AboveNormal, selected: identity);
        bool focused = false;
        profile.Observe(other.Id, _ => { }, (_, _) => focused = true);
        Require(!focused, "Another instance of the same executable must not receive the selected profile.");

        // The selected child has no window. Its exit must stop the watcher even without a focus change.
        watching = Task.Run(() => profile.Watch(mutex, stop.Token, started.SetResult, _ => { }));
        var first = Task.WhenAny(started.Task, watching, Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        if (first == watching)
        {
            var result = watching.GetAwaiter().GetResult();
            throw new InvalidOperationException($"The selected profile ended before startup: {result}.");
        }
        Require(first == started.Task, "The selected profile did not start.");
        Require(!watching.IsCompleted, "A running background application must keep its profile.");
        selected.StandardInput.Close();
        Require(watching.Wait(TimeSpan.FromSeconds(5)) && watching.Result is { Restored: true, ProcessExited: true },
            "The profile did not stop automatically after its selected process exited.");
        Require(!other.HasExited, "A running instance of the same executable must not affect automatic stop.");

        using var manualStop = new CancellationTokenSource();
        var next = new ContextProfileManager(other.ProcessName, ProcessPriorityClass.AboveNormal,
            selected: new ContextProfileManager.SelectedProcess(other.Id, other.StartTime.ToUniversalTime()));
        Require(next.Watch(mutex, manualStop.Token, manualStop.Cancel, _ => { }) is { Restored: true, ProcessExited: false },
            "An automatically stopped profile must release control for the next profile.");
        Console.WriteLine("PASS automatic focus profile stop on selected background process exit");
    }
    finally
    {
        stop.Cancel();
        if (!selected.HasExited)
            selected.Kill();
        if (!other.HasExited)
            other.Kill();
        if (watching is { IsCompleted: false })
            watching.Wait(TimeSpan.FromSeconds(5));
    }
}

static Process StartIdleChild()
{
    string host = Environment.ProcessPath!;
    var start = new ProcessStartInfo(host)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true
    };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--idle");
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start the verification child.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
