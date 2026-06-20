using System.Windows;

namespace RadeonPatcher;

public partial class AppUpdateWindow : Window
{
    public bool UpdateRequested { get; private set; }

    public AppUpdateWindow(AppUpdateInfo update)
    {
        InitializeComponent();
        CurrentVersionText.Text = Format(update.CurrentVersion);
        LatestVersionText.Text = Format(update.LatestVersion);
        if (update.ReleaseNotes.Count > 0)
        {
            ReleaseNotesText.Text = string.Join(Environment.NewLine, update.ReleaseNotes.Select(note => $"• {note}"));
            ReleaseNotesPanel.Visibility = Visibility.Visible;
        }
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested = true;
        DialogResult = true;
    }

    private static string Format(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";
}
