using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RadeonPatcher;

public sealed record HardwareInfo(
    string? GpuName,
    string? GpuInstanceId,
    string? DisplayDriverVersion,
    string? DisplayDriverPackageVersion,
    string? AudioDriverVersion,
    string OsName,
    string OsVersion,
    bool IsServer,
    bool IsMpoDisabled,
    bool IsUpdateCheckServiceInstalled,
    bool IsAdrenalinInstalled);

public sealed record DriverRelease(
    string DisplayName,
    string VersionText,
    string ReleaseDateText,
    string FileSizeText,
    string DownloadUrl,
    string SupportUrl);

public sealed record InstallRequest(
    HardwareInfo Hardware,
    DriverRelease? Driver,
    string SupportUrl,
    bool InstallDisplayDriver,
    bool EnableServerCompatibility,
    bool InstallAdrenalin,
    bool ReplaceAdrenalin,
    bool InstallBundledAudio);

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string? CurrentVersion,
    DriverRelease? LatestDriver,
    string Message);

[SupportedOSPlatform("windows")]
public sealed class DriverWorkflow : IDisposable
{
    private static readonly Regex DriverUrlRegex = new(@"https://drivers\.amd\.com/drivers/[^""'<>\s\\]+?\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);
    private readonly HttpClient _http = new();

    public string WorkRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RadeonPatcher");
    private string ToolsRoot => Path.Combine(WorkRoot, "tools");
    private string AudioPayloadRoot => Path.Combine(WorkRoot, "bundled-audio");
    private string DownloadsRoot => Path.Combine(WorkRoot, "downloads");
    private string ExtractedRoot => Path.Combine(WorkRoot, "extracted");
    private string PatchedRoot => Path.Combine(WorkRoot, "patched");

    public DriverWorkflow()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 RadeonPatcher/1.0");
    }

    public async Task EnsurePayloadsAsync()
    {
        Directory.CreateDirectory(ToolsRoot);
        Directory.CreateDirectory(AudioPayloadRoot);
        Directory.CreateDirectory(DownloadsRoot);
        Directory.CreateDirectory(ExtractedRoot);
        Directory.CreateDirectory(PatchedRoot);

        await ExtractEmbeddedFolderAsync("Tools\\", ToolsRoot);
        await ExtractEmbeddedFolderAsync("BundledAudio\\", AudioPayloadRoot);
    }

    public async Task<HardwareInfo> GetHardwareInfoAsync()
    {
        var display = await RunPowerShellAsync("""
            $gpu = Get-CimInstance Win32_PnPEntity |
              Where-Object {
                $_.PNPDeviceID -match 'VEN_1002' -and
                (
                  $_.PNPClass -eq 'Display' -or
                  $_.Name -match 'Radeon|AMD|Microsoft Basic Display|Display|Video' -or
                  $_.Service -match 'BasicDisplay|amdwddmg|amdkmdag'
                )
              } |
              Sort-Object @{ Expression = { if ($_.PNPClass -eq 'Display') { 0 } else { 1 } } }, Name |
              Select-Object -First 1
            $drv = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceClass -eq 'DISPLAY' -and ($_.DeviceID -match 'VEN_1002' -or $_.DeviceName -match 'AMD|Radeon') } | Select-Object -First 1
            $aud = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceClass -eq 'MEDIA' -and ($_.DeviceID -match 'HDAUDIO\\FUNC_01&VEN_1002&DEV_AA01' -or $_.DeviceName -match 'AMD High Definition Audio') } | Select-Object -First 1
            $os = Get-CimInstance Win32_OperatingSystem
            $windowsVersion = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
            $osName = ($os.Caption -replace '^Microsoft\s+', '')
            $osBuild = if ($windowsVersion.CurrentBuildNumber -and $null -ne $windowsVersion.UBR) { "$($windowsVersion.CurrentBuildNumber).$($windowsVersion.UBR)" } else { $os.Version }
            $mpoDisabled = $false
            try { $mpoDisabled = ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm' -ErrorAction SilentlyContinue).OverlayTestMode -eq 5) } catch {}
            $updateCheckInstalled = $null -ne (Get-ScheduledTask -TaskName 'RadeonPatcher Update Check' -ErrorAction SilentlyContinue)
            $adrenalinInstalled = $null -ne (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
              Where-Object { $_.DisplayName -match '^(AMD Software|AMD Settings)(?:: Adrenalin Edition)?$' } |
              Select-Object -First 1)
            $packageVersion = $null
            if ($drv) {
              $graphicsUpdate = Get-ItemProperty 'HKLM:\SOFTWARE\AMD\AMDInstallManager\CheckForUpdates\GraphicsDriverConsumer' -ErrorAction SilentlyContinue
              $activeBuild = [regex]::Match([string]$gpu.Service, 'amduw\d+g-(?<build>\d+)-').Groups['build'].Value
              $registryBuild = [regex]::Match([string]$graphicsUpdate.InternalVersion, '-(?<build>\d+)C-').Groups['build'].Value
              if ($activeBuild -and $activeBuild -eq $registryBuild -and $graphicsUpdate.Version -match '^\d+\.\d+\.\d+$') {
                $packageVersion = $graphicsUpdate.Version
              } elseif ($graphicsUpdate.VersionCurrentlyInstalled -match '^\d+\.\d+\.\d+$') {
                $packageVersion = $graphicsUpdate.VersionCurrentlyInstalled
              }
            }
            foreach ($manifest in @('C:\AMD\AMD-Software-Installer\Config\InstallManifest.json','C:\AMD\AMD-Software-Installer\Bin64\InstallManifest.json')) {
              if ($packageVersion) { break }
              if (Test-Path -LiteralPath $manifest) {
                try {
                  $json = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
                  $packageVersion = $json.BuildInfo.RadeonSoftwareVersion
                  if (-not $packageVersion) { $packageVersion = $json.BuildInfo.ExternalVersion }
                  if ($packageVersion) { break }
                } catch {}
              }
            }
            if (-not $packageVersion) {
              $latestLog = Get-ChildItem 'C:\Program Files\AMD\CIM\Log','C:\Program Files\AMD\AMDInstallManager\Logs' -Filter '*.log' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 20
              foreach ($log in $latestLog) {
                try {
                  $text = Get-Content -LiteralPath $log.FullName -Raw -ErrorAction Stop
                  if ($text -match 'driver Version\s+(\d+\.\d+\.\d+)\s+-') {
                    $packageVersion = $Matches[1]
                    break
                  }
                } catch {}
              }
            }
            [pscustomobject]@{
              GpuName=$gpu.Name
              GpuInstanceId=$gpu.PNPDeviceID
              DisplayDriverVersion=$drv.DriverVersion
              DisplayDriverPackageVersion=$packageVersion
              AudioDriverVersion=$aud.DriverVersion
              OsName=$osName
              OsVersion=$osBuild
              IsServer=($os.ProductType -ne 1)
              IsMpoDisabled=$mpoDisabled
              IsUpdateCheckServiceInstalled=$updateCheckInstalled
              IsAdrenalinInstalled=$adrenalinInstalled
            } | ConvertTo-Json -Compress
            """);
        return SimpleJson.ParseHardware(display.Trim());
    }

