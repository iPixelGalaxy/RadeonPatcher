using System.Windows;
using Forms = System.Windows.Forms;

namespace RadeonPatcher;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--check-updates", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await RunUpdateCheckAsync();
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static async Task RunUpdateCheckAsync()
    {
        try
        {
            using var workflow = new DriverWorkflow();
            var result = await workflow.CheckForUpdatesAsync(_ => { });
            if (result.UpdateAvailable && result.LatestDriver is not null)
            {
                ShowNotification(
                    "AMD driver update available",
                    $"Version {result.LatestDriver.VersionText} is available. Installed: {result.CurrentVersion ?? "unknown"}.");
            }
        }
        catch
        {
            // Background checks should never interrupt logon or boot.
        }
    }

    private static void ShowNotification(string title, string message)
    {
        using var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Forms.Application.ExecutablePath),
            Visible = true,
            Text = "RadeonPatcher"
        };

        System.Media.SystemSounds.Exclamation.Play();
        icon.ShowBalloonTip(10000, title, message, Forms.ToolTipIcon.Info);
        Thread.Sleep(11000);
        icon.Visible = false;
    }
}
