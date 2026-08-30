using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace RadeonPatcher;

public partial class AdvancedPatchesWindow : Window
{
    private readonly List<RadeonIdPresetRow> _presetRows;
    private readonly ICollectionView _presetView;

    public bool ApplyRuntimeNow { get; private set; }
    public DriverPatchOptions Options { get; private set; }

    public AdvancedPatchesWindow(DriverPatchOptions options)
    {
        InitializeComponent();
        Options = options;
        EnablePatchesCheck.IsChecked = options.Enabled;
        DisableAnalyticsCheck.IsChecked = options.DisableAnalytics;
        FeatureOverridesCheck.IsChecked = options.EnableFeatureOverrides;
        LegacyRebarInfCheck.IsChecked = options.EnableLegacyRebarInfSupport;
        HybridDriverCheck.IsChecked = options.UseHybridDriver;
        ModernSupportUrlBox.Text = string.IsNullOrWhiteSpace(options.ModernDriverSupportUrl)
            ? DriverPatchOptions.DefaultModernDriverSupportUrl
            : options.ModernDriverSupportUrl;
        RebarCombo.SelectedIndex = (int)options.RebarMode;
        HardwareSchedulingCombo.SelectedIndex = (int)options.HardwareSchedulingMode;
        HardwareFlipCombo.SelectedIndex = (int)options.HardwareFlipQueueMode;
        ShaderCacheCombo.SelectedIndex = (int)options.ShaderCacheMode;
        WindowsDriverUpdatesCombo.SelectedIndex = (int)options.WindowsDriverUpdateMode;
        var selectedPresets = new HashSet<string>(options.RadeonIdPresetIds, StringComparer.OrdinalIgnoreCase);
        _presetRows = RadeonIdPresetCatalog.All
            .Select(preset => new RadeonIdPresetRow(preset, selectedPresets.Contains(preset.Id)))
            .ToList();
        _presetView = CollectionViewSource.GetDefaultView(_presetRows);
        _presetView.Filter = FilterPreset;
        RadeonIdPresetList.ItemsSource = _presetView;
        UpdatePresetCounts();
        UpdateEnabledState();
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
    }

    private void EnablePatchesCheck_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void HybridDriverCheck_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void PresetSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _presetView?.Refresh();
        UpdatePresetCounts();
    }

    private void PresetCheckBox_Click(object sender, RoutedEventArgs e) => UpdatePresetCounts();

    private void ClearPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _presetRows) row.IsSelected = false;
        UpdatePresetCounts();
    }

    private bool FilterPreset(object item)
    {
        if (item is not RadeonIdPresetRow row) return false;
        var search = PresetSearchBox?.Text.Trim();
        return string.IsNullOrWhiteSpace(search) ||
               row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePresetCounts()
    {
        if (PresetResultCountText is null || PresetSelectedCountText is null || _presetRows is null) return;
        var visible = _presetView?.Cast<object>().Count() ?? _presetRows.Count;
        PresetResultCountText.Text = $"{visible} of {_presetRows.Count} presets shown";
        PresetSelectedCountText.Text = $"{_presetRows.Count(row => row.IsSelected)} selected";
    }

    private void UpdateEnabledState()
    {
        if (OptionsPanel is null) return;
        OptionsPanel.IsEnabled = EnablePatchesCheck.IsChecked == true;
        HybridSourcePanel.IsEnabled = HybridDriverCheck.IsChecked == true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

    private void ApplyNowButton_Click(object sender, RoutedEventArgs e) => CloseWithResult(true);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseWithResult(bool applyRuntimeNow)
    {
        var modernUrl = ModernSupportUrlBox.Text.Trim();
        if (EnablePatchesCheck.IsChecked == true && HybridDriverCheck.IsChecked == true &&
            (!Uri.TryCreate(modernUrl, UriKind.Absolute, out var modernUri) || modernUri.Scheme != Uri.UriSchemeHttps))
        {
            AppDialog.ShowError(this, "Invalid modern driver source", "Enter an absolute HTTPS AMD support URL.");
            return;
        }

        var presetIds = _presetRows.Where(row => row.IsSelected).Select(row => row.Id).ToList();
        var conflicts = RadeonIdPresetCatalog.FindConflicts(presetIds);
        if (EnablePatchesCheck.IsChecked == true && conflicts.Count > 0)
        {
            var details = string.Join(Environment.NewLine, conflicts.Take(8));
            if (conflicts.Count > 8) details += $"{Environment.NewLine}...and {conflicts.Count - 8} more.";
            AppDialog.ShowError(this, "Conflicting Radeon-ID presets",
                "Choose only one mode for each registry value:" + Environment.NewLine + Environment.NewLine + details);
            return;
        }

        Options = new DriverPatchOptions(
            EnablePatchesCheck.IsChecked == true,
            DisableAnalyticsCheck.IsChecked == true,
            FeatureOverridesCheck.IsChecked == true,
            LegacyRebarInfCheck.IsChecked == true,
            HybridDriverCheck.IsChecked == true,
            modernUrl,
            (RebarOverrideMode)Math.Max(0, RebarCombo.SelectedIndex),
            (ToggleOverrideMode)Math.Max(0, HardwareSchedulingCombo.SelectedIndex),
            (HardwareFlipQueueMode)Math.Max(0, HardwareFlipCombo.SelectedIndex),
            (ShaderCacheOverrideMode)Math.Max(0, ShaderCacheCombo.SelectedIndex),
            (WindowsDriverUpdateMode)Math.Max(0, WindowsDriverUpdatesCombo.SelectedIndex),
            presetIds);
        ApplyRuntimeNow = applyRuntimeNow;
        DialogResult = true;
        Close();
    }
}

internal sealed class RadeonIdPresetRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public RadeonIdPresetRow(RadeonIdPresetDefinition preset, bool isSelected)
    {
        Preset = preset;
        _isSelected = isSelected;
    }

    public RadeonIdPresetDefinition Preset { get; }
    public string Id => Preset.Id;
    public string DisplayName => Preset.DisplayName;
    public string Details => $"{Preset.Category} · {Preset.MutationSummary}";
    public string SearchText => $"{Preset.Id} {DisplayName} {Details}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
