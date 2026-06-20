using System.Windows;
using System.Windows.Input;

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

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            System.Windows.Clipboard.SetText($"{HeadingText.Text}{Environment.NewLine}{Environment.NewLine}{MessageText.Text}");
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
