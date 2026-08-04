using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forms = System.Windows.Forms;

internal static class Program
{
    private static readonly Regex DriverUrlRegex = new(@"https://drivers\.amd\.com/drivers/[^\""'<>\s\\]+?\.exe", RegexOptions.IgnoreCase);

    [STAThread]
    private static async Task Main(string[] args)
    {
        var scheduled = args.Any(arg => arg.Equals("--scheduled", StringComparison.OrdinalIgnoreCase));
        try
        {
            var result = await CheckAsync();
            if (result.UpdateAvailable)
            {
                await ShowNotificationAsync("AMD driver update available", $"Version {result.LatestVersion} is available. Installed: {result.CurrentVersion ?? "unknown"}.");
            }
            else if (!scheduled)
            {
                await ShowNotificationAsync("AMD driver update check", result.Message);
            }
        }
        catch (Exception ex)
        {
            if (!scheduled)
            {
                await ShowNotificationAsync("AMD driver update check failed", ex.Message);
            }
        }
    }

    private static async Task<CheckResult> CheckAsync()
    {
        var hardware = await DetectHardwareAsync();
        var supportUrl = ResolveSupportUrl(hardware.GpuName, hardware.CpuName)
            ?? throw new InvalidOperationException("No mapped AMD support page was found for this GPU.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 RadeonPatcher-UpdateCheck");
        var html = await client.GetStringAsync(supportUrl);
        var latest = DriverUrlRegex.Matches(html.Replace("\\u002F", "/"))
            .Select(match => Regex.Match(Path.GetFileName(new Uri(match.Value).AbsolutePath), @"(?:adrenalin-edition-|software-)(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase).Groups["version"].Value)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => Version.TryParse(version, out var parsed) ? parsed : new Version())
            .OrderDescending()
            .FirstOrDefault() ?? throw new InvalidOperationException("No AMD driver releases were found.");
        var currentText = ResolveInstalledPackageVersion(hardware.InstanceId);
        var updateAvailable = !Version.TryParse(currentText, out var current) || latest > current;
        return new CheckResult(updateAvailable, currentText, latest.ToString(3), updateAvailable
            ? $"AMD driver {latest.ToString(3)} is available."
            : $"AMD driver is current. Installed: {currentText}. Latest: {latest.ToString(3)}.");
    }

    private static async Task<Hardware> DetectHardwareAsync()
    {
        var script = "$gpus = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -like 'PCI\\VEN_1002*' -and ($_.PNPClass -eq 'Display' -or $_.ClassGuid -eq '{4d36e968-e325-11ce-bfc1-08002be10318}' -or $_.Service -match 'BasicDisplay|amdwddmg|amdkmdag') } | ForEach-Object { [pscustomobject]@{ Name=$_.Name; InstanceId=$_.PNPDeviceID } }); $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1; $entries = @($gpus) + @([pscustomobject]@{ CpuName=$cpu.Name }); [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($entries | ConvertTo-Json -Compress))))"
            .Replace("CPU_GRAPHICS_DEVICE_IDS", RadeonPatcher.AmdCpuGraphicsHardware.DeviceIdAlternation, StringComparison.Ordinal);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start hardware detection.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        var encodedResult = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encodedResult)) throw new InvalidOperationException("No AMD display adapter was detected.");
        var entries = JsonSerializer.Deserialize<List<HardwareEntry>>(Decode(encodedResult)) ?? [];
        var cpuName = entries.LastOrDefault()?.CpuName ?? "";
        var adapters = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.InstanceId)).ToList();
        var primaryName = RadeonPatcher.DisplayTopology.GetPrimaryAdapterName();
        var primaryGpu = adapters.FirstOrDefault(adapter => string.Equals(adapter.Name, primaryName, StringComparison.OrdinalIgnoreCase) && !IsCpuGraphics(adapter.InstanceId));
        var cpuGraphics = adapters.FirstOrDefault(adapter => IsCpuGraphics(adapter.InstanceId));
        var gpu = primaryGpu ?? cpuGraphics ?? adapters.FirstOrDefault(adapter => !IsCpuGraphics(adapter.InstanceId));
        if (gpu is null) throw new InvalidOperationException("No AMD display adapter was detected.");
        return new Hardware(gpu.Name!, gpu.InstanceId!, cpuName);
    }

    private static string? ResolveSupportUrl(string gpuName, string cpuName)
    {
        var match = Regex.Match(gpuName, @"rx\s*(?<num>\d{4})\s*(?<suffix>xtx|xt|gre)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            var cpu = Regex.Match(cpuName, @"AMD\s+Ryzen\s+(?<tier>[3579])\s+(?<model>\d{4,5}(?:[A-Za-z0-9]+)?)", RegexOptions.IgnoreCase);
            if (!cpu.Success) return null;
            var model = cpu.Groups["model"].Value.ToLowerInvariant();
            var cpuSeries = model[0] switch { '9' => "ryzen-9000-series", '8' => "ryzen-8000-series", '7' => "ryzen-7000-series", '6' => "ryzen-6000-series", '5' => "ryzen-5000-series", '4' => "ryzen-4000-series", '3' => "ryzen-3000-series", '2' => "ryzen-2000-series", '1' => "ryzen-1000-series", _ => null };
            return cpuSeries is null ? null : $"https://www.amd.com/en/support/downloads/drivers.html/processors/ryzen/{cpuSeries}/amd-ryzen-{cpu.Groups["tier"].Value}-{model}.html";
        }
        var number = match.Groups["num"].Value;
        var suffix = match.Groups["suffix"].Success ? "-" + match.Groups["suffix"].Value.ToLowerInvariant() : "";
        var series = number[0] switch { '9' => "radeon-rx-9000-series", '7' => "radeon-rx-7000-series", '6' => "radeon-rx-6000-series", '5' => "radeon-rx-5000-series", _ => "radeon-rx-series" };
        return $"https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/{series}/amd-radeon-rx-{number}{suffix}.html";
    }

    private static string? ResolveInstalledPackageVersion(string instanceId)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonPatcher", "driver-receipts.json");
        if (!File.Exists(path)) return null;
        var receipts = JsonSerializer.Deserialize<List<DriverReceipt>>(File.ReadAllText(path)) ?? [];
        return receipts.Where(receipt => receipt.GpuInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(receipt => receipt.InstalledAt).FirstOrDefault()?.PackageVersion;
    }

    private static Task ShowNotificationAsync(string title, string message)
    {
        var applicationPath = ResolveLastApplicationPath();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new Forms.ApplicationContext();
                using var icon = new Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Forms.Application.ExecutablePath),
                    Visible = true,
                    Text = "RadeonPatcher"
                };
                using var timer = new Forms.Timer { Interval = 11000 };
                timer.Tick += (_, _) => context.ExitThread();
                icon.BalloonTipClicked += (_, _) =>
                {
                    if (applicationPath is not null)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(applicationPath) { UseShellExecute = true });
                        }
                        catch
                        {
                            // A stale or inaccessible application path should not crash the checker.
                        }
                    }
                    context.ExitThread();
                };
                icon.ShowBalloonTip(10000, title, message, Forms.ToolTipIcon.Info);
                timer.Start();
                Forms.Application.Run(context);
                icon.Visible = false;
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string? ResolveLastApplicationPath()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonPatcher", "settings.json");
            var configured = File.Exists(path)
                ? JsonSerializer.Deserialize<CheckerSettings>(File.ReadAllText(path))?.LastApplicationPath
                : null;
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
    private static bool IsCpuGraphics(string? instanceId) => Regex.IsMatch(instanceId ?? "", $@"DEV_({RadeonPatcher.AmdCpuGraphicsHardware.DeviceIdAlternation})", RegexOptions.IgnoreCase);
    private sealed record Hardware(string GpuName, string InstanceId, string CpuName);
    private sealed record HardwareEntry(string? Name, string? InstanceId, string? CpuName = null);
    private sealed record DriverReceipt(string GpuInstanceId, string OriginalInf, string PackageVersion, DateTimeOffset InstalledAt);
    private sealed record CheckerSettings(string? LastApplicationPath);
    private sealed record CheckResult(bool UpdateAvailable, string? CurrentVersion, string LatestVersion, string Message);
}
