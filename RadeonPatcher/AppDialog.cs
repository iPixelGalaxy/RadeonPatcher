using System.Windows;

namespace RadeonPatcher;

public static class AppDialog
{
    public static bool Confirm(Window owner, string heading, string message, string acceptText = "Yes", string cancelText = "No")
    {
        var dialog = new AppDialogWindow("RadeonPatcher", heading, message, acceptText, cancelText) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Accepted;
    }

    public static void Show(Window owner, string heading, string message)
    {
        new AppDialogWindow("RadeonPatcher", heading, message, "OK") { Owner = owner }.ShowDialog();
    }

    public static void ShowError(Window owner, string heading, string message)
    {
        new AppDialogWindow("RadeonPatcher", heading, message, "OK") { Owner = owner }.ShowDialog();
    }
}
