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

        var updateCheck = AppUpdateService.CheckAsync();
        MainWindow = new MainWindow();
        MainWindow.Show();
        await CheckForAppUpdateAsync(MainWindow, updateCheck);
    }

    private static async Task CheckForAppUpdateAsync(Window owner, Task<AppUpdateInfo?> updateCheck)
    {
        AppUpdateInfo? update;
        try
        {
            update = await updateCheck;
        }
        catch
        {
            return;
        }

        if (update is null)
        {
            return;
        }

        var prompt = new AppUpdateWindow(update) { Owner = owner };
        prompt.ShowDialog();
        if (prompt.UpdateInstalled)
        {
            Current.Shutdown();
        }
    }

    private static async Task RunUpdateCheckAsync()
    {
        try
        {
            using var workflow = new DriverWorkflow();
            var result = await workflow.CheckForUpdatesAsync(_ => { });
            if (result.UpdateAvailable && result.LatestDriver is not null)
            {
                await ShowNotification(
                    "AMD driver update available",
                    $"Version {result.LatestDriver.VersionText} is available. Installed: {result.CurrentVersion ?? "unknown"}.");
            }
        }
        catch
        {
            // Background checks should never interrupt logon or boot.
        }
    }

    private static async Task ShowNotification(string title, string message)
    {
        using var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Forms.Application.ExecutablePath),
            Visible = true,
            Text = "RadeonPatcher"
        };

        System.Media.SystemSounds.Exclamation.Play();
        icon.ShowBalloonTip(10000, title, message, Forms.ToolTipIcon.Info);
        await Task.Delay(11000);
        icon.Visible = false;
    }
}
