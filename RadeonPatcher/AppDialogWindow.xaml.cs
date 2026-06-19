using System.Windows;

namespace RadeonPatcher;

public partial class AppDialogWindow : Window
{
    public bool Accepted { get; private set; }

    public AppDialogWindow(string title, string heading, string message, string primaryText, string? secondaryText = null)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText;
        SecondaryButton.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
