using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RadeonPatcher;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private static readonly Version LatestBundledAudioVersion = new(10, 0, 1, 42);
    private static readonly TimeSpan ForcedVersionDuration = TimeSpan.FromMinutes(2);
    private readonly DriverWorkflow _workflow = new();
    private readonly UserSettings _settings;
    private HardwareInfo? _hardware;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private bool _applyingSettings;
    private bool _updatingOptions;
    private bool _hasDownloadCache;
    private bool _canUninstallGpuDriver;
    private bool _canUninstallAudioDriver;
    private bool _canUninstallAdrenalin;
    private int _busyOperationCount;
    private readonly string _appVersionTag;

    public MainWindow()
    {
        InitializeComponent();
        var version = AppUpdateService.CurrentVersion;
        _appVersionTag = $"v{version.Major}.{version.Minor}.{version.Build}";
        AppVersionText.Text = _appVersionTag;
        _settings = UserSettingsStore.Load();
        _settings.LastApplicationPath = Environment.ProcessPath;
        UserSettingsStore.Save(_settings);
        ApplySavedSettings();
        ApplyTheme();
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        DriverCombo.SelectionChanged += (_, _) => UpdateSelectedDriverText();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private void AppVersionText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Process.Start(new ProcessStartInfo($"https://github.com/iPixelGalaxy/RadeonPatcher/releases/tag/{_appVersionTag}")
        {
            UseShellExecute = true
        });
    }

    private async Task RefreshAsync(bool refreshDrivers = false)
    {
        await Busy(async () =>
        {
            Log("Refreshing detected hardware and extracting embedded tools.");
            var hardwareTask = _workflow.GetHardwareInfoAsync();
            var payloadTask = _workflow.EnsurePayloadsAsync();
            await Task.WhenAll(hardwareTask, payloadTask);
            _hardware = await hardwareTask;
            if (_hardware.IsUpdateCheckServiceInstalled)
            {
                await _workflow.EnsureUpdateCheckServiceCurrentAsync(GetSavedUpdateCheckFrequency(), Log);
            }
            _hasDownloadCache = _workflow.HasDownloadCache();
            var displayForced = IsForcedVersionCurrent(_settings.LastInstalledDisplayPackageAt);
            var cpuGraphicsForced = IsForcedVersionCurrent(_settings.LastInstalledCpuGraphicsPackageAt);
            var audioForced = IsForcedVersionCurrent(_settings.LastInstalledAudioDriverAt);
            var gpuTargetIsCpuGraphics = string.Equals(_hardware.CpuGraphicsAdapter?.InstanceId, _hardware.GpuInstanceId, StringComparison.OrdinalIgnoreCase);
            _canUninstallGpuDriver = (gpuTargetIsCpuGraphics ? cpuGraphicsForced : displayForced)
                ? !string.IsNullOrWhiteSpace(gpuTargetIsCpuGraphics ? _settings.LastInstalledCpuGraphicsPackageVersion : _settings.LastInstalledDisplayPackageVersion)
                : !string.IsNullOrWhiteSpace(_hardware.DisplayDriverOriginalInf);
            _canUninstallAudioDriver = audioForced
                ? !string.IsNullOrWhiteSpace(_settings.LastInstalledAudioDriverVersion)
                : _hardware.AudioDriverVersion is not null;
            _canUninstallAdrenalin = _hardware.IsAdrenalinInstalled;

            var primaryDisplay = _hardware.PrimaryDisplayAdapter;
            GpuText.Text = FormatGpuList(_hardware);
            DisplayDriverText.Text = primaryDisplay is not null &&
                                     !string.Equals(primaryDisplay.InstanceId, _hardware.GpuInstanceId, StringComparison.OrdinalIgnoreCase)
                ? primaryDisplay.UsesBasicDisplayDriver
                    ? "Primary Display Driver: Microsoft Basic Display Adapter"
                    : $"Primary Display Driver: {primaryDisplay.DriverProviderName ?? "Unknown"} {primaryDisplay.DriverVersion ?? "driver unknown"}"
                : displayForced
                ? $"Installed Video Driver: {_settings.LastInstalledDisplayPackageVersion ?? "None"}"
                : _hardware.DisplayDriverPackageVersion is null
                ? _hardware.DisplayDriverVersion is null
                    ? "Installed Video Driver: None"
                    : "Installed Video Driver: Unknown"
                : $"Installed Video Driver: {_hardware.DisplayDriverPackageVersion}";
            OsText.Text = $"OS: {_hardware.OsName} ({_hardware.OsVersion})";
            AudioText.Text = audioForced
                ? $"Installed AMD HD Audio Driver: {_settings.LastInstalledAudioDriverVersion ?? "None"}"
                : _hardware.AudioDriverVersion is null
                ? "Installed AMD HD Audio Driver: None"
                : $"Installed AMD HD Audio Driver: {_hardware.AudioDriverVersion}";
            var cpuName = FormatCpuDisplayName(_hardware.CpuName);
            CpuGraphicsText.Text = _hardware.CpuGraphicsAdapter is { } cpuGraphics
                ? cpuGraphics.UsesBasicDisplayDriver
                    ? $"CPU Graphics: {cpuName}. Status: Enabled, using Microsoft Basic Display Adapter."
                    : $"CPU Graphics: {cpuName}. Status: Enabled ({(cpuGraphicsForced ? _settings.LastInstalledCpuGraphicsPackageVersion : cpuGraphics.PackageVersion) ?? cpuGraphics.DriverVersion ?? "AMD driver not installed"})."
                : !string.IsNullOrWhiteSpace(_hardware.CpuSupportUrl)
                    ? $"CPU Graphics: {cpuName}. Status: Disabled in firmware."
                    : "";

            UpdateInstallTargetControls();

            var savedCustomUrl = _settings.CustomSupportUrl;
            var supportUrl = string.IsNullOrWhiteSpace(savedCustomUrl)
                ? _workflow.ResolveSupportUrl(_hardware) ?? ""
                : savedCustomUrl;
            SupportUrlBox.Text = supportUrl;
            var hasCpuGraphics = _hardware.CpuGraphicsAdapter is not null;
            var cpuGraphicsNeedsSeparatePackage = hasCpuGraphics &&
                !string.Equals(_hardware.CpuGraphicsAdapter!.InstanceId, _hardware.GpuInstanceId, StringComparison.OrdinalIgnoreCase);
            var hasMappedCpuSupportUrl = !string.IsNullOrWhiteSpace(_hardware.CpuSupportUrl);
            var cpuSupportUrl = hasMappedCpuSupportUrl
                ? _hardware.CpuSupportUrl!
                : _settings.CustomCpuSupportUrl ?? "";
            CpuSupportUrlBox.Text = cpuSupportUrl;
            var needsCustomUrl = !string.IsNullOrWhiteSpace(savedCustomUrl) || string.IsNullOrWhiteSpace(supportUrl);
            var needsCpuCustomUrl = cpuGraphicsNeedsSeparatePackage && !hasMappedCpuSupportUrl;
            DriverSourcePanel.Visibility = needsCustomUrl || needsCpuCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlCheck.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlCheck.IsChecked = needsCustomUrl;
            CustomUrlPanel.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CpuSupportUrlPanel.Visibility = needsCpuCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            SourceSummaryText.Text = needsCustomUrl
                ? "No mapped AMD support page was found. Enter a custom AMD support URL."
                : $"Using detected AMD support page for {_hardware.GpuName}.";
            ServerCompatCheck.IsChecked = _hardware.IsServer;
            ServerCompatCheck.IsEnabled = false;
            var effectiveAudioVersion = audioForced
                ? _settings.LastInstalledAudioDriverVersion
                : _hardware.AudioDriverVersion;
            var audioInstalled = effectiveAudioVersion is not null;
            var audioNeedsUpdate = !Version.TryParse(effectiveAudioVersion, out var installedAudioVersion) ||
                installedAudioVersion < LatestBundledAudioVersion;
            _updatingOptions = true;
            AudioCheck.Content = !audioInstalled
                ? "Install Latest AMD HD Audio Driver"
                : audioNeedsUpdate
                    ? "Update to Latest AMD HD Audio Driver"
                    : "Reinstall Latest AMD HD Audio Driver";
            AudioCheck.IsChecked = _settings.InstallAudioDriver;
            AudioCheck.IsEnabled = true;
            _updatingOptions = false;
            AudioInstalledHint.ToolTip = !audioNeedsUpdate
                ? $"AMD HD Audio Driver {LatestBundledAudioVersion} or later is already installed."
                : audioInstalled
                    ? $"AMD HD Audio Driver {LatestBundledAudioVersion} is available."
                    : $"No AMD HD Audio driver is installed. Bundled version {LatestBundledAudioVersion} is available from Snappy Driver Installer.";
            UpdateCheckServiceButtonText.Text = _hardware.IsUpdateCheckServiceInstalled
                ? "Uninstall Update Check Service"
                : "Install Update Check Service";
            UpdateMpoButtonText();
            UpdateSelectedDriverText();
            Log("Ready.");
            await LoadDriversAsync(refreshDrivers);
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync(refreshDrivers: true);

    private async Task LoadDriversAsync(bool forceRefresh = false)
    {
        DriverCombo.ItemsSource = null;
        SelectedDriverText.Text = "";
        var supportUrl = SupportUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(supportUrl))
        {
            Log("No AMD support URL available yet.");
            return;
        }

        Log($"Loading AMD driver versions from {supportUrl}");
        var drivers = await _workflow.GetAvailableDriversAsync(supportUrl, Log, forceRefresh);
        IReadOnlyList<DriverSelection> selections;
        var cpuGraphicsNeedsSeparatePackage = _hardware?.CpuGraphicsAdapter is not null &&
            !string.Equals(_hardware.CpuGraphicsAdapter.InstanceId, _hardware.GpuInstanceId, StringComparison.OrdinalIgnoreCase);
        if (cpuGraphicsNeedsSeparatePackage)
        {
            var cpuSupportUrl = CpuSupportUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(cpuSupportUrl))
            {
                Log("CPU graphics support URL is required before selecting a synced driver version.");
                return;
            }

            Log($"Loading CPU graphics driver versions from {cpuSupportUrl}");
            var cpuDrivers = await _workflow.GetAvailableDriversAsync(cpuSupportUrl, Log, forceRefresh);
            var cpuByVersion = cpuDrivers
                .GroupBy(driver => driver.VersionText, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            selections = drivers
                .Where(driver => cpuByVersion.ContainsKey(driver.VersionText))
                .Select(driver => new DriverSelection(driver, cpuByVersion[driver.VersionText]))
                .ToList();
            if (selections.Count == 0)
            {
                Log("No driver version is shared by the discrete GPU and CPU graphics support pages.");
            }
        }
        else
        {
            selections = drivers.Select(driver => new DriverSelection(driver, null)).ToList();
        }

        DriverCombo.ItemsSource = selections;
        DriverCombo.SelectedItem = selections.FirstOrDefault(d => string.Equals(d.VersionText, _settings.SelectedDriverVersion, StringComparison.OrdinalIgnoreCase));
        DriverCombo.SelectedIndex = DriverCombo.SelectedItem is null && selections.Count > 0 ? 0 : DriverCombo.SelectedIndex;
        Log($"Loaded {selections.Count} driver option(s).");
    }

    private async void CustomUrlCheck_Changed(object sender, RoutedEventArgs e)
    {
        CustomUrlPanel.Visibility = CustomUrlCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_hardware is null)
        {
            return;
        }

        if (CustomUrlCheck.IsChecked != true)
        {
            SupportUrlBox.Text = _workflow.ResolveSupportUrl(_hardware) ?? "";
            var needsCustomUrl = string.IsNullOrWhiteSpace(SupportUrlBox.Text);
            DriverSourcePanel.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlCheck.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlPanel.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            SourceSummaryText.Text = string.IsNullOrWhiteSpace(SupportUrlBox.Text)
                ? "No mapped AMD support page was found. Enter a custom AMD support URL."
                : $"Using detected AMD support page for {_hardware.GpuName}.";
            await Busy(() => LoadDriversAsync());
        }
        else
        {
            SourceSummaryText.Text = "Using a custom AMD support page.";
        }

        SaveSettings();
    }

    private async void SupportUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (CustomUrlCheck.IsChecked == true)
        {
            SaveSettings();
            await Busy(() => LoadDriversAsync());
        }
    }

    private async void CpuSupportUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_hardware?.CpuGraphicsAdapter is not null)
        {
            SaveSettings();
            await Busy(() => LoadDriversAsync());
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var installed = false;
        InstallRequest? completedRequest = null;
        InstallResult? completedResult = null;
        await Busy(async () =>
        {
            var hardware = _hardware ?? await _workflow.GetHardwareInfoAsync();
            var request = new InstallRequest(
                hardware,
                DriverCombo.SelectedItem as DriverSelection,
                SupportUrlBox.Text.Trim(),
                HasPrimaryAmdGpu() && InstallDisplayDriverCheck.IsChecked == true,
                _hardware?.CpuGraphicsAdapter is not null && InstallCpuGraphicsDriverCheck.IsChecked == true,
                hardware.IsServer,
                AdrenalinCheck.IsChecked == true,
                hardware.IsAdrenalinInstalled && IsSelectedDriverDifferentFromCurrent(),
                AudioCheck.IsChecked == true
                    ? AudioInstallSource.BundledLatest
                    : hardware.AudioDriverVersion is null
                        ? AudioInstallSource.DriverPackage
                        : AudioInstallSource.None,
                AutoClearDownloadCacheCheck.IsChecked == true);

            var downloadProgress = new Progress<DriverDownloadProgress>(UpdateDownloadProgress);
            completedResult = await _workflow.InstallAsync(request, Log, downloadProgress);
            Log("Install workflow finished.");
            completedRequest = request;
            installed = true;
        });

        if (installed && completedRequest is not null && completedResult is not null)
        {
            RememberInstalledVersions(completedResult);
            await RefreshAfterInstallAsync(completedRequest, completedResult);
        }
    }

    private void RememberInstalledVersions(InstallResult result)
    {
        var installedAt = DateTimeOffset.UtcNow;
        if (result.PrimaryGpuPackageVersion is not null)
        {
            _settings.LastInstalledDisplayPackageVersion = result.PrimaryGpuPackageVersion;
            _settings.LastInstalledDisplayPackageAt = installedAt;
        }
        if (result.CpuGraphicsPackageVersion is not null)
        {
            _settings.LastInstalledCpuGraphicsPackageVersion = result.CpuGraphicsPackageVersion;
            _settings.LastInstalledCpuGraphicsPackageAt = installedAt;
        }
        if (result.AudioDriverVersion is not null)
        {
            _settings.LastInstalledAudioDriverVersion = result.AudioDriverVersion;
            _settings.LastInstalledAudioDriverAt = installedAt;
        }
        SaveSettings();
        _ = RefreshAfterForcedVersionExpiresAsync(installedAt);
    }

    private void RememberRemovedVersions(bool displayRemoved, bool audioRemoved, bool cpuGraphicsRemoved = false)
    {
        var removedAt = DateTimeOffset.UtcNow;
        if (displayRemoved)
        {
            _settings.LastInstalledDisplayPackageVersion = null;
            _settings.LastInstalledDisplayPackageAt = removedAt;
        }
        if (cpuGraphicsRemoved)
        {
            _settings.LastInstalledCpuGraphicsPackageVersion = null;
            _settings.LastInstalledCpuGraphicsPackageAt = removedAt;
        }
        if (audioRemoved)
        {
            _settings.LastInstalledAudioDriverVersion = null;
            _settings.LastInstalledAudioDriverAt = removedAt;
        }
        SaveSettings();
        _ = RefreshAfterForcedVersionExpiresAsync(removedAt);
    }

    private async Task RefreshAfterInstallAsync(InstallRequest request, InstallResult result)
    {
        var expectDisplay = result.PrimaryGpuPackageVersion is not null;
        var expectCpuGraphics = result.CpuGraphicsPackageVersion is not null;
        var expectAudio = result.AudioDriverVersion is not null;
        Log("Refreshing installed driver versions.");
        await RefreshAsync();
        if (expectDisplay || expectCpuGraphics || expectAudio)
        {
            PromptForRestart("Driver installation finished", "Restart Windows to finish applying the installed AMD drivers.");
            _ = ObserveInstalledDriverStateAsync(expectDisplay, expectCpuGraphics, expectAudio);
        }
    }

    private static bool IsForcedVersionCurrent(DateTimeOffset? installedAt) =>
        installedAt is not null && DateTimeOffset.UtcNow - installedAt.Value < ForcedVersionDuration;

    private string? GetEffectiveDisplayPackageVersion() =>
        IsForcedVersionCurrent(_settings.LastInstalledDisplayPackageAt)
            ? _settings.LastInstalledDisplayPackageVersion
            : _hardware?.DisplayDriverPackageVersion;

    private string? GetEffectiveCpuGraphicsPackageVersion() =>
        _hardware?.CpuGraphicsAdapter is { UsesBasicDisplayDriver: true }
            ? null
            : IsForcedVersionCurrent(_settings.LastInstalledCpuGraphicsPackageAt)
                ? _settings.LastInstalledCpuGraphicsPackageVersion
                : _hardware?.CpuGraphicsAdapter?.PackageVersion;

    private async Task RefreshAfterForcedVersionExpiresAsync(DateTimeOffset installedAt)
    {
        try
        {
            await Task.Delay(ForcedVersionDuration);
            if (_settings.LastInstalledDisplayPackageAt == installedAt || _settings.LastInstalledCpuGraphicsPackageAt == installedAt || _settings.LastInstalledAudioDriverAt == installedAt)
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Log("Could not refresh driver versions after the forced value expired: " + ex.Message);
        }
    }

    private async Task ObserveInstalledDriverStateAsync(bool expectDisplay, bool expectCpuGraphics, bool expectAudio)
    {
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                var hardware = await _workflow.GetHardwareInfoAsync();
                var displayReady = !expectDisplay || (hardware.DisplayDriverVersion is not null && hardware.DisplayDriverPackageVersion is not null);
                var cpuGraphicsReady = !expectCpuGraphics || (hardware.CpuGraphicsAdapter?.DriverVersion is not null && hardware.CpuGraphicsAdapter?.PackageVersion is not null);
                var audioReady = !expectAudio || hardware.AudioDriverVersion is not null;
                if (displayReady && cpuGraphicsReady && audioReady)
                {
                    Log("Windows finished refreshing installed driver information.");
                    await RefreshAsync();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log("Could not refresh installed driver information: " + ex.Message);
        }
    }

    private async void UpdateCheckServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var hardware = _hardware ?? await _workflow.GetHardwareInfoAsync();
        if (hardware.IsUpdateCheckServiceInstalled)
        {
            if (!AppDialog.Confirm(
                this,
                "Uninstall update check service",
                "Are you sure you want to uninstall the update check service?",
                "Uninstall",
                "Cancel"))
            {
                return;
            }

            await Busy(async () =>
            {
                await _workflow.UninstallUpdateCheckServiceAsync(Log);
                _hardware = hardware with { IsUpdateCheckServiceInstalled = false };
                UpdateCheckServiceButtonText.Text = "Install Update Check Service";
            });
            return;
        }

        var dialog = new UpdateCheckServiceInstallDialog(checkFrequency: GetSavedUpdateCheckFrequency()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await Busy(async () =>
        {
            await _workflow.InstallUpdateCheckServiceAsync(dialog.CheckOnBoot, dialog.CheckFrequency, Log);
            _settings.UpdateCheckFrequencyMinutes = (int)Math.Ceiling(dialog.CheckFrequency.TotalMinutes);
            SaveSettings();
            _hardware = hardware with { IsUpdateCheckServiceInstalled = true };
            UpdateCheckServiceButtonText.Text = "Uninstall Update Check Service";
        });
    }

    private async void ClearDownloadCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await Busy(() => _workflow.ClearDownloadCacheAsync(Log));
        _hasDownloadCache = false;
    }

    private async void UninstallGpuDriverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmUninstall("This removes the active AMD display driver. Windows may temporarily use Microsoft Basic Display Adapter."))
        {
            return;
        }

        var removed = false;
        await Busy(async () =>
        {
            await _workflow.UninstallDisplayDriverAsync(_hardware ?? await _workflow.GetHardwareInfoAsync(), Log);
            Log("AMD display driver removal finished.");
            removed = true;
        });
        if (removed)
        {
            var targetIsCpuGraphics = string.Equals(_hardware?.CpuGraphicsAdapter?.InstanceId, _hardware?.GpuInstanceId, StringComparison.OrdinalIgnoreCase);
            RememberRemovedVersions(displayRemoved: !targetIsCpuGraphics, audioRemoved: false, cpuGraphicsRemoved: targetIsCpuGraphics);
            await RefreshAfterDriverRemovalAsync(expectDisplayRemoved: true, expectAudioRemoved: false);
        }
    }

    private async void UninstallAudioDriverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmUninstall("This removes the active AMD HD Audio driver."))
        {
            return;
        }

        var removed = false;
        await Busy(async () =>
        {
            await _workflow.UninstallAudioDriverAsync(Log);
            removed = true;
        });
        if (removed)
        {
            RememberRemovedVersions(displayRemoved: false, audioRemoved: true);
            await RefreshAfterDriverRemovalAsync(expectDisplayRemoved: false, expectAudioRemoved: true);
        }
    }

    private async void UninstallAdrenalinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmUninstall("This removes AMD Software: Adrenalin Edition and keeps the display driver installed."))
        {
            return;
        }

        await Busy(() => _workflow.RemoveAdrenalinAsync(Log));
        await RefreshAsync();
    }

    private async void UninstallAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmUninstall("This removes active AMD display and HD Audio drivers plus AMD Software: Adrenalin Edition. Windows may temporarily use Microsoft Basic Display Adapter."))
        {
            return;
        }

        var removed = false;
        await Busy(async () =>
        {
            await _workflow.UninstallDriverAndSoftwareAsync(_hardware ?? await _workflow.GetHardwareInfoAsync(), Log);
            await _workflow.RemoveLocalSigningCertificateAsync(Log);
            Log("AMD driver and software removal finished.");
            removed = true;
        });
        if (removed)
        {
            RememberRemovedVersions(displayRemoved: true, audioRemoved: true, cpuGraphicsRemoved: _hardware?.CpuGraphicsAdapter is not null);
            await RefreshAfterDriverRemovalAsync(expectDisplayRemoved: true, expectAudioRemoved: true);
        }
    }

    private async Task RefreshAfterDriverRemovalAsync(bool expectDisplayRemoved, bool expectAudioRemoved)
    {
        Log("Refreshing installed driver versions.");
        await RefreshAsync();
        PromptForRestart("Driver removal finished", "Restart Windows to finish removing the AMD drivers.");
        _ = ObserveRemovedDriverStateAsync(expectDisplayRemoved, expectAudioRemoved);
    }

    private async Task ObserveRemovedDriverStateAsync(bool expectDisplayRemoved, bool expectAudioRemoved)
    {
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                var hardware = await _workflow.GetHardwareInfoAsync();
                var displayRemoved = !expectDisplayRemoved || hardware.DisplayDriverVersion is null;
                var audioRemoved = !expectAudioRemoved || hardware.AudioDriverVersion is null;
                if (displayRemoved && audioRemoved)
                {
                    Log("Windows finished refreshing removed driver information.");
                    await RefreshAsync();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log("Could not refresh removed driver information: " + ex.Message);
        }
    }

    private void PromptForRestart(string heading, string message)
    {
        if (!AppDialog.Confirm(this, heading, message, "Restart Now", "Later"))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = true });
    }

    private bool ConfirmUninstall(string message) => AppDialog.Confirm(this, "Confirm removal", message);

    private async void ToggleMpoButton_Click(object sender, RoutedEventArgs e)
    {
        var disable = _hardware?.IsMpoDisabled != true;
        await Busy(async () =>
        {
            var message = await _workflow.SetMpoOverrideAsync(disable, Log);
            if (_hardware is not null)
            {
                _hardware = _hardware with { IsMpoDisabled = disable };
                UpdateMpoButtonText();
            }

            AppDialog.Show(this, "MPO setting changed", message);
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void AboutButton_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void UpdateSelectedDriverText()
    {
        if (DriverCombo.SelectedItem is not DriverSelection driver)
        {
            SelectedDriverText.Text = "";
            InstallDisplayDriverCheck.Content = "Install GPU Driver";
            InstallCpuGraphicsDriverCheck.Content = "Install iGPU Driver";
            UpdateInstallTargetControls();
            UpdateAdrenalinControl();
            return;
        }

        SelectedDriverText.Text = $"{driver.ReleaseDateText}  {driver.FileSizeText}";
        if (!_applyingSettings)
        {
            _settings.SelectedDriverVersion = driver.VersionText;
            SaveSettings();
        }
        InstallDisplayDriverCheck.Content = GetDriverActionText(driver.VersionText, GetEffectiveDisplayPackageVersion(), "GPU");
        InstallCpuGraphicsDriverCheck.Content = GetDriverActionText(driver.VersionText, GetEffectiveCpuGraphicsPackageVersion(), "iGPU");
        UpdateInstallTargetControls();
        UpdateAdrenalinControl();
    }

    private static string FormatGpuList(HardwareInfo hardware)
    {
        var primary = hardware.PrimaryDisplayAdapter;
        if (primary is null) return "No physical display adapter detected.";

        var lines = new List<string> { $"Primary Display: {primary.Name ?? "Unknown display adapter"}" };
        lines.AddRange((hardware.DisplayAdapters ?? [])
            .Where(adapter => !string.Equals(adapter.InstanceId, primary.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(adapter.InstanceId, hardware.CpuGraphicsAdapter?.InstanceId, StringComparison.OrdinalIgnoreCase))
            .Select(adapter => $"Other GPU: {adapter.Name ?? "Unknown display adapter"}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatCpuDisplayName(string? cpuName)
    {
        var name = cpuName?.Trim() ?? "AMD processor";
        return System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+\s*[- ]Core Processor\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string GetDriverActionText(string? selectedVersion, string? currentVersion, string target) =>
        Version.TryParse(selectedVersion, out var selected) && Version.TryParse(currentVersion, out var current)
            ? selected > current
                ? $"Update {target} Driver"
                : selected < current
                    ? $"Downgrade {target} Driver"
                    : $"Reinstall {target} Driver"
            : $"Install {target} Driver";

    private bool HasPrimaryAmdGpu()
    {
        if (_hardware?.PrimaryDisplayAdapter is not { } primary || string.IsNullOrWhiteSpace(_hardware.GpuInstanceId)) return false;
        return string.Equals(primary.InstanceId, _hardware.GpuInstanceId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_hardware.CpuGraphicsAdapter?.InstanceId, _hardware.GpuInstanceId, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateInstallTargetControls()
    {
        var hasPrimaryAmdGpu = HasPrimaryAmdGpu();
        InstallDisplayDriverCheck.Visibility = hasPrimaryAmdGpu ? Visibility.Visible : Visibility.Collapsed;
        InstallDisplayDriverCheck.IsEnabled = hasPrimaryAmdGpu;
        InstallDisplayDriverCheck.ToolTip = null;

        var hasCpuGraphics = _hardware?.CpuGraphicsAdapter is not null;
        InstallCpuGraphicsDriverCheck.Visibility = hasCpuGraphics ? Visibility.Visible : Visibility.Collapsed;
        InstallCpuGraphicsDriverCheck.IsEnabled = hasCpuGraphics;
    }

    private void UpdateMpoButtonText() => ToggleMpoButtonText.Text = _hardware?.IsMpoDisabled == true
        ? "Turn MPO On"
        : "Turn MPO Off";

    private bool IsSelectedDriverDifferentFromCurrent()
    {
        var selectedVersion = (DriverCombo.SelectedItem as DriverSelection)?.VersionText;
        var currentVersion = GetEffectiveDisplayPackageVersion();
        return Version.TryParse(selectedVersion, out var selected) &&
            Version.TryParse(currentVersion, out var current) &&
            selected != current;
    }

    private void UpdateAdrenalinControl()
    {
        var currentVersion = GetEffectiveDisplayPackageVersion();
        var selectedVersion = (DriverCombo.SelectedItem as DriverSelection)?.VersionText;
        var action = "Install";
        var forceInstall = false;
        var adrenalinInstalled = _hardware?.IsAdrenalinInstalled == true;

        if (adrenalinInstalled && Version.TryParse(selectedVersion, out var selected) && Version.TryParse(currentVersion, out var current))
        {
            if (selected > current)
            {
                action = "Update";
                forceInstall = true;
            }
            else if (selected < current)
            {
                action = "Downgrade";
                forceInstall = true;
            }
            else
            {
                action = "Reinstall";
            }
        }
        else if (adrenalinInstalled)
        {
            action = "Reinstall";
        }

        _updatingOptions = true;
        AdrenalinCheck.Content = $"{action} AMD Software: Adrenalin Edition";
        AdrenalinCheck.IsChecked = forceInstall || _settings.InstallAdrenalin;
        AdrenalinCheck.IsEnabled = !forceInstall;
        _updatingOptions = false;
    }

    private void ApplySavedSettings()
    {
        _applyingSettings = true;
        InstallDisplayDriverCheck.IsChecked = _settings.InstallGpuDriver;
        InstallCpuGraphicsDriverCheck.IsChecked = _settings.InstallCpuGraphicsDriver;
        AdrenalinCheck.IsChecked = _settings.InstallAdrenalin;
        AudioCheck.IsChecked = _settings.InstallAudioDriver;
        AutoClearDownloadCacheCheck.IsChecked = _settings.AutoClearDownloadedCache;
        _applyingSettings = false;
        InstallDisplayDriverCheck.Checked += (_, _) => SaveSettings();
        InstallDisplayDriverCheck.Unchecked += (_, _) => SaveSettings();
        InstallCpuGraphicsDriverCheck.Checked += (_, _) => SaveSettings();
        InstallCpuGraphicsDriverCheck.Unchecked += (_, _) => SaveSettings();
        AdrenalinCheck.Checked += (_, _) => SaveSettings();
        AdrenalinCheck.Unchecked += (_, _) => SaveSettings();
        AudioCheck.Checked += (_, _) => SaveSettings();
        AudioCheck.Unchecked += (_, _) => SaveSettings();
        AutoClearDownloadCacheCheck.Checked += (_, _) => SaveSettings();
        AutoClearDownloadCacheCheck.Unchecked += (_, _) => SaveSettings();
    }

    private void SaveSettings()
    {
        if (_applyingSettings || _updatingOptions)
        {
            return;
        }

        _settings.InstallGpuDriver = InstallDisplayDriverCheck.IsChecked == true;
        _settings.InstallCpuGraphicsDriver = InstallCpuGraphicsDriverCheck.IsChecked == true;
        _settings.InstallAdrenalin = AdrenalinCheck.IsChecked == true;
        _settings.InstallAudioDriver = AudioCheck.IsChecked == true;
        _settings.AutoClearDownloadedCache = AutoClearDownloadCacheCheck.IsChecked == true;
        _settings.CustomSupportUrl = CustomUrlCheck.IsChecked == true ? SupportUrlBox.Text.Trim() : null;
        if (_hardware?.CpuGraphicsAdapter is not null && string.IsNullOrWhiteSpace(_hardware.CpuSupportUrl))
        {
            _settings.CustomCpuSupportUrl = CpuSupportUrlBox.Text.Trim();
        }
        var currentSettings = UserSettingsStore.Load();
        _settings.IgnoredAppUpdateVersion = currentSettings.IgnoredAppUpdateVersion;
        UserSettingsStore.Save(_settings);
    }

    private TimeSpan GetSavedUpdateCheckFrequency()
    {
        var minutes = _settings.UpdateCheckFrequencyMinutes > 0
            ? _settings.UpdateCheckFrequencyMinutes
            : 6 * 60;
        return TimeSpan.FromMinutes(Math.Max(minutes, 60));
    }

    private async Task Busy(Func<Task> action)
    {
        if (Interlocked.Increment(ref _busyOperationCount) == 1)
        {
            ApplyBusyState(true);
        }

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            AppDialog.ShowError(this, "Operation failed", ex.Message);
        }
        finally
        {
            if (Interlocked.Decrement(ref _busyOperationCount) == 0)
            {
                ApplyBusyState(false);
            }
        }
    }

    private void ApplyBusyState(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        Progress.IsIndeterminate = busy;
        if (!busy) DownloadProgressPanel.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy;
        ClearDownloadCacheButton.IsEnabled = !busy && _hasDownloadCache;
        ThemeCombo.IsEnabled = !busy;
        AboutButton.IsEnabled = true;
        InstallButton.IsEnabled = !busy;
        ToggleMpoButton.IsEnabled = !busy;
        UpdateCheckServiceButton.IsEnabled = !busy;
        UninstallGpuDriverButton.IsEnabled = !busy && _canUninstallGpuDriver;
        UninstallAudioDriverButton.IsEnabled = !busy && _canUninstallAudioDriver;
        UninstallAdrenalinButton.IsEnabled = !busy && _canUninstallAdrenalin;
        UninstallAllButton.IsEnabled = !busy && (_canUninstallGpuDriver || _canUninstallAudioDriver || _canUninstallAdrenalin);
        InstallOptionsPanel.Visibility = Visibility.Visible;
        InstallOptionsPanel.IsEnabled = !busy;
    }

    private void UpdateDownloadProgress(DriverDownloadProgress progress)
    {
        if (progress.IsComplete)
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DownloadProgressPanel.Visibility = Visibility.Visible;
        DriverDownloadProgressBar.IsIndeterminate = progress.TotalBytes is null;
        if (progress.TotalBytes is > 0)
        {
            var percentage = Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes.Value, 0, 100);
            DriverDownloadProgressBar.Value = percentage;
            DownloadStatusText.Text = $"AMD Driver {progress.DriverVersion}: [{percentage:0}%] {FormatMegabytes(progress.DownloadedBytes)} / {FormatMegabytes(progress.TotalBytes.Value)}";
        }
        else
        {
            DownloadStatusText.Text = $"AMD Driver {progress.DriverVersion}: {FormatMegabytes(progress.DownloadedBytes)}";
        }

        var speedText = progress.BytesPerSecond is > 0
            ? $"{progress.BytesPerSecond.Value / 1024d / 1024d:F1} MB/s"
            : "Calculating speed...";
        DownloadSpeedText.Text = progress.EstimatedRemaining is { } remaining
            ? $"{FormatRemaining(remaining)} remaining - {speedText}"
            : speedText;
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / 1024d / 1024d:F1} MB";

    private static string FormatRemaining(TimeSpan remaining) => remaining.TotalHours >= 1
        ? $"{(int)remaining.TotalHours}:{remaining:mm\\:ss}"
        : $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _workflow.Dispose();
        base.OnClosing(e);
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_themeMode != AppThemeMode.System)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            ApplyTheme();
            ApplyTitleBarTheme();
        });
    }

    private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _themeMode = ThemeCombo.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
        ApplyTheme();
        ApplyTitleBarTheme();
    }

    private void ApplyTheme()
    {
        var dark = IsDarkTheme();
        SetThemeBrush("WindowBackgroundBrush", dark ? "#241D1A" : "#F1E9E4");
        SetThemeBrush("PanelBackgroundBrush", dark ? "#2F2B2E" : "#FFFCFB");
        SetThemeBrush("PanelBorderBrush", dark ? "#4F464B" : "#DFD4D7");
        SetThemeBrush("PrimaryTextBrush", dark ? "#F1EEF0" : "#292529");
        SetThemeBrush("SecondaryTextBrush", dark ? "#C2BCC1" : "#635A60");
        SetThemeBrush("InputBackgroundBrush", dark ? "#383338" : "#FFFEFD");
        SetThemeBrush("InputBorderBrush", dark ? "#675A60" : "#C7B8BB");
        SetThemeBrush("ButtonBackgroundBrush", dark ? "#423B40" : "#EEE6E8");
        SetThemeBrush("ButtonForegroundBrush", dark ? "#F4F1F2" : "#292529");
        SetThemeBrush("LogBackgroundBrush", dark ? "#242124" : "#FFFBFB");
        SetThemeBrush("LogForegroundBrush", dark ? "#ECE9EC" : "#292529");
        SetThemeBrush("LogoBrush", dark ? "#F05A48" : "#D93626");
        SetThemeBrush("LogoHoverBrush", dark ? "#C94738" : "#B72D20");
        SetThemeBrush("SelectionBackgroundBrush", dark ? "#54494F" : "#E8DBDF");
        SetThemeBrush("ScrollTrackBrush", dark ? "#242124" : "#ECE4E6");
        SetThemeBrush("ScrollThumbBrush", dark ? "#675A62" : "#B9A6AA");
        SetThemeBrush("ScrollThumbHoverBrush", dark ? "#7B6A73" : "#A0818B");
    }

    private void SetThemeBrush(string key, string color)
    {
        var brush = Brush(color);
        Resources[key] = brush;
        System.Windows.Application.Current.Resources[key] = brush;
    }

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = IsDarkTheme();
        var enabled = dark ? 1 : 0;

        if (DwmSetWindowAttribute(handle, DwmWindowAttribute.UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        if (dark)
        {
            var captionColor = ColorRef(0x2F, 0x2B, 0x2E);
            var textColor = ColorRef(0xF1, 0xEE, 0xF0);
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.TextColor, ref textColor, sizeof(int));
        }
        else
        {
            var defaultColor = unchecked((int)0xFFFFFFFF);
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref defaultColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.TextColor, ref defaultColor, sizeof(int));
        }
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    private bool IsDarkTheme()
    {
        return _themeMode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            _ => IsSystemDarkTheme()
        };
    }

    private static bool IsSystemDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int value, int size);

    private enum DwmWindowAttribute
    {
        UseImmersiveDarkModeBefore20H1 = 19,
        UseImmersiveDarkMode = 20,
        CaptionColor = 35,
        TextColor = 36
    }

    private enum AppThemeMode
    {
        System,
        Light,
        Dark
    }
}
