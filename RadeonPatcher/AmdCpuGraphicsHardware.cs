using System.Text.RegularExpressions;

namespace RadeonPatcher;

internal static class AmdCpuGraphicsHardware
{
    public const string DeviceIdAlternation = "1114|13C0|15BF|15C8|164C|164D|164E|1900|1901|1902";

    public static bool IsCpuGraphicsDevice(string? instanceId) =>
        Regex.IsMatch(instanceId ?? "", $@"PCI\\VEN_1002&DEV_({DeviceIdAlternation})", RegexOptions.IgnoreCase);
}
