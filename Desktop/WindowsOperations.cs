using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CoreShift.Desktop;

internal static class WindowsOperations
{
    internal sealed record Result(int ExitCode, string Output, string Error);
    internal sealed record Package(string Name, string DisplayName, string FullName, string Family, string Version);

    private static readonly HashSet<string> OptionalApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.MicrosoftSolitaireCollection", "Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.BingWeather",
        "Microsoft.WindowsMaps", "Microsoft.WindowsFeedbackHub", "Microsoft.MicrosoftStickyNotes", "Microsoft.GetHelp",
        "Microsoft.Getstarted", "Microsoft.People", "Microsoft.SkypeApp", "Microsoft.WindowsSoundRecorder",
        "Microsoft.WindowsAlarms", "Microsoft.OutlookForWindows", "Microsoft.PowerAutomateDesktop", "Microsoft.Todos",
        "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", "Microsoft.YourPhone", "Microsoft.Windows.DevHome",
        "Microsoft.Microsoft3DViewer", "Microsoft.3DBuilder", "Microsoft.MSPaint", "Microsoft.Office.OneNote",
        "microsoft.windowscommunicationsapps", "Microsoft.Messaging", "Microsoft.MixedReality.Portal"
    };

    internal static bool CanRemove(Package app) => OptionalApps.Contains(app.Name);

    internal static async Task<Package[]> ReadPackagesAsync()
    {
        var result = await PowerShellAsync("""
            $appNames = @{}
            try {
                Get-StartApps | ForEach-Object {
                    if ($_.AppID -and $_.AppID.Contains('!') -and $_.Name) {
                        $family = $_.AppID.Split('!')[0]
                        if (-not $appNames.ContainsKey($family)) { $appNames[$family] = $_.Name }
                    }
                }
            } catch { }
            $packages = @(Get-AppxPackage -PackageTypeFilter Main | Where-Object {
                -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable
            } | ForEach-Object {
                $displayName = $appNames[$_.PackageFamilyName]
                if (-not $displayName) { $displayName = $_.Name }
                [PSCustomObject]@{ Name = $_.Name; DisplayName = $displayName; FullName = $_.PackageFullName;
                    Family = $_.PackageFamilyName; Version = $_.Version.ToString() }
            })
            ConvertTo-Json -InputObject $packages -Compress
            """);
        EnsureSuccess(result);
        return JsonSerializer.Deserialize<Package[]>(result.Output.Trim()) ?? [];
    }

    internal static async Task RemovePackageAsync(Package package)
    {
        if (!CanRemove(package) || !package.FullName.StartsWith(package.Name + "_", StringComparison.OrdinalIgnoreCase) ||
            !package.FullName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
            throw new InvalidOperationException("This package is outside the supported removal list.");

        var result = await PowerShellAsync($$"""
            $package = Get-AppxPackage -Name '{{package.Name}}' | Where-Object { $_.PackageFullName -eq '{{package.FullName}}' }
            if ($package -and -not $package.NonRemovable -and -not $package.IsFramework) {
                Remove-AppxPackage -Package $package.PackageFullName
            }
            """);
        EnsureSuccess(result);
    }

    internal static Task<Result> CreateRestorePointAsync() => PowerShellAsync("""
        $existing = @(Get-ComputerRestorePoint | ForEach-Object SequenceNumber)
        Checkpoint-Computer -Description 'CoreShift' -RestorePointType 'MODIFY_SETTINGS'
        $created = Get-ComputerRestorePoint | Where-Object { $_.SequenceNumber -notin $existing } | Select-Object -First 1
        if (-not $created) { throw 'Windows did not create a new restore point. Check System Protection and recent restore points.' }
        $created | Format-List Description, SequenceNumber, CreationTime | Out-String
        """);

    private static Task<Result> PowerShellAsync(string script)
    {
        const string prologue = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); try {\n";
        const string epilogue = "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prologue + script + epilogue));
        return RunAsync(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], Encoding.UTF8);
    }

    private static void EnsureSuccess(Result result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
    }

    internal static async Task<Result> RunAsync(string executable, string[] arguments, Encoding? encoding = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the requested tool.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new Result(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
