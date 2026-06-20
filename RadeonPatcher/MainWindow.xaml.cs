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

    public MainWindow()
    {
        InitializeComponent();
        _settings = UserSettingsStore.Load();
        ApplySavedSettings();
        ApplyTheme();
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        DriverCombo.SelectionChanged += (_, _) => UpdateSelectedDriverText();
        Loaded += async (_, _) => await RefreshAsync();
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
                await _workflow.EnsureUpdateCheckServiceCurrentAsync(Log);
            }
            _hasDownloadCache = _workflow.HasDownloadCache();
            _canUninstallGpuDriver = !string.IsNullOrWhiteSpace(_hardware.DisplayDriverOriginalInf);
            _canUninstallAudioDriver = _hardware.AudioDriverVersion is not null;
            _canUninstallAdrenalin = _hardware.IsAdrenalinInstalled;

            GpuText.Text = _hardware.GpuName ?? "No AMD display adapter detected.";
            var displayForced = IsForcedVersionCurrent(_settings.LastInstalledDisplayPackageAt);
            var audioForced = IsForcedVersionCurrent(_settings.LastInstalledAudioDriverAt);
            DisplayDriverText.Text = displayForced && !string.IsNullOrWhiteSpace(_settings.LastInstalledDisplayPackageVersion)
                ? $"Installed Video Driver: {_settings.LastInstalledDisplayPackageVersion}"
                : _hardware.DisplayDriverPackageVersion is null
                ? _hardware.DisplayDriverVersion is null
                    ? "Installed Video Driver: None"
                    : "Installed Video Driver: Unknown"
                : $"Installed Video Driver: {_hardware.DisplayDriverPackageVersion}";
            OsText.Text = $"{_hardware.OsName} ({_hardware.OsVersion})";
            AudioText.Text = audioForced && !string.IsNullOrWhiteSpace(_settings.LastInstalledAudioDriverVersion)
                ? $"Installed AMD HD Audio Driver: {_settings.LastInstalledAudioDriverVersion}"
                : _hardware.AudioDriverVersion is null
                ? "Installed AMD HD Audio Driver: None"
                : $"Installed AMD HD Audio Driver: {_hardware.AudioDriverVersion}";

            var savedCustomUrl = _settings.CustomSupportUrl;
            var supportUrl = string.IsNullOrWhiteSpace(savedCustomUrl)
                ? _workflow.ResolveSupportUrl(_hardware) ?? ""
                : savedCustomUrl;
            SupportUrlBox.Text = supportUrl;
            var needsCustomUrl = !string.IsNullOrWhiteSpace(savedCustomUrl) || string.IsNullOrWhiteSpace(supportUrl);
            DriverSourcePanel.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlCheck.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlCheck.IsChecked = needsCustomUrl;
            CustomUrlPanel.Visibility = needsCustomUrl ? Visibility.Visible : Visibility.Collapsed;
            SourceSummaryText.Text = needsCustomUrl
                ? "No mapped AMD support page was found. Enter a custom AMD support URL."
                : $"Using detected AMD support page for {_hardware.GpuName}.";
            ServerCompatCheck.IsChecked = _hardware.IsServer;
            ServerCompatCheck.IsEnabled = false;
            var audioInstalled = _hardware.AudioDriverVersion is not null;
            var audioNeedsUpdate = !Version.TryParse(_hardware.AudioDriverVersion, out var installedAudioVersion) ||
                installedAudioVersion < LatestBundledAudioVersion;
            _updatingOptions = true;
            AudioCheck.IsChecked = audioNeedsUpdate && _settings.InstallAudioDriver;
            AudioCheck.IsEnabled = audioNeedsUpdate;
            _updatingOptions = false;
            AudioInstalledHint.ToolTip = !audioNeedsUpdate
                ? $"AMD HD Audio Driver {LatestBundledAudioVersion} or later is already installed."
                : audioInstalled
                    ? $"AMD HD Audio Driver {LatestBundledAudioVersion} is available."
                : "AMD does not provide the latest driver with their GPU Drivers, this driver is sourced from Snappy Driver Installer.";
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
        DriverCombo.ItemsSource = drivers;
        DriverCombo.SelectedItem = drivers.FirstOrDefault(d => string.Equals(d.VersionText, _settings.SelectedDriverVersion, StringComparison.OrdinalIgnoreCase));
        DriverCombo.SelectedIndex = DriverCombo.SelectedItem is null && drivers.Count > 0 ? 0 : DriverCombo.SelectedIndex;
        Log($"Loaded {drivers.Count} driver option(s).");
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
                DriverCombo.SelectedItem as DriverRelease,
                SupportUrlBox.Text.Trim(),
                InstallDisplayDriverCheck.IsChecked == true,
                hardware.IsServer,
                AdrenalinCheck.IsChecked == true,
                hardware.IsAdrenalinInstalled && IsSelectedDriverDifferentFromCurrent(),
                AudioCheck.IsChecked == true
                    ? AudioInstallSource.BundledLatest
                    : hardware.AudioDriverVersion is null
                        ? AudioInstallSource.DriverPackage
                        : AudioInstallSource.None,
                AutoClearDownloadCacheCheck.IsChecked == true);

            completedResult = await _workflow.InstallAsync(request, Log);
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
        if (result.DisplayPackageVersion is not null)
        {
            _settings.LastInstalledDisplayPackageVersion = result.DisplayPackageVersion;
            _settings.LastInstalledDisplayPackageAt = installedAt;
        }
        if (result.AudioDriverVersion is not null)
        {
            _settings.LastInstalledAudioDriverVersion = result.AudioDriverVersion;
            _settings.LastInstalledAudioDriverAt = installedAt;
        }
        SaveSettings();
        _ = RefreshAfterForcedVersionExpiresAsync(installedAt);
    }

    private async Task RefreshAfterInstallAsync(InstallRequest request, InstallResult result)
    {
        var expectDisplay = result.DisplayPackageVersion is not null;
        var expectAudio = result.AudioDriverVersion is not null;
        Log("Refreshing installed driver versions.");
        await RefreshAsync();
        if (expectDisplay || expectAudio)
        {
            PromptForRestart("Driver installation finished", "Restart Windows to finish applying the installed AMD drivers.");
            _ = ObserveInstalledDriverStateAsync(expectDisplay, expectAudio);
        }
    }

    private static bool IsForcedVersionCurrent(DateTimeOffset? installedAt) =>
        installedAt is not null && DateTimeOffset.UtcNow - installedAt.Value < ForcedVersionDuration;

    private async Task RefreshAfterForcedVersionExpiresAsync(DateTimeOffset installedAt)
    {
        try
        {
            await Task.Delay(ForcedVersionDuration);
            if (_settings.LastInstalledDisplayPackageAt == installedAt || _settings.LastInstalledAudioDriverAt == installedAt)
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Log("Could not refresh driver versions after the forced value expired: " + ex.Message);
        }
    }

    private async Task ObserveInstalledDriverStateAsync(bool expectDisplay, bool expectAudio)
    {
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                var hardware = await _workflow.GetHardwareInfoAsync();
                var displayReady = !expectDisplay || (hardware.DisplayDriverVersion is not null && hardware.DisplayDriverPackageVersion is not null);
                var audioReady = !expectAudio || hardware.AudioDriverVersion is not null;
                if (displayReady && audioReady)
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
        await Busy(async () =>
        {
            var installed = await _workflow.ToggleUpdateCheckServiceAsync(hardware.IsUpdateCheckServiceInstalled, Log);
            _hardware = hardware with { IsUpdateCheckServiceInstalled = installed };
            UpdateCheckServiceButtonText.Text = installed
                ? "Uninstall Update Check Service"
                : "Install Update Check Service";
        });
    }

    private async void ClearDownloadCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await Busy(() => _workflow.ClearDownloadCacheAsync(Log));
        _hasDownloadCache = false;
        SetBusy(false);
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

        var removeDisplay = _canUninstallGpuDriver;
        var removeAudio = _canUninstallAudioDriver;
        var removed = false;
        await Busy(async () =>
        {
            await _workflow.UninstallDriverAndSoftwareAsync(_hardware ?? await _workflow.GetHardwareInfoAsync(), Log);
            await _workflow.RemoveLocalSigningCertificateAsync(Log);
            Log("AMD driver and software removal finished.");
            removed = true;
        });
        if (removed && (removeDisplay || removeAudio))
        {
            await RefreshAfterDriverRemovalAsync(removeDisplay, removeAudio);
        }
        else if (removed)
        {
            await RefreshAsync();
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

    private void UpdateSelectedDriverText()
    {
        if (DriverCombo.SelectedItem is not DriverRelease driver)
        {
            SelectedDriverText.Text = "";
            InstallDisplayDriverCheck.Content = "Install GPU Driver";
            UpdateAdrenalinControl();
            return;
        }

        SelectedDriverText.Text = $"{driver.ReleaseDateText}  {driver.FileSizeText}";
        if (!_applyingSettings)
        {
            _settings.SelectedDriverVersion = driver.VersionText;
            SaveSettings();
        }
        var currentVersion = _hardware?.DisplayDriverPackageVersion;
        if (Version.TryParse(driver.VersionText, out var selected) && Version.TryParse(currentVersion, out var current))
        {
            InstallDisplayDriverCheck.Content = selected > current
                ? "Update GPU Driver"
                : selected < current
                    ? "Downgrade GPU Driver"
                    : "Reinstall GPU Driver";
            UpdateAdrenalinControl();
            return;
        }

        InstallDisplayDriverCheck.Content = "Install GPU Driver";
        UpdateAdrenalinControl();
    }

    private void UpdateMpoButtonText() => ToggleMpoButtonText.Text = _hardware?.IsMpoDisabled == true
        ? "Turn MPO On"
        : "Turn MPO Off";

    private bool IsSelectedDriverDifferentFromCurrent()
    {
        var selectedVersion = (DriverCombo.SelectedItem as DriverRelease)?.VersionText;
        var currentVersion = _hardware?.DisplayDriverPackageVersion;
        return Version.TryParse(selectedVersion, out var selected) &&
            Version.TryParse(currentVersion, out var current) &&
            selected != current;
    }

    private void UpdateAdrenalinControl()
    {
        var currentVersion = _hardware?.DisplayDriverPackageVersion;
        var selectedVersion = (DriverCombo.SelectedItem as DriverRelease)?.VersionText;
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
        AdrenalinCheck.IsChecked = _settings.InstallAdrenalin;
        AudioCheck.IsChecked = _settings.InstallAudioDriver;
        AutoClearDownloadCacheCheck.IsChecked = _settings.AutoClearDownloadedCache;
        _applyingSettings = false;
        InstallDisplayDriverCheck.Checked += (_, _) => SaveSettings();
        InstallDisplayDriverCheck.Unchecked += (_, _) => SaveSettings();
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
        _settings.InstallAdrenalin = AdrenalinCheck.IsChecked == true;
        _settings.InstallAudioDriver = AudioCheck.IsChecked == true;
        _settings.AutoClearDownloadedCache = AutoClearDownloadCacheCheck.IsChecked == true;
        _settings.CustomSupportUrl = CustomUrlCheck.IsChecked == true ? SupportUrlBox.Text.Trim() : null;
        UserSettingsStore.Save(_settings);
    }

    private async Task Busy(Func<Task> action)
    {
        try
        {
            SetBusy(true);
            await action();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            AppDialog.ShowError(this, "Operation failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        Progress.IsIndeterminate = busy;
        RefreshButton.IsEnabled = !busy;
        ClearDownloadCacheButton.IsEnabled = !busy && _hasDownloadCache;
        ThemeCombo.IsEnabled = !busy;
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
        SetThemeBrush("WindowBackgroundBrush", dark ? "#202226" : "#F3F5F7");
        SetThemeBrush("PanelBackgroundBrush", dark ? "#2B2E33" : "#FFFFFF");
        SetThemeBrush("PanelBorderBrush", dark ? "#444A53" : "#D7DEE7");
        SetThemeBrush("PrimaryTextBrush", dark ? "#ECEFF3" : "#1F2933");
        SetThemeBrush("SecondaryTextBrush", dark ? "#B7C0CA" : "#52606D");
        SetThemeBrush("InputBackgroundBrush", dark ? "#343840" : "#FFFFFF");
        SetThemeBrush("InputBorderBrush", dark ? "#59616C" : "#BAC5D1");
        SetThemeBrush("ButtonBackgroundBrush", dark ? "#3A4049" : "#E8EEF5");
        SetThemeBrush("ButtonForegroundBrush", dark ? "#F2F5F8" : "#1F2933");
        SetThemeBrush("LogBackgroundBrush", dark ? "#24272D" : "#FFFFFF");
        SetThemeBrush("LogForegroundBrush", dark ? "#E5EAF0" : "#1F2933");
        SetThemeBrush("LogoBrush", dark ? "#F05A48" : "#D93626");
        SetThemeBrush("SelectionBackgroundBrush", dark ? "#46505C" : "#DCE7F3");
        SetThemeBrush("ScrollTrackBrush", dark ? "#24272D" : "#E6EBF1");
        SetThemeBrush("ScrollThumbBrush", dark ? "#586270" : "#A9B5C2");
        SetThemeBrush("ScrollThumbHoverBrush", dark ? "#6B7685" : "#8795A5");
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
            var captionColor = ColorRef(0x2B, 0x2E, 0x33);
            var textColor = ColorRef(0xEC, 0xEF, 0xF3);
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
