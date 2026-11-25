using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using LibreHardwareMonitor.Hardware;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string Unavailable = "Недоступно";

    private sealed record PcInfoRow(string Label, string Value);
    private sealed record PcInfoSection(string Title, IReadOnlyList<PcInfoRow> Rows);
    private sealed record MemoryModule(string Slot, string? Model, string? Type, ulong? Capacity,
        uint? ConfiguredSpeed, uint? RatedSpeed, uint? ConfiguredMillivolts, uint Ranks)
    {
        public string Description => JoinDetails(Type, Capacity is { } bytes ? FormatHardwareCapacity(bytes) : null,
            ConfiguredSpeed is { } configured ? $"{configured:N0} МТ/с (настроено)" : null,
            RatedSpeed is { } rated && rated != ConfiguredSpeed ? $"{rated:N0} МТ/с (по SMBIOS)" : null,
            Ranks > 0 ? $"{Ranks}R" : null, Model)
            ?? Unavailable;
    }

    private async Task LoadPcInfoAsync()
    {
        loadingPcInfo = true;
        try
        {
            var sections = await RunInBackground(ReadPcInfo);
            if (closing)
                return;

            PcInfoItems.ItemsSource = sections;
            PcInfoStatus.Visibility = Visibility.Collapsed;
        }
        finally
        {
            loadingPcInfo = false;
        }
    }

    private static PcInfoSection[] ReadPcInfo()
    {
        string windows = ReadWmiValues("SELECT Caption FROM Win32_OperatingSystem", item => WmiText(item, "Caption"))
            ?? "Windows";
        string? board = ReadWmiValues("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", item =>
            JoinDetails(WmiText(item, "Manufacturer"), WmiText(item, "Product"),
                WmiText(item, "Version") is { } version and not "x.x" ? $"рев. {version}" : null));
        string? bios = ReadWmiValues("SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS", item =>
            JoinDetails(WmiText(item, "Manufacturer"), WmiText(item, "SMBIOSBIOSVersion")));
        var processors = ReadWmi("SELECT Name, CurrentVoltage FROM Win32_Processor", item =>
            (Name: WmiText(item, "Name"), Voltage: ReadFirmwareCpuVoltage(item["CurrentVoltage"])));
        string[] processorNames = processors.Select(processor => processor.Name).OfType<string>().ToArray();
        string? gpu = ReadWmiValues("SELECT Name FROM Win32_VideoController", item => WmiText(item, "Name"));
        string? disks = ReadWmiValues("SELECT Model, Size FROM Win32_DiskDrive", item =>
            JoinDetails(WmiText(item, "Model"), item["Size"] is ulong bytes && bytes > 0 ? FormatBytes(bytes) : null));

        var modules = ReadWmi(
            "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, SMBIOSMemoryType, Capacity, ConfiguredClockSpeed, Speed, " +
            "ConfiguredVoltage, Attributes FROM Win32_PhysicalMemory", ReadMemoryModule);
        string installed = modules.Count > 0 && modules.All(module => module.Capacity.HasValue)
            ? FormatHardwareCapacity(modules.Aggregate(0UL, (total, module) => total + module.Capacity.GetValueOrDefault()))
            : Unavailable;
        string usable = Unavailable;
        try
        {
            usable = FormatHardwareCapacity(MemoryAndPagefileManager.ReadMemorySnapshot().Physical.TotalBytes);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
        }

        var memoryRows = new List<PcInfoRow>
        {
            new("Установлено", installed),
            new("Объём для Windows", usable),
            new("Установлено модулей", modules.Count > 0 ? modules.Count.ToString() : Unavailable)
        };
        memoryRows.AddRange(modules.Select(module => new PcInfoRow(module.Slot, module.Description)));

        var timings = new List<PcInfoRow>();
        var voltages = new List<PcInfoRow>();
        ReadHardwareSensors(timings, voltages);
        memoryRows.Add(new("Тайминги SPD", timings.Count > 0
            ? string.Join(Environment.NewLine, timings.Select(row => $"{row.Label} · {row.Value}"))
            : "Недоступны для текущей конфигурации"));

        if (voltages.Count == 0)
            voltages.Add(new("Показания датчиков", "Недоступны для текущей конфигурации"));
        var cpuVoltages = processors.Where(processor => processor.Voltage.HasValue)
            .Select(processor => JoinDetails(processor.Name, $"{processor.Voltage:0.0} В")).ToArray();
        if (cpuVoltages.Length > 0)
            voltages.Add(new("ЦП по данным SMBIOS", string.Join(Environment.NewLine, cpuVoltages)));
        string[] configuredVoltages = modules.Where(module => module.ConfiguredMillivolts.HasValue)
            .Select(module => $"{module.Slot} · {module.ConfiguredMillivolts.GetValueOrDefault() / 1000d:0.###} В").ToArray();
        voltages.Add(new("ОЗУ по настройкам BIOS", configuredVoltages.Length > 0
            ? string.Join(Environment.NewLine, configuredVoltages)
            : Unavailable));

        return
        [
            new("Система",
            [
                new("Имя компьютера", Environment.MachineName),
                new("Операционная система", $"{windows} · сборка {Environment.OSVersion.Version.Build} · {RuntimeInformation.OSArchitecture}"),
                new("Материнская плата", board ?? Unavailable),
                new("BIOS / UEFI", bios ?? Unavailable),
                new("Процессоры", processorNames.Length > 0 ? string.Join(Environment.NewLine, processorNames) : Unavailable),
                new("Видеоадаптеры", gpu ?? Unavailable),
                new("Накопители", disks ?? Unavailable)
            ]),
            new("Оперативная память", memoryRows),
            new("Напряжения", voltages)
        ];
    }

    private static MemoryModule ReadMemoryModule(ManagementBaseObject item) => new(
        JoinDetails(WmiText(item, "DeviceLocator"), WmiText(item, "BankLabel")) ?? "Модуль памяти",
        JoinDetails(WmiText(item, "Manufacturer"), WmiText(item, "PartNumber")),
        (item["SMBIOSMemoryType"] as uint?) switch
        {
            20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4",
            27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4",
            34 => "DDR5", 35 => "LPDDR5",
            _ => null
        },
        item["Capacity"] is ulong bytes && bytes > 0 ? bytes : null,
        PositiveUInt(item["ConfiguredClockSpeed"]), PositiveUInt(item["Speed"]), PositiveUInt(item["ConfiguredVoltage"]),
        (item["Attributes"] as uint? ?? 0) & 0x0F);

    private static double? ReadFirmwareCpuVoltage(object? value)
    {
        // WMI can return the decoded tenths of a volt, or the SMBIOS byte with its high bit set.
        if (value is not ushort raw || raw > byte.MaxValue)
            return null;

        int tenths = raw & 0x7F;
        return tenths > 0 ? tenths / 10d : null;
    }

    private static string FormatHardwareCapacity(ulong bytes) => $"{bytes / (1024d * 1024 * 1024):0.##} ГиБ";

    private static uint? PositiveUInt(object? value) => value is uint number && number > 0 ? number : null;

    private static string? WmiText(ManagementBaseObject item, string property) =>
        (item[property] as string)?.Trim() is { Length: > 0 } value ? value : null;

    private static string? JoinDetails(params string?[] details) =>
        string.Join(" · ", details.OfType<string>()) is { Length: > 0 } value ? value : null;

    private static string? ReadWmiValues(string query, Func<ManagementBaseObject, string?> format)
    {
        var values = ReadWmi(query, format).OfType<string>().ToArray();
        return values.Length > 0 ? string.Join(Environment.NewLine, values) : null;
    }

    private static List<T> ReadWmi<T>(string query, Func<ManagementBaseObject, T> read)
    {
        var values = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                    values.Add(read(item));
            }
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
            return [];
        }

        return values;
    }

    private static void ReadHardwareSensors(List<PcInfoRow> timings, List<PcInfoRow> voltages)
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true
        };
        try
        {
            bool hasPawnIo = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
            computer.Open();
            foreach (var hardware in computer.Hardware)
                ReadHardwareSensors(hardware, hardware.Name, hasPawnIo, timings, voltages);
        }
        catch (Exception ex) when (IsSensorError(ex))
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            try { computer.Close(); }
            catch (Exception ex) when (IsSensorError(ex)) { Debug.WriteLine(ex); }
        }
    }

    private static void ReadHardwareSensors(IHardware hardware, string componentName, bool hasPawnIo,
        List<PcInfoRow> timings, List<PcInfoRow> voltages)
    {
        try
        {
            hardware.Update();
            if (hardware.HardwareType != HardwareType.Cpu || hasPawnIo)
            {
                var voltageValues = hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Voltage &&
                    !sensor.Name.Contains("VID", StringComparison.OrdinalIgnoreCase) &&
                    sensor.Value is float voltage && float.IsFinite(voltage) && voltage != 0)
                    .Select(sensor => $"{sensor.Name} · {sensor.Value:0.###} В").ToArray();
                if (voltageValues.Length > 0)
                    voltages.Add(new(componentName, string.Join(Environment.NewLine, voltageValues)));
            }

            if (hardware.HardwareType == HardwareType.Memory && FormatSpdTimings(hardware.Sensors) is { } spd)
                timings.Add(new(hardware.Name, spd));
        }
        catch (Exception ex) when (IsSensorError(ex))
        {
            Debug.WriteLine(ex);
        }

        foreach (var child in hardware.SubHardware)
            ReadHardwareSensors(child, componentName, hasPawnIo, timings, voltages);
    }

    private static string? FormatSpdTimings(ISensor[] sensors)
    {
        var timings = new List<string>();
        foreach (string name in new[] { "tAA", "tRCD", "tRP", "tRAS" })
        {
            var sensor = sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Timing &&
                sensor.Name.StartsWith(name + " (", StringComparison.Ordinal));
            if (sensor?.Value is float delay && float.IsFinite(delay) && delay > 0)
                timings.Add($"{name} {delay:0.###} нс");
        }

        return timings.Count > 0 ? string.Join(" · ", timings) : null;
    }

    private static bool IsSensorError(Exception ex) => IsSystemError(ex) || ex is ExternalException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        or TypeInitializationException or NotSupportedException;
}
