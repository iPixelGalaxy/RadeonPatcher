using System.Windows;

namespace RadeonPatcher;

public partial class UpdateCheckServiceInstallDialog : Window
{
    public bool CheckOnBoot { get; private set; } = true;
    public TimeSpan CheckFrequency { get; private set; } = TimeSpan.FromHours(6);

    public UpdateCheckServiceInstallDialog(bool checkOnBoot = true, TimeSpan? checkFrequency = null)
    {
        InitializeComponent();
        CheckOnBoot = checkOnBoot;
        CheckFrequency = checkFrequency is { } frequency && frequency >= TimeSpan.FromHours(1)
            ? frequency
            : TimeSpan.FromHours(6);
        CheckOnBootBox.IsChecked = CheckOnBoot;
        var options = new[]
        {
            new UpdateCheckFrequencyOption("Every hour", TimeSpan.FromHours(1)),
            new UpdateCheckFrequencyOption("Every 6 hours", TimeSpan.FromHours(6)),
            new UpdateCheckFrequencyOption("Every 12 hours", TimeSpan.FromHours(12)),
            new UpdateCheckFrequencyOption("Every 24 hours", TimeSpan.FromHours(24)),
            new UpdateCheckFrequencyOption("Every 7 days", TimeSpan.FromDays(7))
        };
        FrequencyBox.ItemsSource = options;
        FrequencyBox.SelectedItem = options
            .OrderBy(option => Math.Abs((option.Interval - CheckFrequency).TotalMinutes))
            .First();
        SourceInitialized += (_, _) => DialogTheme.ApplyTitleBar(this);
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        CheckOnBoot = CheckOnBootBox.IsChecked == true;
        if (FrequencyBox.SelectedItem is UpdateCheckFrequencyOption option)
        {
            CheckFrequency = option.Interval;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed record UpdateCheckFrequencyOption(string Label, TimeSpan Interval)
    {
        public override string ToString() => Label;
    }
}