    public string? ResolveSupportUrl(HardwareInfo hardware)
    {
        var name = (hardware.GpuName ?? "").ToLowerInvariant();
        var match = Regex.Match(name, @"rx\s*(?<num>\d{4})\s*(?<suffix>xtx|xt|gre)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var num = match.Groups["num"].Value;
        var suffix = match.Groups["suffix"].Success ? "-" + match.Groups["suffix"].Value.ToLowerInvariant() : "";
        var series = num[0] switch
        {
            '9' => "radeon-rx-9000-series",
            '7' => "radeon-rx-7000-series",
            '6' => "radeon-rx-6000-series",
            '5' => "radeon-rx-5000-series",
            _ => "radeon-rx-series"
        };
        return $"https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/{series}/amd-radeon-rx-{num}{suffix}.html";
    }

    public async Task<IReadOnlyList<DriverRelease>> GetAvailableDriversAsync(string supportUrl, Action<string> log)
    {
        var urls = new List<string> { supportUrl };
        if (supportUrl.Contains("/drivers.html/", StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(supportUrl.Replace("/drivers.html/", "/previous-drivers.html/", StringComparison.OrdinalIgnoreCase));
        }

        var releases = new Dictionary<string, DriverRelease>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            log($"Fetching {url}");
            var html = await _http.GetStringAsync(url);
            foreach (Match match in DriverUrlRegex.Matches(html.Replace("\\u002F", "/")))
            {
                var download = match.Value;
                if (!download.Contains("amd-software-adrenalin", StringComparison.OrdinalIgnoreCase) &&
                    !download.Contains("radeon-software-adrenalin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!download.Contains("win11", StringComparison.OrdinalIgnoreCase) &&
                    !download.Contains("win10", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                releases.TryAdd(download, DescribeRelease(download, html, url));
            }
        }

        return releases.Values
            .OrderByDescending(r => VersionKey(r.VersionText))
            .ThenByDescending(r => r.ReleaseDateText)
            .ToList();
    }

    public async Task InstallAsync(InstallRequest request, Action<string> log)
    {
        await EnsurePayloadsAsync();
        if (request.Driver is not null)
        {
            var packageExe = await DownloadDriverAsync(request.Driver, false, log);
            var packageRoot = await ExtractPackageAsync(packageExe, log);
            if (request.InstallDisplayDriver)
            {
                await InstallDisplayDriverAsync(packageRoot, request, log);
            }
            if (request.InstallAdrenalin)
            {
                await InstallAdrenalinAsync(packageRoot, request.ReplaceAdrenalin, log);
            }
        }

        if (request.InstallBundledAudio)
        {
            await InstallBundledAudioAsync(request.EnableServerCompatibility, log);
        }

    }

    public async Task<bool> ToggleUpdateCheckServiceAsync(bool installed, Action<string> log)
    {
        if (installed)
        {
            await UninstallUpdateCheckServiceAsync(log);
            return false;
        }

        await InstallUpdateCheckServiceAsync(log);
        return true;
    }

    public async Task RemoveAdrenalinAsync(Action<string> log)
    {
        var uninstall = FindAdrenalinUninstallCommand();
        if (uninstall is null)
        {
            throw new InvalidOperationException("AMD Software: Adrenalin Edition uninstall command was not found.");
        }

        log("Removing AMD Software: Adrenalin Edition.");
        await RunProcessAsync(uninstall.Value.FileName, uninstall.Value.Arguments, log);
        log("AMD Software: Adrenalin Edition removed.");
    }

    private static (string FileName, string Arguments)? FindAdrenalinUninstallCommand()
    {
        const string uninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        (string FileName, string Arguments)? fallback = null;
        foreach (var root in new[] { uninstallRoot, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName") as string;
                if (displayName is null || !Regex.IsMatch(displayName, @"^(AMD Software|AMD Settings)(?:: Adrenalin Edition)?$", RegexOptions.IgnoreCase)) continue;

                if (subKey?.GetValue("WindowsInstaller") is 1 && Regex.IsMatch(subKeyName, @"^\{[0-9A-F-]+\}$", RegexOptions.IgnoreCase))
                {
                    return ("msiexec.exe", $"/x {subKeyName} /qn /norestart");
                }

                var uninstallCommand = subKey?.GetValue("UninstallString") as string;
                var match = Regex.Match(uninstallCommand?.Trim() ?? "", "^\\\"(?<file>[^\\\"]+)\\\"\\s*(?<args>.*)$|^(?<file>.+?\\.exe)(?:\\s+(?<args>.*))?$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    fallback ??= (match.Groups["file"].Value, match.Groups["args"].Value);
                }
            }
        }

        return fallback;
    }

    public Task<string> SetMpoOverrideAsync(bool disable, Action<string> log)
    {
        SetMpoState(disable);
        var message = disable
            ? "MPO override set. Restart or sign out/in for it to apply cleanly."
            : "MPO override removed. Restart or sign out/in for it to apply cleanly.";
        log(message);
        return Task.FromResult(message);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(Action<string> log)
    {
        var hardware = await GetHardwareInfoAsync();
        var supportUrl = ResolveSupportUrl(hardware);
        if (string.IsNullOrWhiteSpace(supportUrl))
        {
            return new UpdateCheckResult(false, hardware.DisplayDriverPackageVersion, null, "No mapped AMD support page was found for this GPU.");
        }

        var releases = await GetAvailableDriversAsync(supportUrl, log);
        var latest = releases.FirstOrDefault();
        if (latest is null)
        {
            return new UpdateCheckResult(false, hardware.DisplayDriverPackageVersion, null, "No AMD driver releases were found.");
        }

        var currentVersion = hardware.DisplayDriverPackageVersion ?? hardware.DisplayDriverVersion;
        var updateAvailable = VersionKey(latest.VersionText) > VersionKey(currentVersion ?? "");
        var message = updateAvailable
            ? $"AMD driver {latest.VersionText} is available. Installed: {currentVersion ?? "unknown"}."
            : $"AMD driver is current. Installed: {currentVersion ?? "unknown"}. Latest: {latest.VersionText}.";
        return new UpdateCheckResult(updateAvailable, currentVersion, latest, message);
    }

    private async Task InstallUpdateCheckServiceAsync(Action<string> log)
    {
        Directory.CreateDirectory(WorkRoot);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the current executable path.");
        var serviceExe = Path.Combine(WorkRoot, "RadeonPatcherUpdateCheck.exe");
        File.Copy(processPath, serviceExe, true);

        var startTime = DateTime.Now.AddMinutes(10).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $taskName = 'RadeonPatcher Update Check'
            $exe = '{{serviceExe.Replace("'", "''")}}'
            $action = New-ScheduledTaskAction -Execute $exe -Argument '--check-updates'
            $triggers = @()
            $triggers += New-ScheduledTaskTrigger -AtStartup
            $triggers += New-ScheduledTaskTrigger -Daily -At '{{startTime}}'
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 15)
            $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Settings $settings -Principal $principal -Force | Out-Null
            """;
        log("Installing update check scheduled task.");
        await RunPowerShellAsync(script);
        log("Update check service installed. It will run at Windows boot and once every 24 hours.");
    }

    private async Task UninstallUpdateCheckServiceAsync(Action<string> log)
    {
        const string taskName = "RadeonPatcher Update Check";
        log("Removing update check service.");
        await RunPowerShellAsync($"Unregister-ScheduledTask -TaskName '{taskName}' -Confirm:$false -ErrorAction SilentlyContinue");
        var serviceExe = Path.Combine(WorkRoot, "RadeonPatcherUpdateCheck.exe");
        if (File.Exists(serviceExe))
        {
            File.Delete(serviceExe);
        }

        log("Update check service removed.");
    }

    private async Task<string> DownloadDriverAsync(DriverRelease driver, bool force, Action<string> log)
    {
        Directory.CreateDirectory(DownloadsRoot);
        var destination = Path.Combine(DownloadsRoot, Path.GetFileName(new Uri(driver.DownloadUrl).AbsolutePath));
        if (File.Exists(destination) && !force && new FileInfo(destination).Length > 50 * 1024 * 1024)
        {
            log($"Using existing download: {destination}");
            return destination;
        }

        log($"Downloading {driver.DownloadUrl}");
        using var request = new HttpRequestMessage(HttpMethod.Get, driver.DownloadUrl);
        request.Headers.Referrer = new Uri(driver.SupportUrl);
        request.Headers.Accept.ParseAdd("application/octet-stream,*/*");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target);

        if (new FileInfo(destination).Length < 50 * 1024 * 1024)
        {
            throw new InvalidOperationException($"Downloaded file is too small to be an AMD driver package: {destination}");
        }

        return destination;
    }

    private async Task<string> ExtractPackageAsync(string packageExe, Action<string> log)
    {
        var destination = Path.Combine(ExtractedRoot, Path.GetFileNameWithoutExtension(packageExe));
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, true);
        }
        Directory.CreateDirectory(destination);

        log($"Extracting package to {destination}");
        await RunProcessAsync(Path.Combine(ToolsRoot, "7z.exe"), $"x \"{packageExe}\" -o\"{destination}\" -y", log);
        return destination;
    }

    private async Task InstallDisplayDriverAsync(string packageRoot, InstallRequest request, Action<string> log)
    {
        var displayInf = FindDisplayInf(packageRoot);
        if (!request.EnableServerCompatibility)
        {
            log($"Installing display driver INF: {displayInf}");
            await StageAndForceInstallDisplayDriverAsync(displayInf, request.Hardware.GpuInstanceId, log);
            return;
        }

        var patchDirectory = CopyPackageDirectory(displayInf, "display");
        var patchedInf = Path.Combine(patchDirectory, Path.GetFileName(displayInf));
        PatchDisplayInf(patchedInf, request.Hardware.GpuInstanceId, log);
        await SignPackageAsync(patchDirectory, patchedInf, log);
        log($"Installing patched display driver INF: {patchedInf}");
        await StageAndForceInstallDisplayDriverAsync(patchedInf, request.Hardware.GpuInstanceId, log);
    }

    private static async Task StageAndForceInstallDisplayDriverAsync(string infPath, string? instanceId, Action<string> log)
    {
        await RunProcessAsync("pnputil.exe", $"/add-driver \"{infPath}\"", log);

        var activeInf = await GetActiveDisplayDriverInfNameAsync(instanceId);
        if (!string.IsNullOrWhiteSpace(activeInf))
        {
            log($"Removing active display driver package: {activeInf}");
            await RunProcessAsync("pnputil.exe", $"/delete-driver {activeInf} /uninstall /force", log);
        }

        var hardwareId = ExtractExactHardwareId(instanceId);
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            throw new InvalidOperationException("Could not determine the exact GPU hardware ID for forced display driver installation.");
        }

        log($"Forcing display driver selection for {hardwareId}.");
        var installed = UpdateDriverForPlugAndPlayDevices(
            IntPtr.Zero,
            hardwareId,
            Path.GetFullPath(infPath),
            InstallFlagForce | InstallFlagNonInteractive,
            out var rebootRequired);
        if (!installed)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Forced display driver installation failed.");
        }

        log(rebootRequired
            ? "Display driver installed. Windows reports a reboot is required."
            : "Display driver installed.");
    }

    private static async Task<string?> GetActiveDisplayDriverInfNameAsync(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var escapedInstanceId = instanceId.Replace("'", "''", StringComparison.Ordinal);
        var output = await RunPowerShellAsync($$"""
            $driver = Get-CimInstance Win32_PnPSignedDriver |
              Where-Object { $_.DeviceClass -eq 'DISPLAY' -and $_.DeviceID -eq '{{escapedInstanceId}}' } |
              Sort-Object DriverDate -Descending |
              Select-Object -First 1
            if ($driver -and $driver.InfName -match '^oem\d+\.inf$') {
              [Console]::Out.WriteLine($driver.InfName)
            }
            """);
        var match = Regex.Match(output, @"\boem\d+\.inf\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    private async Task InstallBundledAudioAsync(bool serverCompatibility, Action<string> log)
    {
        var sourceInf = Path.Combine(AudioPayloadRoot, "AtihdWT6.inf");
        if (!File.Exists(sourceInf))
        {
            throw new FileNotFoundException("Bundled AMD HD Audio INF was not extracted.", sourceInf);
        }

        var package = CopyPackageDirectory(sourceInf, "audio");
        var inf = Path.Combine(package, "AtihdWT6.inf");
        if (serverCompatibility)
        {
            PatchGenericAmdInf(inf, log);
            await SignPackageAsync(package, inf, log);
        }

        log($"Installing bundled AMD HD Audio driver: {inf}");
        await RunProcessAsync("pnputil.exe", $"/add-driver \"{inf}\" /install", log, allowNoUpdate: true);
    }

    private async Task InstallAdrenalinAsync(string packageRoot, bool replaceExisting, Action<string> log)
    {
        if (replaceExisting && FindAdrenalinUninstallCommand() is not null)
        {
            log("Removing existing AMD Software before installing the selected driver version.");
            await RemoveAdrenalinAsync(log);
        }

        var installer = Directory.EnumerateFiles(packageRoot, "ccc2_install.exe", SearchOption.AllDirectories)
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
        if (installer is null)
        {
            log("Adrenalin installer ccc2_install.exe was not found in the package.");
            return;
        }

        var temp = Path.Combine(ExtractedRoot, "ccc2-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(temp);
        log($"Extracting Adrenalin component installer: {installer}");
        var exit = await RunProcessAsync(Path.Combine(ToolsRoot, "7z.exe"), $"x \"{installer}\" -o\"{temp}\" -y", log, throwOnError: false);
        var msi = Directory.EnumerateFiles(temp, "ccc-next64.msi", SearchOption.AllDirectories).FirstOrDefault();
        if (exit == 0 && msi is not null)
        {
            log($"Installing Adrenalin MSI: {msi}");
            await RunProcessAsync("msiexec.exe", $"/i \"{msi}\" /qn /norestart", log);
            return;
        }

        log("Falling back to direct ccc2_install.exe execution.");
        await RunProcessAsync(installer, "", log, throwOnError: false);
    }

    private async Task SignPackageAsync(string packageDirectory, string infPath, Action<string> log)
    {
        var certThumbprint = await EnsureCodeSigningCertificateAsync(log);
        var catalogName = GetCatalogNameFromInf(infPath);
        var cdfPath = NewCatalogDefinition(packageDirectory, catalogName);

        log($"Building catalog: {catalogName}");
        await RunProcessAsync(Path.Combine(ToolsRoot, "makecat.exe"), $"-v \"{cdfPath}\"", log, workingDirectory: packageDirectory);

        var catalogPath = Path.Combine(packageDirectory, catalogName);
        log("Signing catalog.");
        await RunProcessAsync(Path.Combine(ToolsRoot, "signtool.exe"), $"sign /v /fd SHA256 /sm /s My /sha1 {certThumbprint} \"{catalogPath}\"", log);

        log("Verifying catalog signature.");
        await RunProcessAsync(Path.Combine(ToolsRoot, "signtool.exe"), $"verify /pa /v /c \"{catalogPath}\" \"{infPath}\"", log);
    }

    private static string FindDisplayInf(string packageRoot)
    {
        return Directory.EnumerateFiles(packageRoot, "*.inf", SearchOption.AllDirectories)
            .Select(p => new { Path = p, Text = File.ReadAllText(p), Length = new FileInfo(p).Length })
            .Where(x => x.Text.Contains("Class=Display", StringComparison.OrdinalIgnoreCase) && x.Text.Contains(@"PCI\VEN_1002", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Length)
            .Select(x => x.Path)
            .FirstOrDefault() ?? throw new InvalidOperationException($"No AMD display INF found under {packageRoot}.");
    }

    private string CopyPackageDirectory(string infPath, string label)
    {
        var source = Path.GetDirectoryName(infPath)!;
        var destination = Path.Combine(PatchedRoot, $"patched-{label}-{DateTime.Now:yyyyMMdd-HHmmss}");
        CopyDirectory(source, destination);
        return destination;
    }

    private static void PatchDisplayInf(string infPath, string? instanceId, Action<string> log)
    {
        var lines = File.ReadAllLines(infPath).ToList();
        var changed = AddCompatibleManufacturerSections(lines, log);
        var exactHardwareId = ExtractExactHardwareId(instanceId);
        if (!string.IsNullOrWhiteSpace(exactHardwareId) && !string.Join('\n', lines).Contains(exactHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            var compatibleId = Regex.Match(exactHardwareId, @"PCI\\VEN_1002&DEV_[^&\\]+", RegexOptions.IgnoreCase).Value;
            var templateIndex = lines.FindIndex(l => l.Contains(compatibleId, StringComparison.OrdinalIgnoreCase) && l.TrimStart().StartsWith("\"%"));
            if (templateIndex >= 0 && Regex.Match(lines[templateIndex], "^\\s*\"(?<name>%[^%]+%)\"\\s*=\\s*(?<section>[^,]+),").Success is true)
            {
                var match = Regex.Match(lines[templateIndex], "^\\s*\"(?<name>%[^%]+%)\"\\s*=\\s*(?<section>[^,]+),");
                lines.Insert(templateIndex + 1, $"\"{match.Groups["name"].Value}\" = {match.Groups["section"].Value.Trim()}, {exactHardwareId}");
                changed = true;
                log($"Added exact hardware ID: {exactHardwareId}");
            }
        }

        if (changed)
        {
            File.Copy(infPath, infPath + ".bak", true);
            File.WriteAllLines(infPath, lines, Encoding.ASCII);
        }
    }

    private static void PatchGenericAmdInf(string infPath, Action<string> log)
    {
        var lines = File.ReadAllLines(infPath).ToList();
        if (AddCompatibleManufacturerSections(lines, log))
        {
            File.Copy(infPath, infPath + ".bak", true);
            File.WriteAllLines(infPath, lines, Encoding.ASCII);
        }
    }

    private static bool AddCompatibleManufacturerSections(List<string> lines, Action<string> log)
    {
        var manufacturerIndex = lines.FindIndex(l => Regex.IsMatch(l, @"^\[Manufacturer\]", RegexOptions.IgnoreCase));
        if (manufacturerIndex < 0)
        {
            throw new InvalidOperationException("INF does not contain a [Manufacturer] section.");
        }

        var changed = false;
        var models = new List<string>();
        for (var i = manufacturerIndex + 1; i < lines.Count && !lines[i].StartsWith("[", StringComparison.Ordinal); i++)
        {
            var match = Regex.Match(lines[i], @"^\s*%[^%]+%\s*=\s*(?<model>[^,]+)\s*,\s*(?<decorations>.+)$");
            if (!match.Success) continue;
            var model = match.Groups["model"].Value.Trim();
            models.Add(model);
            var decorations = match.Groups["decorations"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var decoration in new[] { "NTamd64.10.0", "NTamd64.10.0.3", "NTamd64.6.3.3", "NTamd64.6.2.3" })
            {
                if (!decorations.Contains(decoration, StringComparer.OrdinalIgnoreCase))
                {
                    decorations.Add(decoration);
                    changed = true;
                }
            }
            lines[i] = $"{lines[i].Split('=', 2)[0].Trim()}={model},{string.Join(',', decorations)}";
        }

        var source = FindPopulatedSection(lines, models);
        if (source is null)
        {
            log("Could not find a populated manufacturer section to clone.");
            return changed;
        }

        foreach (var section in new[] { $"{source.Value.Model}.NTamd64.10.0", $"{source.Value.Model}.NTamd64.10.0.3", $"{source.Value.Model}.NTamd64.6.3.3", $"{source.Value.Model}.NTamd64.6.2.3" })
        {
            var range = SectionRange(lines, section);
            if (range is null)
            {
                lines.Add("");
                lines.Add($"[{section}]");
                lines.AddRange(source.Value.Body);
                changed = true;
                log($"Added section [{section}].");
                continue;
            }

            var hasDevices = lines.Skip(range.Value.Start + 1).Take(range.Value.End - range.Value.Start - 1)
                .Any(l => l.Contains(@"PCI\VEN_1002", StringComparison.OrdinalIgnoreCase) || l.Contains(@"HDAUDIO\FUNC_01&VEN_1002", StringComparison.OrdinalIgnoreCase));
            if (!hasDevices)
            {
                lines.InsertRange(range.Value.Start + 1, source.Value.Body);
                changed = true;
                log($"Populated existing section [{section}].");
            }
        }

        return changed;
    }

    private static (string Model, List<string> Body)? FindPopulatedSection(List<string> lines, List<string> models)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var match = Regex.Match(lines[i], @"^\[(?<model>.+)\.NTamd64.*\]$", RegexOptions.IgnoreCase);
            if (!match.Success || lines[i].Contains(".Services]", StringComparison.OrdinalIgnoreCase)) continue;
            var model = match.Groups["model"].Value;
            if (!models.Contains(model, StringComparer.OrdinalIgnoreCase)) continue;
            var end = NextSectionIndex(lines, i + 1);
            var body = lines.Skip(i + 1).Take(end - i - 1).ToList();
            if (body.Any(l => l.Contains(@"PCI\VEN_1002", StringComparison.OrdinalIgnoreCase) || l.Contains(@"HDAUDIO\FUNC_01&VEN_1002", StringComparison.OrdinalIgnoreCase)))
            {
                return (model, body);
            }
        }
        return null;
    }

    private static (int Start, int End)? SectionRange(List<string> lines, string name)
    {
        var start = lines.FindIndex(l => string.Equals(l.Trim(), $"[{name}]", StringComparison.OrdinalIgnoreCase));
        return start < 0 ? null : (start, NextSectionIndex(lines, start + 1));
    }

    private static int NextSectionIndex(List<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("[", StringComparison.Ordinal)) return i;
        }
        return lines.Count;
    }

    private static string? ExtractExactHardwareId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var match = Regex.Match(instanceId, @"PCI\\VEN_1002&DEV_[^\\]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string GetCatalogNameFromInf(string infPath)
    {
        var line = File.ReadLines(infPath).FirstOrDefault(l => Regex.IsMatch(l, @"^\s*CatalogFile\s*=", RegexOptions.IgnoreCase));
        return line?.Split('=', 2)[1].Trim() ?? throw new InvalidOperationException("INF does not declare CatalogFile=.");
    }

    private static string NewCatalogDefinition(string packageDirectory, string catalogName)
    {
        var cdfPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(catalogName) + ".cdf");
        var catalogPath = Path.Combine(packageDirectory, catalogName);
        if (File.Exists(catalogPath)) File.Delete(catalogPath);
        var lines = new List<string>
        {
            "[CatalogHeader]",
            $"Name={catalogName}",
            @"ResultDir=.\",
            "PublicVersion=0x00000001",
            "CatalogVersion=2",
            "HashAlgorithms=SHA256",
            "EncodingType=0x00010001",
            "CATATTR1=0x10010001:OSAttr:2:10.0",
            "",
            "[CatalogFiles]"
        };

        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(file, catalogPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file, cdfPath, StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(packageDirectory, file);
            var tag = Regex.Replace(relative, @"[^A-Za-z0-9_.-]", "_");
            lines.Add($@"<HASH>{tag}=.\{relative}");
        }

        File.WriteAllLines(cdfPath, lines, Encoding.ASCII);
        return cdfPath;
    }

    private async Task<string> EnsureCodeSigningCertificateAsync(Action<string> log)
    {
        var script = """
            $ProgressPreference = 'SilentlyContinue'
            $subject = 'CN=AMD Driver Modding Authority'
            $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
              Where-Object { $_.Subject -eq $subject } |
              Sort-Object NotAfter -Descending |
              Select-Object -First 1
            if (-not $cert) {
              $cert = New-SelfSignedCertificate -Subject $subject -Type CodeSigningCert -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable -KeyUsage DigitalSignature -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
            }
            $cer = Join-Path $env:TEMP 'AMDDriverModdingAuthority.cer'
            Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
            foreach ($store in 'Cert:\LocalMachine\Root','Cert:\LocalMachine\TrustedPublisher','Cert:\CurrentUser\Root','Cert:\CurrentUser\TrustedPublisher') {
              Import-Certificate -FilePath $cer -CertStoreLocation $store | Out-Null
            }
            [Console]::Out.WriteLine($cert.Thumbprint)
            """;
        log("Ensuring local code-signing certificate is trusted.");
        var output = await RunPowerShellAsync(script);
        var thumbprint = Regex.Match(output, @"\b[0-9A-Fa-f]{40}\b").Value.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidOperationException($"Could not determine the code-signing certificate thumbprint.{Environment.NewLine}{output.Trim()}");
        }

        return thumbprint;
    }

    private static DriverRelease DescribeRelease(string downloadUrl, string html, string supportUrl)
    {
        var file = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
        var versionMatch = Regex.Match(file, @"(?:adrenalin-edition-|software-)(?<ver>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
        var version = versionMatch.Success ? versionMatch.Groups["ver"].Value : file;
        var contextIndex = html.IndexOf(downloadUrl, StringComparison.OrdinalIgnoreCase);
        var context = ExtractDriverArticle(html, contextIndex);
        var plain = System.Net.WebUtility.HtmlDecode(HtmlTagRegex.Replace(context, " "));
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        var date = Regex.Match(plain, @"Release Date\s*(?<date>\d{4}-\d{2}-\d{2}|[A-Za-z]+\s+\d{1,2},\s+\d{4}|\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase).Groups["date"].Value;
        if (DateOnly.TryParse(date, out var parsedDate))
        {
            date = parsedDate.ToString("yyyy-MM-dd");
        }

        var size = Regex.Match(plain, @"File Size\s*(?<size>\d+(?:\.\d+)?\s*(?:MB|GB))", RegexOptions.IgnoreCase).Groups["size"].Value;
        var label = file.Contains("whql", StringComparison.OrdinalIgnoreCase) ? "WHQL" : "AMD";
        return new DriverRelease($"{version} {label}", version, string.IsNullOrWhiteSpace(date) ? "Release Date Unknown" : date, string.IsNullOrWhiteSpace(size) ? "Size Unknown" : size, downloadUrl, supportUrl);
    }

    private static string ExtractDriverArticle(string html, int contextIndex)
    {
        if (contextIndex < 0)
        {
            return "";
        }

        var start = html.LastIndexOf("<article", contextIndex, StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf("</article>", contextIndex, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            return html.Substring(start, end - start + "</article>".Length);
        }

        start = Math.Max(0, contextIndex - 2500);
        return html.Substring(start, Math.Min(5000, html.Length - start));
    }

    private static Version VersionKey(string version)
    {
        return Version.TryParse(Regex.Match(version, @"\d+(\.\d+)+").Value, out var parsed) ? parsed : new Version(0, 0);
    }

    private static void SetMpoState(bool disable)
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Dwm");
        if (disable)
        {
            key.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("OverlayTestMode", false);
        }
    }

    private async Task ExtractEmbeddedFolderAsync(string prefix, string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(prefix.Replace('\\', '.')) || n.Contains(prefix)))
        {
            var logicalName = resource.Contains(prefix)
                ? resource[(resource.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..]
                : resource[(resource.IndexOf(prefix.Replace('\\', '.'), StringComparison.Ordinal) + prefix.Length)..].TrimStart('.');
            logicalName = logicalName.TrimStart('.', '\\').Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(logicalName)) continue;
            var target = Path.Combine(destination, logicalName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
            await using var output = File.Create(target);
            await source.CopyToAsync(output);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
        }
    }

    private static async Task<string> RunPowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var output = new StringBuilder();
        var exit = await RunProcessCaptureAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}", output);
        if (exit != 0) throw new InvalidOperationException(CleanPowerShellOutput(output.ToString()));
        return output.ToString();
    }

    private static string CleanPowerShellOutput(string output)
    {
        if (!output.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            return output;
        }

        var messages = Regex.Matches(output, @"<S S=""Error"">(?<message>.*?)</S>", RegexOptions.Singleline)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["message"].Value)
                .Replace("_x000D__x000A", Environment.NewLine, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return messages.Count == 0 ? output : string.Join("", messages);
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, Action<string> log, string? workingDirectory = null, bool throwOnError = true, bool allowNoUpdate = false)
    {
        var output = new StringBuilder();
        var exit = await RunProcessCaptureAsync(fileName, arguments, output, workingDirectory, log);
        var text = output.ToString();
        if (allowNoUpdate && exit == 259 && (text.Contains("up-to-date on device", StringComparison.OrdinalIgnoreCase) || text.Contains("Added driver packages:  0", StringComparison.OrdinalIgnoreCase)))
        {
            return exit;
        }

        if (throwOnError && exit != 0)
        {
            var command = string.IsNullOrWhiteSpace(arguments)
                ? fileName
                : $"{fileName} {arguments}";
            var details = string.IsNullOrWhiteSpace(text)
                ? "No output was captured."
                : text.Trim();
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed with exit code {exit}.{Environment.NewLine}{Environment.NewLine}Command:{Environment.NewLine}{command}{Environment.NewLine}{Environment.NewLine}Output:{Environment.NewLine}{details}");
        }

        return exit;
    }

    private static async Task<int> RunProcessCaptureAsync(string fileName, string arguments, StringBuilder output, string? workingDirectory = null, Action<string>? log = null)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); log?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); log?.Invoke(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private const uint InstallFlagForce = 0x00000001;
    private const uint InstallFlagNonInteractive = 0x00000004;

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        out bool rebootRequired);

    public void Dispose() => _http.Dispose();
}

internal static class SimpleJson
{
    public static HardwareInfo ParseHardware(string json)
    {
        static string? Value(string json, string name)
        {
            var match = Regex.Match(json, $"\"{name}\"\\s*:\\s*(?:\"(?<v>(?:\\\\.|[^\"])*)\"|(?<v>true|false|null))", RegexOptions.IgnoreCase);
            if (!match.Success || match.Groups["v"].Value == "null") return null;
            return Regex.Unescape(match.Groups["v"].Value);
        }

        return new HardwareInfo(
            Value(json, "GpuName"),
            Value(json, "GpuInstanceId"),
            Value(json, "DisplayDriverVersion"),
            Value(json, "DisplayDriverPackageVersion"),
            Value(json, "AudioDriverVersion"),
            Value(json, "OsName") ?? "Windows",
            Value(json, "OsVersion") ?? "",
            string.Equals(Value(json, "IsServer"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(Value(json, "IsMpoDisabled"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(Value(json, "IsUpdateCheckServiceInstalled"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(Value(json, "IsAdrenalinInstalled"), "true", StringComparison.OrdinalIgnoreCase));
    }
}
