namespace RadeonPatcher;

public enum RebarOverrideMode
{
    Default,
    Disabled,
    Enabled,
    ForcedLegacy
}

public enum ToggleOverrideMode
{
    Default,
    Disabled,
    Enabled
}

public enum HardwareFlipQueueMode
{
    Default,
    Disabled,
    Mode1,
    Mode2
}

public enum ShaderCacheOverrideMode
{
    Default,
    Disabled,
    Enabled
}

public enum WindowsDriverUpdateMode
{
    Default,
    Enabled,
    NeededOnly,
    PartiallyDisabled,
    FullyDisabled
}

public sealed record DriverPatchOptions(
    bool Enabled,
    bool DisableAnalytics,
    bool EnableFeatureOverrides,
    bool EnableLegacyRebarInfSupport,
    bool UseHybridDriver,
    string ModernDriverSupportUrl,
    RebarOverrideMode RebarMode,
    ToggleOverrideMode HardwareSchedulingMode,
    HardwareFlipQueueMode HardwareFlipQueueMode,
    ShaderCacheOverrideMode ShaderCacheMode,
    WindowsDriverUpdateMode WindowsDriverUpdateMode,
    IReadOnlyList<string> RadeonIdPresetIds)
{
    public const string DefaultModernDriverSupportUrl =
        "https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070-xt.html";

    public bool RequiresInfPatch => Enabled &&
        (DisableAnalytics || EnableFeatureOverrides || EnableLegacyRebarInfSupport || RadeonIdPresetIds.Count > 0);

    public bool HasRuntimeTweaks => Enabled &&
        (RebarMode != RebarOverrideMode.Default ||
         HardwareSchedulingMode != ToggleOverrideMode.Default ||
         HardwareFlipQueueMode != HardwareFlipQueueMode.Default ||
         ShaderCacheMode != ShaderCacheOverrideMode.Default ||
         WindowsDriverUpdateMode != WindowsDriverUpdateMode.Default ||
         RadeonIdPresetIds.Count > 0);
}
