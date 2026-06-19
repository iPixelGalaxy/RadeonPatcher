using System.ComponentModel;
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
    private readonly DriverWorkflow _workflow = new();
    private readonly UserSettings _settings;
    private HardwareInfo? _hardware;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private bool _applyingSettings;

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

    private async Task RefreshAsync()
    {
        await Busy(async () =>
        {
            Log("Refreshing detected hardware and extracting embedded tools.");
            _hardware = await _workflow.GetHardwareInfoAsync();
            await _workflow.EnsurePayloadsAsync();

            GpuText.Text = _hardware.GpuName ?? "No AMD display adapter detected.";
            DisplayDriverText.Text = _hardware.DisplayDriverPackageVersion is null
                ? $"Current Driver: {_hardware.DisplayDriverVersion ?? "unknown"}"
                : $"Current Driver: {_hardware.DisplayDriverPackageVersion}";
            OsText.Text = $"{_hardware.OsName} ({_hardware.OsVersion})";
            AudioText.Text = _hardware.AudioDriverVersion is null
                ? "AMD HD Audio: not detected"
                : $"AMD HD Audio: {_hardware.AudioDriverVersion}";

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
            ServerCompatCheck.Visibility = _hardware.IsServer ? Visibility.Visible : Visibility.Collapsed;
            ServerCompatCheck.IsChecked = _hardware.IsServer;
            ServerCompatCheck.IsEnabled = false;
            AudioCheck.IsChecked = _settings.InstallAudioDriver;
            AudioCheck.Visibility = _hardware.AudioDriverVersion is null ? Visibility.Visible : Visibility.Collapsed;
            UpdateCheckServiceButtonText.Text = _hardware.IsUpdateCheckServiceInstalled
                ? "Uninstall Update Check Service"
                : "Install Update Check Service";
            AdrenalinCheck.Content = _hardware.IsAdrenalinInstalled
                ? "Reinstall AMD Software: Adrenalin Edition"
                : "Install AMD Software: Adrenalin Edition";
            UpdateMpoButtonText();
            UpdateSelectedDriverText();
            Log("Ready.");
            await LoadDriversAsync();
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task LoadDriversAsync()
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
        var drivers = await _workflow.GetAvailableDriversAsync(supportUrl, Log);
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
            await Busy(LoadDriversAsync);
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
            await Busy(LoadDriversAsync);
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
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
                AudioCheck.IsChecked == true);

            await _workflow.InstallAsync(request, Log);
            Log("Install workflow finished.");
        });
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

            System.Windows.MessageBox.Show(this, message, "RadeonPatcher", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void UpdateSelectedDriverText()
    {
        if (DriverCombo.SelectedItem is not DriverRelease driver)
        {
            SelectedDriverText.Text = "";
            InstallDisplayDriverCheck.Content = "Install GPU Driver";
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
            return;
        }

        InstallDisplayDriverCheck.Content = "Install GPU Driver";
    }

    private void UpdateMpoButtonText() => ToggleMpoButtonText.Text = _hardware?.IsMpoDisabled == true
        ? "Turn MPO On"
        : "Turn MPO Off";

    private void ApplySavedSettings()
    {
        _applyingSettings = true;
        InstallDisplayDriverCheck.IsChecked = _settings.InstallGpuDriver;
        AdrenalinCheck.IsChecked = _settings.InstallAdrenalin;
        AudioCheck.IsChecked = _settings.InstallAudioDriver;
        _applyingSettings = false;
        InstallDisplayDriverCheck.Checked += (_, _) => SaveSettings();
        InstallDisplayDriverCheck.Unchecked += (_, _) => SaveSettings();
        AdrenalinCheck.Checked += (_, _) => SaveSettings();
        AdrenalinCheck.Unchecked += (_, _) => SaveSettings();
        AudioCheck.Checked += (_, _) => SaveSettings();
        AudioCheck.Unchecked += (_, _) => SaveSettings();
    }

    private void SaveSettings()
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.InstallGpuDriver = InstallDisplayDriverCheck.IsChecked == true;
        _settings.InstallAdrenalin = AdrenalinCheck.IsChecked == true;
        _settings.InstallAudioDriver = AudioCheck.IsChecked == true;
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
            System.Windows.MessageBox.Show(this, ex.Message, "RadeonPatcher", MessageBoxButton.OK, MessageBoxImage.Error);
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
        ThemeCombo.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        ToggleMpoButton.IsEnabled = !busy;
        UpdateCheckServiceButton.IsEnabled = !busy;
        InstallOptionsPanel.Visibility = busy ? Visibility.Hidden : Visibility.Visible;
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
        Resources["WindowBackgroundBrush"] = Brush(dark ? "#202226" : "#F3F5F7");
        Resources["PanelBackgroundBrush"] = Brush(dark ? "#2B2E33" : "#FFFFFF");
        Resources["PanelBorderBrush"] = Brush(dark ? "#444A53" : "#D7DEE7");
        Resources["PrimaryTextBrush"] = Brush(dark ? "#ECEFF3" : "#1F2933");
        Resources["SecondaryTextBrush"] = Brush(dark ? "#B7C0CA" : "#52606D");
        Resources["InputBackgroundBrush"] = Brush(dark ? "#343840" : "#FFFFFF");
        Resources["InputBorderBrush"] = Brush(dark ? "#59616C" : "#BAC5D1");
        Resources["ButtonBackgroundBrush"] = Brush(dark ? "#3A4049" : "#E8EEF5");
        Resources["ButtonForegroundBrush"] = Brush(dark ? "#F2F5F8" : "#1F2933");
        Resources["LogBackgroundBrush"] = Brush(dark ? "#24272D" : "#FFFFFF");
        Resources["LogForegroundBrush"] = Brush(dark ? "#E5EAF0" : "#1F2933");
        Resources["LogoBrush"] = Brush(dark ? "#F05A48" : "#D93626");
        Resources["SelectionBackgroundBrush"] = Brush(dark ? "#46505C" : "#DCE7F3");
        Resources["ScrollTrackBrush"] = Brush(dark ? "#24272D" : "#E6EBF1");
        Resources["ScrollThumbBrush"] = Brush(dark ? "#586270" : "#A9B5C2");
        Resources["ScrollThumbHoverBrush"] = Brush(dark ? "#6B7685" : "#8795A5");
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
