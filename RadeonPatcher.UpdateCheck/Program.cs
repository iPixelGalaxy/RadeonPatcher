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
        var supportUrl = ResolveSupportUrl(hardware.GpuName)
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
        const string script = "$gpu = Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -match 'VEN_1002' -and $_.PNPClass -eq 'Display' } | Select-Object -First 1; [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$gpu.Name))); [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$gpu.PNPDeviceID)))";
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
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) throw new InvalidOperationException("No AMD display adapter was detected.");
        return new Hardware(Decode(lines[0]), Decode(lines[1]));
    }

    private static string? ResolveSupportUrl(string gpuName)
    {
        var match = Regex.Match(gpuName, @"rx\s*(?<num>\d{4})\s*(?<suffix>xtx|xt|gre)?", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
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

    private static async Task ShowNotificationAsync(string title, string message)
    {
        var applicationPath = ResolveLastApplicationPath();
        using var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Forms.Application.ExecutablePath),
            Visible = true,
            Text = "RadeonPatcher"
        };
        icon.BalloonTipClicked += (_, _) =>
        {
            if (applicationPath is null) return;
            try
            {
                Process.Start(new ProcessStartInfo(applicationPath) { UseShellExecute = true });
            }
            catch
            {
                // A stale or inaccessible application path should not crash the checker.
            }
        };
        icon.ShowBalloonTip(10000, title, message, Forms.ToolTipIcon.Info);
        await Task.Delay(11000);
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
    private sealed record Hardware(string GpuName, string InstanceId);
    private sealed record DriverReceipt(string GpuInstanceId, string OriginalInf, string PackageVersion, DateTimeOffset InstalledAt);
    private sealed record CheckerSettings(string? LastApplicationPath);
    private sealed record CheckResult(bool UpdateAvailable, string? CurrentVersion, string LatestVersion, string Message);
}
