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
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested = true;
        DialogResult = true;
    }

    private static string Format(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";
}
