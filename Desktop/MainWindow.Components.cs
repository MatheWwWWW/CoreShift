using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using LibreHardwareMonitor.Hardware;
using SmartResourceDispatcher.Core;
using Forms = System.Windows.Forms;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private sealed record ComponentInventory(string? Cpu, uint? Cores, uint? Threads, string? Windows,
        int Displays, int DisplayWidth, int DisplayHeight,
        NetworkInterfaceType? NetworkType, string? NetworkName, long? NetworkSpeed, bool? OnAc, float? Battery, BoardDrive[] Drives);

    private ComponentInventory? componentInventory;
    private bool readingComponents;

    private async void RefreshComponentsClick(object sender, RoutedEventArgs e)
    {
        await RefreshComponentsAsync();
        await RefreshBoardGpuAsync(force: true);
        lastDiskRead = 0;
        await RefreshOverviewAsync();
    }

    private async Task RefreshComponentsAsync()
    {
        if (readingComponents || closing)
            return;

        readingComponents = true;
        RefreshComponentsButton.IsEnabled = false;
        try
        {
            componentInventory = await RunInBackground(ReadComponentInventory);
            if (!closing)
                RenderComponents();
        }
        catch (Exception ex) when (IsSystemError(ex) || ex is NetworkInformationException)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            readingComponents = false;
            RefreshComponentsButton.IsEnabled = !closing;
        }
    }

    private static ComponentInventory ReadComponentInventory()
    {
        var cpu = ReadWmi("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor",
            item => (Name: WmiText(item, "Name"), Cores: AsUInt(item["NumberOfCores"]), Threads: AsUInt(item["NumberOfLogicalProcessors"])))
            .FirstOrDefault();
        var windows = ReadWmiValues("SELECT Caption FROM Win32_OperatingSystem", item => WmiText(item, "Caption"));
        var screen = Forms.Screen.PrimaryScreen;
        var network = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .OrderByDescending(item => item.Speed).FirstOrDefault();
        var power = Forms.SystemInformation.PowerStatus;
        bool? ac = power.PowerLineStatus switch
        {
            Forms.PowerLineStatus.Online => true,
            Forms.PowerLineStatus.Offline => false,
            _ => null
        };
        float? battery = power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) ||
            power.BatteryChargeStatus == Forms.BatteryChargeStatus.Unknown ? null : power.BatteryLifePercent;
        var cpuName = cpu.Name?.Replace("(R)", "", StringComparison.Ordinal).Replace("(TM)", "", StringComparison.Ordinal);
        if (cpuName is { } model && model.IndexOf(" CPU @", StringComparison.Ordinal) is >= 0 and var suffix)
            cpuName = model[..suffix];

        return new ComponentInventory(cpuName, cpu.Cores, cpu.Threads,
            windows?.Replace("Microsoft ", "", StringComparison.Ordinal), Forms.Screen.AllScreens.Length,
            screen?.Bounds.Width ?? 0, screen?.Bounds.Height ?? 0, network?.NetworkInterfaceType,
            network?.Name, network?.Speed, ac, battery, ReadBoardDrives());
    }

    private static (float? Load, float? Temperature) ReadDashboardGpu(string gpuName)
    {
        Computer? computer = null;
        try
        {
            computer = new Computer { IsGpuEnabled = true };
            computer.Open();
            var hardware = computer.Hardware.FirstOrDefault(item =>
                item.Name.Contains(gpuName, StringComparison.OrdinalIgnoreCase) || gpuName.Contains(item.Name, StringComparison.OrdinalIgnoreCase));
            if (hardware is null)
                return (null, null);

            hardware.Update();
            float? load = hardware.Sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Load && sensor.Name == "GPU Core")?.Value;
            float? temperature = hardware.Sensors.FirstOrDefault(sensor =>
                sensor.SensorType == SensorType.Temperature && sensor.Name == "GPU Core")?.Value;
            return (load is >= 0 and <= 100 ? load : null, temperature is > 0 and < 150 ? temperature : null);
        }
        catch (Exception ex) when (IsSystemError(ex) || ex is DllNotFoundException or TypeInitializationException)
        {
            Debug.WriteLine(ex);
            return (null, null);
        }
        finally
        {
            try { computer?.Close(); }
            catch (Exception ex) when (IsSystemError(ex)) { Debug.WriteLine(ex); }
        }
    }

    private void RenderComponents()
    {
        if (componentInventory is not { } data)
            return;

        ComponentDisplay.Text = data.DisplayWidth > 0 ? $"{data.DisplayWidth} × {data.DisplayHeight}" : Unavailable;
        ComponentDisplayDetail.Text = UiText.Format($"Дисплеев: {data.Displays}");
        ComponentNetwork.Text = data.NetworkType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet => "Ethernet",
            _ => "—"
        };
        string? networkName = string.Equals(data.NetworkName, ComponentNetwork.Text, StringComparison.OrdinalIgnoreCase) ? null : data.NetworkName;
        ComponentNetworkDetail.Text = data.NetworkSpeed is > 0 and var speed
            ? JoinDetails(networkName, UiText.Format($"{speed / 1_000_000d:0.#} Мбит/с"))!
            : UiText.Get("Нет активного подключения");
        ComponentPower.Text = data.OnAc switch
        {
            true => UiText.Get("Питание от сети"),
            false => UiText.Get("Питание от батареи"),
            _ => "—"
        };
        ComponentPowerDetail.Text = data.Battery is >= 0 and <= 1 and var battery ? UiText.Format($"Батарея · {battery:P0}") : "—";
        ComponentPowerDetail.Visibility = VisibleWhen(data.Battery is >= 0 and <= 1);
        ComponentWindows.Text = data.Windows ?? "Windows";
        UpdateBoardReadings();
    }
}
