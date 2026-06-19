using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RadeonPatcher;

public static partial class GpuModelDatabase
{
    private static readonly Lazy<DatabaseIndexes> Indexes = new(LoadIndexes);

    public static string ResolveName(string? instanceId, string? reportedName)
    {
        var mappedName = Resolve(instanceId);
        if (!string.IsNullOrWhiteSpace(mappedName))
        {
            return mappedName;
        }

        return IsGenericName(reportedName) ? "AMD display adapter" : reportedName!.Trim();
    }

    public static string? Resolve(string? instanceId)
    {
        var match = HardwareIdRegex().Match(instanceId ?? "");
        if (!match.Success)
        {
            return null;
        }

        var hardwareId = match.Value.ToUpperInvariant();
        var indexes = Indexes.Value;
        if (indexes.Exact.TryGetValue(hardwareId, out var exact))
        {
            return exact;
        }

        var withoutRevision = RevisionRegex().Replace(hardwareId, "");
        if (indexes.UniqueWithoutRevision.TryGetValue(withoutRevision, out var subsystemMatch))
        {
            return subsystemMatch;
        }

        var deviceMatch = DeviceIdRegex().Match(hardwareId);
        return deviceMatch.Success && indexes.UniqueByDeviceId.TryGetValue(deviceMatch.Groups[1].Value, out var deviceName)
            ? deviceName
            : null;
    }

    private static DatabaseIndexes LoadIndexes()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GpuModels.json")
            ?? throw new InvalidOperationException("The bundled AMD GPU model database is missing.");
        var database = JsonSerializer.Deserialize<GpuDatabase>(stream)
            ?? throw new InvalidOperationException("The bundled AMD GPU model database is invalid.");
        if (database.SchemaVersion != 1 || database.Devices.Count == 0)
        {
            throw new InvalidOperationException("The bundled AMD GPU model database has an unsupported schema.");
        }

        var exact = new Dictionary<string, string>(database.Devices, StringComparer.OrdinalIgnoreCase);
        var withoutRevision = BuildUniqueIndex(exact, pair => RevisionRegex().Replace(pair.Key, ""));
        var byDeviceId = BuildUniqueIndex(
            exact.Where(pair => DeviceIdRegex().IsMatch(pair.Key)),
            pair => DeviceIdRegex().Match(pair.Key).Groups[1].Value);
        return new DatabaseIndexes(exact, withoutRevision, byDeviceId);
    }

    private static Dictionary<string, string> BuildUniqueIndex(
        IEnumerable<KeyValuePair<string, string>> mappings,
        Func<KeyValuePair<string, string>, string> keySelector)
    {
        return mappings
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Names = group.Select(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .Where(group => group.Names.Length == 1)
            .ToDictionary(group => group.Key, group => group.Names[0], StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGenericName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Video Controller (VGA Compatible)", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Video Controller", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Display Controller", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"PCI\\VEN_1002&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2})?", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareIdRegex();

    [GeneratedRegex(@"&REV_[0-9A-F]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionRegex();

    [GeneratedRegex(@"&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdRegex();

    private sealed record DatabaseIndexes(
        Dictionary<string, string> Exact,
        Dictionary<string, string> UniqueWithoutRevision,
        Dictionary<string, string> UniqueByDeviceId);

    private sealed record GpuDatabase(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("devices")] Dictionary<string, string> Devices);
}
