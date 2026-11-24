using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CoreShift.Desktop;

internal sealed record AppRelease(Version Version, Uri Installer, long Size, byte[] Sha256);

internal static class AppUpdater
{
    private const string Repository = "MatheWwWWW/CoreShift";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{998EEA5D-ED30-49E3-A992-FF2AC2A9D7BB}_is1";
    private const long MaxInstallerSize = 250 * 1024 * 1024;
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    private sealed record GitHubAsset(string Name, string State, long Size, string? Digest);
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        bool Draft, bool Prerelease, GitHubAsset[] Assets);

    static AppUpdater() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("CoreShift-Updater");

    internal static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string { Length: > 0 } location &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                    Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    internal static async Task<AppRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
        var current = typeof(AppUpdater).Assembly.GetName().Version!;
        if (release is not { Draft: false, Prerelease: false, TagName: { Length: > 0 }, Assets: not null })
            return null;

        string tag = release.TagName;
        string number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(number, out var version) || version.Build < 0 || version.Revision != -1 ||
            number != version.ToString(3) || new Version(version.Major, version.Minor, version.Build, 0) <= current)
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

    internal static string CreateInstallerPath()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CoreShift-Updates")).FullName;
        foreach (var previous in Directory.EnumerateFiles(folder, "CoreShift-Update-*.exe"))
        {
            if (File.GetLastWriteTimeUtc(previous) < DateTime.UtcNow.AddDays(-1))
                TryDeleteInstaller(previous);
        }

        return Path.Combine(folder, $"CoreShift-Update-{Guid.NewGuid():N}.exe");
    }

    internal static async Task<FileStream> DownloadAsync(AppRelease release, string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;

        using var response = await Client.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            using var input = await response.Content.ReadAsStreamAsync(token);
            byte[] buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                size += read;
                if (size > release.Size)
                    throw new InvalidDataException("The installer is larger than the release asset.");

                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }

        // Keep the verified file open for reading so it cannot be replaced before launch.
        var installer = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            if (installer.Length != release.Size || !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(installer, token), release.Sha256))
                throw new InvalidDataException("The installer does not match the release asset.");

            return installer;
        }
        catch
        {
            installer.Dispose();
            throw;
        }
    }

    internal static void LaunchInstaller(string path)
    {
        var start = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) };
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/SP-");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/RESTARTEXITCODE=3010");
        start.ArgumentList.Add($"/DIR={Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}");
        start.ArgumentList.Add($"/CORESHIFTUPDATEPID={Environment.ProcessId}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CoreShift Setup.");
    }

    internal static void TryDeleteInstaller(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }
}
