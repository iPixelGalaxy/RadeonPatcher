using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RadeonPatcher;

public partial class AboutWindow : Window
{
    private const string GitHubProfileUrl = "https://api.github.com/users/iPixelGalaxy";

    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
        Loaded += async (_, _) => await LoadGitHubProfileAsync();
    }

    private async Task LoadGitHubProfileAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RadeonPatcher-About");
            using var profile = JsonDocument.Parse(await client.GetStringAsync(GitHubProfileUrl));
            UsernameRun.Text = profile.RootElement.GetProperty("login").GetString() ?? "iPixelGalaxy";
            var avatarUrl = profile.RootElement.GetProperty("avatar_url").GetString();
            if (string.IsNullOrWhiteSpace(avatarUrl)) return;

            var imageBytes = await client.GetByteArrayAsync(avatarUrl);
            using var stream = new MemoryStream(imageBytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            AvatarBrush.ImageSource = image;
        }
        catch
        {
            // The fallback username and empty avatar keep About usable offline.
        }
    }
}
