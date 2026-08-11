using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RadeonPatcher;

internal static class DialogTheme
{
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var background = System.Windows.Application.Current.Resources["WindowBackgroundBrush"] as SolidColorBrush;
        var dark = background?.Color.R < 100;
        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));

        if (dark)
        {
            var captionColor = ColorRef(0x2F, 0x2B, 0x2E);
            var textColor = ColorRef(0xF1, 0xEE, 0xF0);
            _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
        }
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
