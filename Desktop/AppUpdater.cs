using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CoreShift.Desktop;

internal sealed record AppRelease(Version Version, Uri Installer, long Size, byte[] Sha256);

internal static class AppUpdater
{
    internal const string WorkerMutex = @"Local\CoreShift.998EEA5D.Update";
    internal const string WorkerReadyPrefix = @"Local\CoreShift.998EEA5D.UpdateReady.";
    internal const string ExecutableName = "CoreShift.Desktop.exe";
    private const string Repository = "MatheWwWWW/CoreShift";
    private const string LatestReleasePath = "latest";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{998EEA5D-ED30-49E3-A992-FF2AC2A9D7BB}_is1";
    private const long MaxInstallerSize = 2_000_000_000;
    private static readonly string[] WorkerLibraries =
        ["DirectWriteForwarder.dll", "System.Printing.dll", "ijwhost.dll", "vcruntime140_cor3.dll"];
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    private sealed record GitHubAsset(string Name, string State, long Size, string? Digest);
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        bool Draft, bool Prerelease, GitHubAsset[] Assets, string? Body);

    static AppUpdater() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("CoreShift-Updater");

    internal static Version CurrentVersion { get; } = typeof(AppUpdater).Assembly.GetName().Version!;

    internal static bool IsInstalledCopy() => string.Equals(InstalledDirectory(),
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);

    internal static string? InstalledDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string { Length: > 0 } location
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    internal static async Task<AppRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        var release = await FetchReleaseAsync(LatestReleasePath, cancellationToken);
        if (release is not { Draft: false, Prerelease: false, TagName: { Length: > 0 }, Assets: not null })
            return null;

        string tag = release.TagName;
        string number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(number, out var version) || version.Build < 0 || version.Revision != -1 ||
            number != version.ToString(3) || new Version(version.Major, version.Minor, version.Build, 0) <= CurrentVersion)
            return null;

        string name = $"CoreShift-Setup-{version}-x64.exe";
        var asset = release.Assets.FirstOrDefault(asset => asset.Name == name && asset.State == "uploaded");
        if (asset is not { Size: > 0 and <= MaxInstallerSize, Digest: { Length: 71 } digest } ||
            !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            return null;

        return new AppRelease(version, new Uri($"https://github.com/{Repository}/releases/download/{tag}/{name}"),
            asset.Size, Convert.FromHexString(digest.AsSpan(7)));
    }

    internal static async Task<string?> FetchReleaseNotesAsync(CancellationToken cancellationToken)
    {
        string number = CurrentVersion.ToString(3);
        var release = await FetchReleaseAsync($"tags/{number}", cancellationToken)
            ?? await FetchReleaseAsync($"tags/v{number}", cancellationToken);
        return release is { Draft: false, Body: { } body } && !string.IsNullOrWhiteSpace(body) ? body : null;
    }

    private static async Task<GitHubRelease?> FetchReleaseAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/{path}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (path == LatestReleasePath)
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
    }

    private static string UpdateFolder => Path.Combine(Path.GetTempPath(), "CoreShift-Updates");

    internal static Task StartWorkerAsync(Version version) => Task.Run(() =>
    {
        if (Environment.ProcessPath is not { } exe || typeof(AppUpdater).Assembly.Location.Length != 0)
            throw new InvalidOperationException("The update worker requires the standalone CoreShift executable.");

        Directory.CreateDirectory(UpdateFolder);
        foreach (var previous in Directory.EnumerateFiles(UpdateFolder, "CoreShift-Update*.*"))
        {
            if (File.GetLastWriteTimeUtc(previous) < DateTime.UtcNow.AddDays(-1))
                TryDelete(previous);
        }
        foreach (var previous in Directory.EnumerateDirectories(UpdateFolder, "CoreShift-Updater-*"))
        {
            string name = Path.GetFileName(previous);
            if (Guid.TryParseExact(name.AsSpan("CoreShift-Updater-".Length), "N", out _) &&
                (File.GetAttributes(previous) & FileAttributes.ReparsePoint) == 0 &&
                Directory.GetCreationTimeUtc(previous) < DateTime.UtcNow.AddDays(-1))
                DeleteWorkerFiles(previous);
        }

        string id = Guid.NewGuid().ToString("N");
        string workerFolder = Directory.CreateDirectory(Path.Combine(UpdateFolder, $"CoreShift-Updater-{id}")).FullName;
        string workerPath = Path.Combine(workerFolder, $"CoreShift-Updater-{id}.exe");
        string readyName = WorkerReadyPrefix + id;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var lockedFiles = new List<FileStream>();
        bool started = false;
        try
        {
            StageFile(exe, workerPath);
            string installedFolder = Path.GetDirectoryName(exe)!;
            foreach (string library in WorkerLibraries)
                StageFile(Path.Combine(installedFolder, library), Path.Combine(workerFolder, library));

            using var process = StartProcess(workerPath, ["--update", Environment.ProcessId.ToString(), readyName, version.ToString(3)]);
            var elapsed = Stopwatch.StartNew();
            while (!ready.WaitOne(200))
            {
                if (process.HasExited || elapsed.Elapsed >= TimeSpan.FromSeconds(20))
                    throw new InvalidOperationException("The update worker did not start.");
            }
            started = true;
        }
        finally
        {
            foreach (var file in lockedFiles)
                file.Dispose();
            if (!started)
                DeleteWorkerFiles(workerFolder);
        }

        void StageFile(string sourcePath, string destination)
        {
            using var source = File.OpenRead(sourcePath);
            using (var copy = File.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(copy);

            var staged = File.OpenRead(destination);
            lockedFiles.Add(staged);
            source.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(staged)))
                throw new InvalidDataException("The update worker does not match the installed files.");
        }
    });

    private static void DeleteWorkerFiles(string folder)
    {
        TryDelete(Path.Combine(folder, Path.GetFileName(folder) + ".exe"));
        foreach (string library in WorkerLibraries)
            TryDelete(Path.Combine(folder, library));
        try { Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    internal static string CreateInstallerPath() => Path.Combine(Directory.CreateDirectory(UpdateFolder).FullName,
        $"CoreShift-Update-{Guid.NewGuid():N}.exe");

    internal static async Task<FileStream> DownloadAsync(AppRelease release, string path, CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var token = timeout.Token;

        using var response = await Client.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long size = 0;
            int lastProgress = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                size += read;
                if (size > release.Size)
                    throw new InvalidDataException("The installer is larger than the release asset.");

                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                int percent = (int)(size * 100 / release.Size);
                if (percent != lastProgress)
                {
                    progress?.Report(percent);
                    lastProgress = percent;
                }
            }
        }

        // Keep the verified file open for reading so it cannot be replaced before launch.
        var installer = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            if (installer.Length != release.Size || !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(installer, token).ConfigureAwait(false), release.Sha256))
                throw new InvalidDataException("The installer does not match the release asset.");

            return installer;
        }
        catch
        {
            installer.Dispose();
            throw;
        }
    }

    internal static Process StartInstaller(string path, string installDirectory, string logPath) => StartProcess(path,
        ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NOCANCEL", "/SP-", "/NORESTART", "/RESTARTEXITCODE=3010",
         $"/DIR={installDirectory}", $"/LOG={logPath}"]);

    internal static Process StartProcess(string path, string[] arguments)
    {
        var start = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(path)}.");
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    internal static bool IsUpdateError(Exception ex) => ex is ArgumentException or IOException or UnauthorizedAccessException
        or InvalidOperationException or InvalidDataException or Win32Exception or SecurityException or HttpRequestException
        or JsonException or OperationCanceledException;
}
