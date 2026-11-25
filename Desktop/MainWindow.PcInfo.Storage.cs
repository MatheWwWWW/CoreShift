using System.IO;
using System.Management;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    private sealed record StorageDisk(uint Number, string Name, ulong? Size, uint? Bus, bool IsBoot,
        string? Path, string? UniqueId, string? Serial);
    private sealed record PhysicalStorage(string Name, ulong? Size, uint? Media, uint? Bus,
        string? Path, string? UniqueId, string? Serial);
    private sealed record StoragePartition(uint Disk, uint Number, string? Letter, ulong? Size, string[] AccessPaths, string? Kind = null);
    private sealed record StorageVolume(string? Letter, string? Path, string? Name, string? FileSystem, ulong? Size, ulong? Free);

    private sealed record StorageReport(string Summary, string? WindowsVolume, IReadOnlyList<PcInfoRow> Rows);

    private static StorageReport ReadStorageInfo()
    {
        string? windowsVolume = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        var disks = ReadWmi("SELECT Number, FriendlyName, Size, BusType, IsBoot, UniqueId, SerialNumber FROM MSFT_Disk", item => new StorageDisk(
            AsUInt(item["Number"]) ?? uint.MaxValue, WmiText(item, "FriendlyName") ?? Unavailable, PositiveBytes(item["Size"]),
            AsUInt(item["BusType"]), item["IsBoot"] is true, ObjectPath(item), WmiText(item, "UniqueId"), WmiText(item, "SerialNumber")),
            StorageScope).Where(disk => disk.Number != uint.MaxValue).ToList();
        var partitions = ReadWmi("SELECT DiskNumber, PartitionNumber, DriveLetter, Size, AccessPaths, GptType, MbrType FROM MSFT_Partition",
            item => new StoragePartition(AsUInt(item["DiskNumber"]) ?? uint.MaxValue, AsUInt(item["PartitionNumber"]) ?? 0,
                ReadDriveLetter(item["DriveLetter"]), PositiveBytes(item["Size"]), item["AccessPaths"] as string[] ?? [],
                FormatPartitionKind(WmiText(item, "GptType"), AsUInt(item["MbrType"]))), StorageScope);

        var physical = ReadWmi("SELECT FriendlyName, Size, MediaType, BusType, UniqueId, SerialNumber FROM MSFT_PhysicalDisk",
            item => new PhysicalStorage(WmiText(item, "FriendlyName") ?? Unavailable, PositiveBytes(item["Size"]),
                AsUInt(item["MediaType"]), AsUInt(item["BusType"]), ObjectPath(item), WmiText(item, "UniqueId"), WmiText(item, "SerialNumber")),
            StorageScope);
        var volumes = ReadWmi("SELECT DriveLetter, Path, FileSystemLabel, FileSystem, Size, SizeRemaining FROM MSFT_Volume",
            item => new StorageVolume(ReadDriveLetter(item["DriveLetter"]), WmiText(item, "Path"), WmiText(item, "FileSystemLabel"),
                WmiText(item, "FileSystem"), PositiveBytes(item["Size"]), item["SizeRemaining"] as ulong?), StorageScope);

        if (disks.Count == 0)
        {
            disks = ReadWmi("SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive", item => new StorageDisk(
                AsUInt(item["Index"]) ?? uint.MaxValue, WmiText(item, "Model") ?? Unavailable, PositiveBytes(item["Size"]),
                null, false, null, null, WmiText(item, "SerialNumber"))).Where(disk => disk.Number != uint.MaxValue).ToList();
        }
        if (partitions.Count == 0 && disks.Count > 0)
            partitions = ReadLegacyPartitions();
        if (volumes.Count == 0)
        {
            volumes = ReadWmi("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 2 OR DriveType = 3",
                item => new StorageVolume(WmiText(item, "DeviceID"), null, WmiText(item, "VolumeName"), WmiText(item, "FileSystem"),
                    PositiveBytes(item["Size"]), item["FreeSpace"] as ulong?));
        }

        var rows = new List<PcInfoRow>();
        bool anyTelemetry = false;
        StorageDisk? windowsDisk = null;
        var partitionsByDisk = partitions.ToLookup(partition => partition.Disk);
        var windowsDisks = partitions.Where(partition => SameStorageId(partition.Letter, windowsVolume))
            .Select(partition => partition.Disk).ToHashSet();
        bool ContainsWindows(StorageDisk disk) => windowsDisks.Count > 0 ? windowsDisks.Contains(disk.Number) : disk.IsBoot;

        foreach (var disk in disks.OrderByDescending(ContainsWindows).ThenBy(disk => disk.Number))
        {
            var media = MatchPhysicalStorage(disk, disks, physical);
            bool system = ContainsWindows(disk);
            if (system)
                windowsDisk ??= disk;
            string? mediaType = media?.Media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
            if (mediaType is not null && disk.Name.EndsWith($" {mediaType}", StringComparison.OrdinalIgnoreCase))
                mediaType = null;
            uint? bus = disk.Bus is > 0 ? disk.Bus : media?.Bus;
            string? busLabel = FormatStorageBus(bus);
            if (busLabel is not null && disk.Name.Contains($" {busLabel} ", StringComparison.OrdinalIgnoreCase))
                busLabel = null;
            rows.Add(new($"Диск {disk.Number}", JoinDetails(disk.Name, mediaType, busLabel,
                disk.Size is { } size ? FormatBytes(size) : null,
                system ? windowsVolume is not null ? $"Windows ({windowsVolume})" : "Windows" : null)!, IsGroup: true));

            foreach (var partition in partitionsByDisk[disk.Number].OrderByDescending(partition => partition.Letter is not null)
                .ThenBy(partition => partition.Number))
            {
                var volume = volumes.FirstOrDefault(candidate => IsPartitionVolume(partition, candidate));
                string label = partition.Letter ?? volume?.Letter ?? partition.Kind ?? $"Раздел {partition.Number}";
                var capacity = volume?.Size ?? partition.Size;
                rows.Add(new(label, JoinDetails(volume?.Name, volume?.FileSystem,
                    capacity is { } total ? FormatBytes(total) : null,
                    volume?.Free is { } free ? $"Свободно {FormatBytes(free)}" : null) ?? Unavailable));
            }

            string? reliability = ReadStorageReliability(disk.Path, "MSFT_DiskToStorageReliabilityCounter")
                ?? ReadStorageReliability(media?.Path, "MSFT_PhysicalDiskToStorageReliabilityCounter");
            if (reliability is not null)
            {
                anyTelemetry = true;
                rows.Add(new($"Датчики диска {disk.Number}", reliability));
            }
        }

        foreach (var volume in volumes.Where(volume => volume.Letter is not null &&
            !partitions.Any(partition => IsPartitionVolume(partition, volume))))
        {
            rows.Add(new(volume.Letter!, JoinDetails(volume.Name, volume.FileSystem,
                volume.Size is { } total ? FormatBytes(total) : null,
                volume.Free is { } free ? $"Свободно {FormatBytes(free)}" : null) ?? Unavailable));
        }

        if (disks.Count > 0 && !anyTelemetry)
            rows.Add(new("Температура, износ, наработка", "Windows не вернула данные"));

        string? windowsDetail = windowsVolume is not null ? JoinDetails($"Windows на {windowsVolume}", windowsDisk?.Name) : null;
        return new StorageReport(disks.Count > 0 ? $"Дисков: {disks.Count}" : Unavailable, windowsDetail, AvailableRows(rows));
    }

    private static List<StoragePartition> ReadLegacyPartitions()
    {
        var raw = ReadWmi("SELECT DiskIndex, Index, Size FROM Win32_DiskPartition", item => (
            Disk: AsUInt(item["DiskIndex"]) ?? uint.MaxValue, Number: AsUInt(item["Index"]) ?? 0,
            Size: PositiveBytes(item["Size"]), Path: ObjectPath(item)));
        var partitions = new List<StoragePartition>();
        foreach (var partition in raw.Where(partition => partition.Disk != uint.MaxValue))
        {
            var letters = partition.Path is { } path
                ? ReadWmi($"ASSOCIATORS OF {{{path}}} WHERE AssocClass = Win32_LogicalDiskToPartition ResultClass = Win32_LogicalDisk",
                    item => WmiText(item, "DeviceID")).OfType<string>().ToArray() : [];
            if (letters.Length == 0)
                partitions.Add(new(partition.Disk, partition.Number + 1, null, partition.Size, []));
            else
                partitions.AddRange(letters.Select(letter => new StoragePartition(partition.Disk, partition.Number + 1, letter, partition.Size, [])));
        }

        return partitions;
    }

    private static PhysicalStorage? MatchPhysicalStorage(StorageDisk disk, List<StorageDisk> disks, List<PhysicalStorage> physical)
    {
        var byId = physical.Where(candidate => SameStorageId(disk.UniqueId, candidate.UniqueId)).ToArray();
        if (byId.Length == 1 && disks.Count(candidate => SameStorageId(disk.UniqueId, candidate.UniqueId)) == 1)
            return byId[0];

        var bySerial = physical.Where(candidate => SameStorageId(disk.Serial, candidate.Serial) &&
            disk.Size == candidate.Size).ToArray();
        if (bySerial.Length == 1 && disks.Count(candidate => candidate.Size == disk.Size &&
            SameStorageId(disk.Serial, candidate.Serial)) == 1)
            return bySerial[0];

        if (disk.Size is null || disk.Name == Unavailable || disks.Count(candidate =>
            candidate.Size == disk.Size && SameStorageId(candidate.Name, disk.Name)) != 1)
            return null;

        var byModel = physical.Where(candidate => candidate.Size == disk.Size && SameStorageId(candidate.Name, disk.Name)).ToArray();
        return byModel.Length == 1 ? byModel[0] : null;
    }

    private static bool SameStorageId(string? left, string? right) =>
        left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsPartitionVolume(StoragePartition partition, StorageVolume volume) =>
        SameStorageId(partition.Letter, volume.Letter) || (volume.Path is { } volumePath &&
            partition.AccessPaths.Any(path => SameStorageId(path.TrimEnd('\\'), volumePath.TrimEnd('\\'))));

    private static string? ReadStorageReliability(string? path, string association)
    {
        if (path is null)
            return null;

        return ReadWmi($"ASSOCIATORS OF {{{path}}} WHERE AssocClass = {association} ResultClass = MSFT_StorageReliabilityCounter",
            item => JoinDetails(
                AsUInt(item["Temperature"]) is > 0 and < 255 and var temperature ? $"{temperature} °C" : null,
                AsUInt(item["Wear"]) is <= 100 and var wear ? $"Износ {wear}%" : null,
                AsUInt(item["PowerOnHours"]) is < ushort.MaxValue and var hours ? $"Наработка {hours:N0} ч" : null),
            StorageScope).OfType<string>().FirstOrDefault();
    }

    private static string? ObjectPath(ManagementBaseObject item) => NonEmpty((item as ManagementObject)?.Path.RelativePath);

    private static ulong? PositiveBytes(object? value) => value is ulong bytes && bytes > 0 ? bytes : null;

    private static string? ReadDriveLetter(object? value)
    {
        char letter = value switch { char character => character, ushort number => (char)number, string { Length: > 0 } text => text[0], _ => '\0' };
        return char.IsAsciiLetter(letter) ? $"{char.ToUpperInvariant(letter)}:" : null;
    }

    private static string? FormatPartitionKind(string? gptType, uint? mbrType) => gptType?.Trim('{', '}').ToLowerInvariant() switch
    {
        "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "Системный раздел EFI",
        "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Резерв Microsoft (MSR)",
        "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Раздел восстановления",
        _ => mbrType is 5 or 15 ? "Расширенный раздел MBR" : null
    };

    private static string? FormatStorageBus(uint? bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD",
        13 => "MMC", 14 or 15 => "Виртуальный диск", 16 => "Storage Spaces", 17 => "NVMe", 19 => "UFS",
        _ => null
    };
}
