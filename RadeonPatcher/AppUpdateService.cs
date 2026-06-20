using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadeonPatcher;

public sealed record AppUpdateInfo(Version CurrentVersion, Version LatestVersion, string DownloadUrl, string ChecksumUrl);

public static class AppUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/iPixelGalaxy/RadeonPatcher/releases/latest";
    private const string UpstreamOwner = "iPixelGalaxy";
    private const string UpstreamRepository = "RadeonPatcher";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<AppUpdateInfo?> CheckAsync()
    {
        using var client = CreateClient();
        await using var stream = await client.GetStreamAsync(LatestReleaseUrl);
        var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(stream);
        if (release is null || !TryParseVersion(release.TagName, out var latest) || latest <= CurrentVersion)
        {
            return null;
        }

        var executable = release.Assets.FirstOrDefault(asset => asset.Name.Equals("RadeonPatcher.exe", StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.FirstOrDefault(asset => asset.Name.Equals("RadeonPatcher.exe.sha256", StringComparison.OrdinalIgnoreCase));
        if (executable is null || checksum is null ||
            !IsTrustedReleaseAssetUrl(executable.DownloadUrl) || !IsTrustedReleaseAssetUrl(checksum.DownloadUrl))
        {
            return null;
        }

        // The trust anchor is the upstream GitHub repository. Its checksum detects
        // accidental transport corruption; it does not independently authenticate a release.
        return new AppUpdateInfo(CurrentVersion, latest, executable.DownloadUrl, checksum.DownloadUrl);
    }

    public static async Task DownloadAndRestartAsync(AppUpdateInfo update)
    {
        var currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the running executable.");
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonPatcher", "updates");
        Directory.CreateDirectory(updateRoot);
        var downloadedExe = Path.Combine(updateRoot, $"RadeonPatcher-{update.LatestVersion}.exe");

        using var client = CreateClient();
        await using (var source = await client.GetStreamAsync(update.DownloadUrl))
        await using (var destination = File.Create(downloadedExe))
        {
            await source.CopyToAsync(destination);
        }

        var checksumText = await client.GetStringAsync(update.ChecksumUrl);
        var expectedHash = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        await using var downloadedStream = File.OpenRead(downloadedExe);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloadedStream));
        if (string.IsNullOrWhiteSpace(expectedHash) || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(downloadedExe);
            throw new InvalidOperationException("The downloaded update did not match the checksum published by GitHub Actions.");
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath '{{Escape(downloadedExe)}}' -Destination '{{Escape(currentExe)}}' -Force
            Remove-Item -LiteralPath '{{Escape(downloadedExe)}}' -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath '{{Escape(currentExe)}}'
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RadeonPatcher-AppUpdater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool TryParseVersion(string tag, out Version version) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V').Split('-')[0], out version!);

    internal static bool IsTrustedReleaseAssetUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith($"/{UpstreamOwner}/{UpstreamRepository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private sealed record ReleaseResponse(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] ReleaseAsset[] Assets);

    private sealed record ReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}
