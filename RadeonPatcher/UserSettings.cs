using System.IO;
using System.Text.Json;

namespace RadeonPatcher;

public sealed class UserSettings
{
    public bool InstallGpuDriver { get; set; } = true;
    public bool InstallAdrenalin { get; set; } = true;
    public bool InstallAudioDriver { get; set; } = true;
    public bool AutoClearDownloadedCache { get; set; } = true;
    public string? CustomSupportUrl { get; set; }
    public string? SelectedDriverVersion { get; set; }
    public string? LastInstalledDisplayPackageVersion { get; set; }
    public string? LastInstalledAudioDriverVersion { get; set; }
    public DateTimeOffset? LastInstalledDisplayPackageAt { get; set; }
    public DateTimeOffset? LastInstalledAudioDriverAt { get; set; }
    public string? LastApplicationPath { get; set; }
    public int UpdateCheckFrequencyMinutes { get; set; } = 24 * 60;
    public string? IgnoredAppUpdateVersion { get; set; }
}

internal static class UserSettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonPatcher");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings()
                : new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
    }
}

public sealed record DriverInstallReceipt(
    string GpuInstanceId,
    string OriginalInf,
    string PackageVersion,
    DateTimeOffset InstalledAt);

internal static class DriverInstallReceiptStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonPatcher");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "driver-receipts.json");

    public static IReadOnlyList<DriverInstallReceipt> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<DriverInstallReceipt>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(DriverInstallReceipt receipt)
    {
        var receipts = Load()
            .Where(x => !string.Equals(x.GpuInstanceId, receipt.GpuInstanceId, StringComparison.OrdinalIgnoreCase))
            .Append(receipt)
            .ToList();
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(receipts));
    }
}

public sealed record DriverPackageMap(string OriginalInf, string PackageVersion, DateTimeOffset RecordedAt);

internal static class DriverPackageMapStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonPatcher");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "driver-package-map.json");

    public static IReadOnlyList<DriverPackageMap> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<DriverPackageMap>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(DriverPackageMap map)
    {
        var maps = Load()
            .Where(x => !string.Equals(x.OriginalInf, map.OriginalInf, StringComparison.OrdinalIgnoreCase))
            .Append(map)
            .ToList();
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(maps));
    }
}
