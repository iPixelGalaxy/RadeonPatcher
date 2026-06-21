using System.ComponentModel;
using System.Windows;

namespace RadeonPatcher;

public partial class AppUpdateWindow : Window
{
    private readonly AppUpdateInfo _update;
    private bool _downloading;
    public bool UpdateInstalled { get; private set; }

    public AppUpdateWindow(AppUpdateInfo update)
    {
        InitializeComponent();
        _update = update;
        CurrentVersionText.Text = Format(update.CurrentVersion);
        LatestVersionText.Text = Format(update.LatestVersion);
        if (update.ReleaseNotes.Count > 0)
        {
            ReleaseNotesText.Text = string.Join(Environment.NewLine, update.ReleaseNotes.Select(note => $"• {note}"));
            ReleaseNotesPanel.Visibility = Visibility.Visible;
        }
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _downloading = true;
        UpdateButton.IsEnabled = false;
        MaybeLaterButton.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            DownloadStatusText.Text = value.Status;
            DownloadSpeedText.Text = value.BytesPerSecond is null
                ? ""
                : $"{value.BytesPerSecond.Value / 1024 / 1024:F1} MB/s";
            DownloadProgress.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is not null) DownloadProgress.Value = value.Percentage.Value * 100;
        });

        try
        {
            await AppUpdateService.DownloadAndRestartAsync(_update, progress);
            UpdateInstalled = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppDialog.ShowError(this, "Update failed", $"RadeonPatcher could not install the update.\n\n{ex.Message}");
            _downloading = false;
            UpdateButton.IsEnabled = true;
            MaybeLaterButton.IsEnabled = true;
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_downloading && !UpdateInstalled) e.Cancel = true;
        base.OnClosing(e);
    }

    private static string Format(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";
}
