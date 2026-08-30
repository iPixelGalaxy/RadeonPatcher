using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace RadeonPatcher;

public sealed record RadeonIdPresetMutation(string SubKey, string Name, string Kind, string Value)
{
    public string Location => string.IsNullOrWhiteSpace(SubKey) ? Name : $"{SubKey}\\{Name}";
}

public sealed record RadeonIdPresetDefinition(
    string Id,
    string Name,
    string Category,
    IReadOnlyList<RadeonIdPresetMutation> Mutations)
{
    public string DisplayName => RadeonIdPresetCatalog.Humanize(Name);

    public string MutationSummary
    {
        get
        {
            var names = Mutations.Select(mutation => mutation.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var summary = string.Join(", ", names.Take(3));
            return names.Count > 3 ? $"{summary} (+{names.Count - 3})" : summary;
        }
    }
}

internal sealed record RadeonIdPresetDocument(
    string Source,
    int PresetCount,
    IReadOnlyList<RadeonIdPresetDefinition> Presets);

public static class RadeonIdPresetCatalog
{
    private static readonly Lazy<RadeonIdPresetDocument> Document = new(Load);

    public static string Source => Document.Value.Source;
    public static IReadOnlyList<RadeonIdPresetDefinition> All => Document.Value.Presets;

    public static IReadOnlyList<RadeonIdPresetDefinition> Resolve(IEnumerable<string>? ids)
    {
        var selected = new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        return All.Where(preset => selected.Contains(preset.Id)).ToList();
    }

    public static IReadOnlyList<string> FindConflicts(IEnumerable<string>? ids)
    {
        return Resolve(ids)
            .SelectMany(preset => preset.Mutations.Select(mutation => new { preset.DisplayName, Mutation = mutation }))
            .GroupBy(item => $"{item.Mutation.SubKey}\0{item.Mutation.Name}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(item => $"{item.Mutation.Kind}\0{item.Mutation.Value}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any())
            .Select(group =>
            {
                var mutation = group.First().Mutation;
                var presets = string.Join(", ", group.Select(item => item.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase));
                return $"{mutation.Location}: {presets}";
            })
            .ToList();
    }

    internal static string Humanize(string name)
    {
        var text = name.Replace('_', ' ');
        foreach (var token in new[]
        {
            "disabled", "enabled", "advanced", "extended", "regular", "minimal", "standard",
            "default", "force", "basic", "support", "compute", "graphics", "display", "driver",
            "shader", "cache", "queue", "buffer", "direct", "legacy", "performance", "enterprise",
            "gaming", "prosumer", "workload", "workgroup", "function", "mode", "off", "on"
        })
        {
            if (token is "off" or "on") continue;
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                token,
                " " + token + " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, "(off|on)$", " $1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        foreach (var acronym in new[] { "AMD", "ADL", "D3D", "DXGI", "DXPAL", "DXVA", "FBC", "FSAA", "GPU", "GFX", "HBCC", "HDCP", "HSA", "KMD", "MPO", "OGL", "PCI", "ReBAR", "RSR", "SRIOV", "SVM", "ULPS", "VSync" })
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                $@"\b{acronym}\b",
                acronym,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return text;
    }

    private static RadeonIdPresetDocument Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RadeonIdPresets.json")
            ?? throw new InvalidOperationException("Embedded Radeon-ID preset catalog was not found.");
        var document = JsonSerializer.Deserialize<RadeonIdPresetDocument>(stream)
            ?? throw new InvalidOperationException("Embedded Radeon-ID preset catalog is invalid.");
        if (document.Presets.Count != document.PresetCount)
        {
            throw new InvalidOperationException("Embedded Radeon-ID preset catalog is incomplete.");
        }
        if (document.Presets.Select(preset => preset.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Presets.Count ||
            document.Presets.Any(preset => string.IsNullOrWhiteSpace(preset.Id) || preset.Mutations.Count == 0) ||
            document.Presets.SelectMany(preset => preset.Mutations).Any(mutation =>
                string.IsNullOrWhiteSpace(mutation.Name) ||
                mutation.SubKey.StartsWith('\\') ||
                mutation.SubKey.Split('\\').Any(part => part is "." or "..") ||
                mutation.Kind is not ("String" or "ExpandString" or "Binary" or "DWord")))
        {
            throw new InvalidOperationException("Embedded Radeon-ID preset catalog contains unsafe or unsupported entries.");
        }
        return document;
    }
}
