using System.Runtime.InteropServices;

namespace RadeonPatcher;

internal static class DisplayTopology
{
    private const int DisplayDevicePrimaryDevice = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint index, ref DisplayDevice displayDevice, uint flags);

    public static string? GetPrimaryAdapterName()
    {
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) return null;
            if ((device.StateFlags & DisplayDevicePrimaryDevice) != 0 && !string.IsNullOrWhiteSpace(device.DeviceString)) return device.DeviceString.Trim();
        }
    }
}
