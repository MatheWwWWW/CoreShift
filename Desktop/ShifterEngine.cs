using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CoreShift.Desktop;

internal sealed class ShifterEngine : IDisposable
{
    internal const string Model = "shifter";
    private const uint JobKillOnClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;
    private readonly Process process;
    private readonly SafeFileHandle job;
    internal HttpClient Client { get; }

    private ShifterEngine(Process process, SafeFileHandle job, HttpClient client)
    {
        this.process = process;
        this.job = job;
        Client = client;
    }

    internal static string AssetsDirectory => Path.Combine(AppContext.BaseDirectory, "Shifter");

    internal static bool IsInstalled =>
        File.Exists(Path.Combine(AssetsDirectory, "llama-server.exe")) &&
        File.Exists(Path.Combine(AssetsDirectory, "model.gguf")) &&
        File.Exists(Path.Combine(AssetsDirectory, "vision.gguf"));

    internal static async Task<ShifterEngine> StartAsync(bool withImage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo(Path.Combine(AssetsDirectory, "llama-server.exe"))
        {
            WorkingDirectory = AssetsDirectory,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        foreach (string variable in startInfo.Environment.Keys.Where(key => key.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase)).ToArray())
            startInfo.Environment.Remove(variable);

        string threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(CultureInfo.InvariantCulture);
        foreach (string argument in new[] { "--model", Path.Combine(AssetsDirectory, "model.gguf"), "--alias", Model,
                     "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture), "--api-key", key,
                     "--ctx-size", "4096", "--threads", threads, "--threads-batch", threads, "--parallel", "1",
                     "--n-gpu-layers", "0", "--no-webui", "--no-warmup", "--no-context-shift", "--log-disable" })
            startInfo.ArgumentList.Add(argument);

        if (withImage)
        {
            startInfo.ArgumentList.Add("--mmproj");
            startInfo.ArgumentList.Add(Path.Combine(AssetsDirectory, "vision.gguf"));
            startInfo.ArgumentList.Add("--image-max-tokens");
            startInfo.ArgumentList.Add("1280");
        }

        var job = CreateKillOnCloseJob();
        var process = new Process { StartInfo = startInfo };
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromMinutes(4),
            MaxResponseContentBufferSize = 1_048_576
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var worker = new ShifterEngine(process, job, client);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The Shifter worker could not start.");
            if (!AssignProcessToJobObject(job, process.SafeHandle))
            {
                int error = Marshal.GetLastWin32Error();
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                throw new Win32Exception(error);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            while (!process.HasExited)
            {
                try
                {
                    using var response = await client.GetAsync("health", deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && !process.HasExited)
                        return worker;
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
            throw new InvalidOperationException($"The Shifter worker exited with code {process.ExitCode}.");
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        job.Dispose();
        process.Dispose();
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobLimits { Basic = new BasicJobLimits { Flags = JobKillOnClose } };
        if (!job.IsInvalid && SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            return job;
        int error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicJobLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
