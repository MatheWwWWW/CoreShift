using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

if (args is ["--idle"])
{
    Console.ReadLine();
    return;
}

if (args is ["--explorer-cache"])
{
    CheckExplorerCache();
    return;
}

CheckTempCleanup();
CheckExplorerCache();
CheckTweakRestore();
CheckPagefileAdvice();
CheckContextProfile();

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

static void CheckExplorerCache()
{
    var scratch = Directory.CreateTempSubdirectory("CoreShift-check-");
    try
    {
        string root = scratch.FullName;
        string nested = scratch.CreateSubdirectory("nested").FullName;
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
        var thumbnails = DiskCleaner.ScanExplorerCache(root, CleanupCategory.ExplorerThumbnails, cutoff);
        var icons = DiskCleaner.ScanExplorerCache(root, CleanupCategory.ExplorerIcons, cutoff);
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
        Require(TweaksEngine.SupportIssue("telemetry", "Professional", new Version(10, 0, 22631)) is not null,
            "Telemetry level 0 should be restricted by edition.");
        Require(TweaksEngine.SupportIssue("game-dvr", "Professional", new Version(10, 0, 22631)) is not null,
            "The Game DVR policy should be restricted to Windows 10.");
        Require(TweaksEngine.SupportIssue("notification-center", "Professional", new Version(10, 0, 19043, 1201)) is not null &&
            TweaksEngine.SupportIssue("notification-center", "Professional", new Version(10, 0, 19043, 1202)) is null,
            "The older Windows 10 builds require the taskbar policy update.");
        Console.WriteLine("PASS registry apply, conflicts and restore");
    }
    finally
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }
}

static void CheckPagefileAdvice()
{
    const ulong gib = 1024 * 1024 * 1024;
    var commit = new MemoryCommit(12 * gib, 16 * gib);
    var dump = new CrashDumpSettings(1, false);
    string advice = MemoryAndPagefileManager.ExplainManualSizing(8 * gib, commit, dump);
    Require(advice.Contains("4096") && advice.Contains("8449"), "Manual advice must include the observed workload and full dump requirements.");
    advice = MemoryAndPagefileManager.ExplainManualSizing(null, commit, dump);
    Require(!advice.Contains("4096") && advice.Contains("нужны объём ОЗУ"), "Unknown RAM should be reported as unknown.");
    Console.WriteLine("PASS pagefile advice");
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
        profile.Observe(child.Id, log.Add);
        child.Refresh();
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
