using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string Unavailable = "Недоступно";

    private sealed record PcInfoRow(string Label, string Value, bool IsGroup = false);
    private sealed record PcInfoSection(string Title, IReadOnlyList<PcInfoRow> Rows)
    {
        public bool Expanded { get; set; }
    }
    private sealed record PcInfoCard(string Label, string Value, string? Detail);
    private sealed record PcInfoReport(PcInfoCard[] Summary, PcInfoSection[] Sections)
    {
        public string ToClipboardText()
        {
            var report = new StringBuilder("CoreShift · Конфигурация компьютера");
            foreach (var section in Sections)
            {
                report.AppendLine().AppendLine().Append(section.Title);
                foreach (var row in section.Rows)
                {
                    report.AppendLine().Append(row.Label).Append(row.Label.EndsWith(':') ? " " : ": ");
                    report.Append(row.Value.Replace(Environment.NewLine, Environment.NewLine + "  ", StringComparison.Ordinal));
                }
            }

            return report.ToString();
        }
    }

    private sealed record ProcessorInfo(string Name, uint? Cores, uint? Threads, uint? FirmwareMHz, uint? L2KB, uint? L3KB,
        string? Socket, double? FirmwareVoltage)
    {
        public string? Topology => JoinDetails(Cores is { } cores ? $"Ядра: {cores}" : null,
            Threads is { } threads ? $"Потоки: {threads}" : null);
    }

    private sealed record MemoryModule(string Slot, string? Model, string? Type, ulong? Capacity,
        uint? ConfiguredSpeed, uint? RatedSpeed, uint? ConfiguredMillivolts, uint Ranks)
    {
        public string Description => JoinDetails(Type, Capacity is { } bytes ? FormatBytes(bytes) : null,
            ConfiguredSpeed is { } configured ? $"{configured:N0} МТ/с (настроено)" : null,
            RatedSpeed is { } rated ? $"{rated:N0} МТ/с (по SMBIOS)" : null,
            Ranks > 0 ? $"{Ranks}R" : null, Model)
            ?? Unavailable;
    }

    private PcInfoReport? pcInfoReport;

    private async Task LoadPcInfoAsync()
    {
        if (loadingPcInfo || closing)
            return;

        loadingPcInfo = true;
        PcInfoRefreshButton.IsEnabled = false;
        PcInfoCopyButton.IsEnabled = false;
        PcInfoStatus.Visibility = Visibility.Visible;
        SetStatus(PcInfoStatus, "Получаем сведения о компьютере…");
        try
        {
            var report = await RunInBackground(ReadPcInfo);
            if (closing)
                return;

            if (pcInfoReport is { } previous)
            {
                var expanded = previous.Sections.ToDictionary(section => section.Title, section => section.Expanded);
                foreach (var section in report.Sections)
                {
                    if (expanded.TryGetValue(section.Title, out bool wasExpanded))
                        section.Expanded = wasExpanded;
                }
            }

            pcInfoReport = report;
            PcInfoSummary.ItemsSource = report.Summary;
            PcInfoItems.ItemsSource = report.Sections;
            PcInfoStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
            if (!closing)
                SetStatus(PcInfoStatus, "Не удалось обновить сведения о компьютере.", error: true);
        }
        finally
        {
            loadingPcInfo = false;
            if (!closing)
            {
                PcInfoRefreshButton.IsEnabled = true;
                PcInfoCopyButton.IsEnabled = pcInfoReport is not null;
            }
        }
    }

    private async void RefreshPcInfoClick(object sender, RoutedEventArgs e) => await LoadPcInfoAsync();

    private void CopyPcInfoClick(object sender, RoutedEventArgs e)
    {
        if (pcInfoReport is not { } report || loadingPcInfo)
            return;

        PcInfoStatus.Visibility = Visibility.Visible;
        try
        {
            Clipboard.SetText(report.ToClipboardText());
            SetStatus(PcInfoStatus, "Отчёт скопирован.");
        }
        catch (ExternalException ex)
        {
            Debug.WriteLine(ex);
            SetStatus(PcInfoStatus, "Буфер обмена занят. Попробуйте скопировать отчёт ещё раз.", error: true);
        }
    }

    private static PcInfoReport ReadPcInfo()
    {
        string windows = ReadWmiValues("SELECT Caption FROM Win32_OperatingSystem", item => WmiText(item, "Caption"))
            ?? "Windows";
        string? board = ReadWmiValues("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", item =>
            JoinDetails(WmiText(item, "Manufacturer"), WmiText(item, "Product"),
                WmiText(item, "Version") is { } version and not "x.x" ? $"рев. {version}" : null));
        var bios = ReadWmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", item => (
            Version: JoinDetails(WmiText(item, "Manufacturer"), WmiText(item, "SMBIOSBIOSVersion")),
            Date: FormatFirmwareDate(WmiText(item, "ReleaseDate")))).FirstOrDefault();
        var firmware = ReadFirmwareInfo();
        var processors = ReadWmi("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, " +
            "SocketDesignation, CurrentVoltage FROM Win32_Processor", item => new ProcessorInfo(
                WmiText(item, "Name") ?? Unavailable, PositiveUInt(item["NumberOfCores"]), PositiveUInt(item["NumberOfLogicalProcessors"]),
                PositiveUInt(item["MaxClockSpeed"]), PositiveUInt(item["L2CacheSize"]), PositiveUInt(item["L3CacheSize"]),
                WmiText(item, "SocketDesignation"), ReadFirmwareCpuVoltage(item["CurrentVoltage"])));
        var graphics = ReadGraphicsInfo();
        var storage = ReadStorageInfo();

        var modules = ReadWmi(
            "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, SMBIOSMemoryType, Capacity, ConfiguredClockSpeed, Speed, " +
            "ConfiguredVoltage, Attributes FROM Win32_PhysicalMemory", ReadMemoryModule);
        ulong? installed = modules.Count > 0 && modules.All(module => module.Capacity.HasValue)
            ? modules.Aggregate(0UL, (total, module) => total + module.Capacity.GetValueOrDefault()) : null;
        int[] arrays = ReadWmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray WHERE Use = 3",
            item => (int)(AsUInt(item["MemoryDevices"]) ?? 0)).ToArray();
        int slots = arrays.Length > 0 && arrays.All(count => count > 0) ? arrays.Sum() : 0;
        string? slotUse = modules.Count > 0 && slots >= modules.Count ? $"{modules.Count} из {slots}" : null;
        ulong? usable = null;
        try
        {
            usable = MemoryAndPagefileManager.ReadMemorySnapshot().Physical.TotalBytes;
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
        }

        var memoryRows = new List<PcInfoRow>
        {
            new("Установлено", installed.HasValue ? FormatBytes(installed) : Unavailable),
            new("Объём для Windows", usable.HasValue ? FormatBytes(usable) : Unavailable),
            new("Установлено модулей", modules.Count > 0 ? modules.Count.ToString(CultureInfo.CurrentCulture) : Unavailable),
            new("Слоты по SMBIOS", slotUse ?? Unavailable)
        };
        memoryRows.AddRange(modules.Select(module => new PcInfoRow(module.Slot, module.Description)));

        var timings = new List<PcInfoRow>();
        var voltages = new List<PcInfoRow>();
        var temperatures = new List<PcInfoRow>();
        ReadHardwareSensors(timings, voltages, temperatures);
        memoryRows.Add(new("Тайминги SPD", timings.Count > 0
            ? string.Join(Environment.NewLine, timings.Select(row => $"{row.Label} · {row.Value}"))
            : "Недоступны для текущей конфигурации"));

        if (voltages.Count == 0)
            voltages.Add(new("Датчики напряжения", Unavailable));
        var cpuVoltages = processors.Where(processor => processor.FirmwareVoltage.HasValue)
            .Select(processor => JoinDetails(processor.Name, $"{processor.FirmwareVoltage:0.0} В")).ToArray();
        if (cpuVoltages.Length > 0)
            voltages.Add(new("ЦП по данным SMBIOS", string.Join(Environment.NewLine, cpuVoltages)));
        string[] configuredVoltages = modules.Where(module => module.ConfiguredMillivolts.HasValue)
            .Select(module => $"{module.Slot} · {module.ConfiguredMillivolts.GetValueOrDefault() / 1000d:0.###} В").ToArray();
        voltages.Add(new("ОЗУ по настройкам BIOS", configuredVoltages.Length > 0
            ? string.Join(Environment.NewLine, configuredVoltages)
            : Unavailable));

        var cpuRows = new List<PcInfoRow>();
        uint? baseFrequency = processors.Count == 1 ? ReadCpuBaseFrequency() : null;
        for (int index = 0; index < processors.Count; index++)
        {
            var processor = processors[index];
            var model = SplitProcessorFrequency(processor.Name);
            uint? frequency = baseFrequency ?? model.MHz;
            cpuRows.Add(new(processors.Count > 1 ? $"Процессор {index + 1}" : "Модель", processor.Name));
            cpuRows.Add(new("Ядра и потоки", processor.Topology ?? Unavailable));
            if (frequency is { } mhz)
                cpuRows.Add(new(baseFrequency.HasValue ? "Базовая частота (CPUID)" : "Частота в названии модели", FormatFrequency(mhz)!));
            if (frequency is null || processor.FirmwareMHz is { } firmwareMHz && firmwareMHz != frequency)
                cpuRows.Add(new("Частота по SMBIOS", FormatFrequency(processor.FirmwareMHz) ?? Unavailable));
            cpuRows.Add(new("Кэш L2 / L3", JoinDetails(processor.L2KB is { } l2 ? $"L2 · {FormatBytes((ulong)l2 * 1024)}" : null,
                processor.L3KB is { } l3 ? $"L3 · {FormatBytes((ulong)l3 * 1024)}" : null) ?? Unavailable));
            cpuRows.Add(new("Сокет", processor.Socket ?? Unavailable));
        }

        var gpuRows = new List<PcInfoRow>();
        for (int index = 0; index < graphics.Count; index++)
        {
            var adapter = graphics[index];
            gpuRows.Add(new(graphics.Count > 1 ? $"Видеоадаптер {index + 1}" : "Модель", adapter.Name));
            gpuRows.Add(new("Выделенная видеопамять", adapter.VideoMemory is { } videoBytes ? FormatBytes(videoBytes) : Unavailable));
            gpuRows.Add(new("Версия драйвера", adapter.Driver ?? Unavailable));
        }

        var processorSummary = processors.FirstOrDefault();
        var processorModel = SplitProcessorFrequency(processorSummary?.Name);
        var graphicsSummary = graphics.FirstOrDefault();
        string? memoryType = string.Join(" / ", modules.Select(module => module.Type).OfType<string>().Distinct());
        string? memorySpeed = string.Join(" / ", modules.Select(module => module.ConfiguredSpeed).OfType<uint>()
            .Distinct().OrderBy(speed => speed).Select(speed => $"{speed} МТ/с"));
        ulong? totalMemory = installed ?? usable;
        string memoryValue = totalMemory is { } totalBytes
            ? JoinDetails(FormatBytes(totalBytes), NonEmpty(memoryType))! : Unavailable;

        PcInfoCard[] summary =
        [
            new("Процессор", CountedName(ShortProcessorName(processorModel.Name), processors.Count),
                JoinDetails(processorSummary?.Topology, FormatFrequency(baseFrequency ?? processorModel.MHz ?? processorSummary?.FirmwareMHz))),
            new("Видеокарта", CountedName(graphicsSummary?.Name, graphics.Count),
                graphicsSummary?.VideoMemory is { } vram ? $"{FormatBytes(vram)} видеопамяти" : null),
            new("Оперативная память", memoryValue,
                JoinDetails(NonEmpty(memorySpeed), slotUse is not null ? $"Слоты: {slotUse}" : null)),
            new("Накопители", storage.Summary, storage.WindowsVolume)
        ];

        PcInfoSection[] sections =
        [
            new("Накопители", storage.Rows) { Expanded = true },
            new("Процессоры", AvailableRows(cpuRows)),
            new("Видеоадаптеры", AvailableRows(gpuRows)),
            new("Оперативная память", memoryRows),
            new("Мониторы", AvailableRows(ReadMonitorInfo())),
            new("Система",
            [
                new("Имя компьютера", Environment.MachineName),
                new("Операционная система", $"{windows} · сборка {Environment.OSVersion.Version.Build} · {RuntimeInformation.OSArchitecture}"),
                new("Материнская плата", board ?? Unavailable),
                new("Версия прошивки", bios.Version ?? Unavailable),
                new("Дата прошивки", bios.Date ?? Unavailable),
                new("Режим загрузки", firmware.Mode ?? Unavailable),
                new("Secure Boot", firmware.SecureBoot ?? Unavailable)
            ]),
            new("Температуры датчиков", AvailableRows(temperatures)),
            new("Напряжения", voltages)
        ];
        return new PcInfoReport(summary, sections);
    }

    private static IReadOnlyList<PcInfoRow> AvailableRows(List<PcInfoRow> rows) =>
        rows.Count > 0 ? rows : [new("Сведения", Unavailable)];

    private static string CountedName(string? first, int count) => count > 1 ? $"{first} и ещё {count - 1}" : first ?? Unavailable;

    private static string? ShortProcessorName(string? name)
    {
        if (name is null)
            return null;

        string shortName = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return shortName.EndsWith(" CPU", StringComparison.OrdinalIgnoreCase) ? shortName[..^4] : shortName;
    }

    private static string? FormatFrequency(uint? mhz) => mhz is { } value ? $"{value / 1000d:0.##} ГГц" : null;

    private static (string? Name, uint? MHz) SplitProcessorFrequency(string? name)
    {
        if (name is null)
            return (null, null);

        int separator = name.LastIndexOf('@');
        if (separator <= 0)
            return (name, null);

        var suffix = name.AsSpan(separator + 1).Trim();
        int scale = suffix.EndsWith("GHz", StringComparison.OrdinalIgnoreCase) ? 1000 :
            suffix.EndsWith("MHz", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (scale == 0 || !double.TryParse(suffix[..^3].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
            return (name, null);

        double mhz = Math.Round(value * scale);
        return mhz is >= 1 and <= uint.MaxValue ? (name[..separator].TrimEnd(), (uint)mhz) : (name, null);
    }

    private static uint? ReadCpuBaseFrequency()
    {
        if (!X86Base.IsSupported || X86Base.CpuId(0, 0).Eax < 0x16)
            return null;

        uint mhz = (uint)(X86Base.CpuId(0x16, 0).Eax & 0xFFFF);
        return mhz > 0 ? mhz : null;
    }

    private static string? FormatFirmwareDate(string? value) =>
        value is { Length: >= 8 } && DateOnly.TryParseExact(value.AsSpan(0, 8), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("dd.MM.yyyy") : null;

    private static (string? Mode, string? SecureBoot) ReadFirmwareInfo()
    {
        if (!GetFirmwareType(out uint type))
            return (null, null);
        if (type == 1)
            return ("BIOS (Legacy)", "Доступно в режиме UEFI");
        if (type != 2)
            return (null, null);

        string? secureBoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            secureBoot = key?.GetValue("UEFISecureBootEnabled") switch { 1 => "Включена", 0 => "Выключена", _ => null };
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            Debug.WriteLine(ex);
        }

        return ("UEFI", secureBoot);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

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

    private static uint? PositiveUInt(object? value) => AsUInt(value) is > 0 and var number ? number : null;

    private static uint? AsUInt(object? value) => value switch { byte number => number, ushort number => number, uint number => number, _ => null };

    private static string? WmiText(ManagementBaseObject item, string property) =>
        (item[property] as string)?.Trim() is { Length: > 0 } value ? value : null;

    private static string? JoinDetails(params string?[] details) =>
        string.Join(" · ", details.OfType<string>()) is { Length: > 0 } value ? value : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ReadWmiValues(string query, Func<ManagementBaseObject, string?> format)
    {
        var values = ReadWmi(query, format).OfType<string>().ToArray();
        return values.Length > 0 ? string.Join(Environment.NewLine, values) : null;
    }

    private static List<T> ReadWmi<T>(string query, Func<ManagementBaseObject, T> read, string scope = @"root\cimv2")
    {
        var values = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
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

    private static void ReadHardwareSensors(List<PcInfoRow> timings, List<PcInfoRow> voltages, List<PcInfoRow> temperatures)
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
                ReadHardwareSensors(hardware, hardware.Name, hasPawnIo, timings, voltages, temperatures);
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
        List<PcInfoRow> timings, List<PcInfoRow> voltages, List<PcInfoRow> temperatures)
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
                    voltages.Add(new($"{componentName} (датчик)", string.Join(Environment.NewLine, voltageValues)));
            }

            if (hardware.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                var readings = hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature &&
                    sensor.Value is float degrees && float.IsFinite(degrees) && degrees != 0)
                    .Select(sensor => $"{sensor.Name} · {sensor.Value:0.#} °C").ToArray();
                if (readings.Length > 0)
                    temperatures.Add(new(componentName, string.Join(Environment.NewLine, readings)));
            }

            if (hardware.HardwareType == HardwareType.Memory && FormatSpdTimings(hardware.Sensors) is { } spd)
                timings.Add(new(hardware.Name, spd));
        }
        catch (Exception ex) when (IsSensorError(ex))
        {
            Debug.WriteLine(ex);
        }

        foreach (var child in hardware.SubHardware)
            ReadHardwareSensors(child, componentName, hasPawnIo, timings, voltages, temperatures);
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
