using System.Windows;

namespace RadeonPatcher;

public partial class AdvancedPatchesWindow : Window
{
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
        UpdateEnabledState();
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
    }

    private void EnablePatchesCheck_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void HybridDriverCheck_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

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
            (ShaderCacheOverrideMode)Math.Max(0, ShaderCacheCombo.SelectedIndex));
        ApplyRuntimeNow = applyRuntimeNow;
        DialogResult = true;
        Close();
    }
}
