using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RadeonPatcher;

[SupportedOSPlatform("windows")]
internal sealed record WindowsInstallerPackage(string Path, string Name, string ProductCode, string UpgradeCode, string Version)
{
    public static WindowsInstallerPackage Read(string path)
    {
        Check(MsiOpenDatabase(path, IntPtr.Zero, out var database));
        uint view = 0;
        try
        {
            Check(MsiDatabaseOpenView(database, "SELECT `Property`, `Value` FROM `Property`", out view));
            Check(MsiViewExecute(view, 0));
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                var result = MsiViewFetch(view, out var record);
                if (result == 259) break;
                Check(result);
                try { properties[ReadField(record, 1)] = ReadField(record, 2); }
                finally { MsiCloseHandle(record); }
            }

            var code = properties["ProductCode"];
            var upgrade = properties["UpgradeCode"];
            if (!Guid.TryParse(code, out _) || !Guid.TryParse(upgrade, out _))
                throw new InvalidDataException($"The MSI has invalid product identifiers: {path}");
            return new WindowsInstallerPackage(path, properties["ProductName"], code, upgrade, properties["ProductVersion"]);
        }
        finally
        {
            if (view != 0) MsiCloseHandle(view);
            MsiCloseHandle(database);
        }
    }

    private static string ReadField(uint record, uint field)
    {
        var buffer = new StringBuilder(256);
        var length = (uint)buffer.Capacity;
        var result = MsiRecordGetString(record, field, buffer, ref length);
        if (result == 234)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = (uint)buffer.Capacity;
            result = MsiRecordGetString(record, field, buffer, ref length);
        }
        Check(result);
        return buffer.ToString();
    }

    public bool IsInstalled => MsiQueryProductState(ProductCode) == 5;

    public bool IsExpectedVersionInstalled()
    {
        if (!IsInstalled) return false;
        var buffer = new StringBuilder(256);
        var length = (uint)buffer.Capacity;
        Check(MsiGetProductInfo(ProductCode, "VersionString", buffer, ref length));
        return string.Equals(buffer.ToString(), Version, StringComparison.Ordinal);
    }

    public IReadOnlyList<string> GetOtherInstalledProducts()
    {
        // Complete enumeration on one thread before any asynchronous uninstall changes the list.
        var products = new List<string>();
        for (uint index = 0; ; index++)
        {
            var buffer = new StringBuilder(39);
            var result = MsiEnumRelatedProducts(UpgradeCode, 0, index, buffer);
            if (result == 259) break;
            Check(result);
            var code = buffer.ToString();
            if (!string.Equals(code, ProductCode, StringComparison.OrdinalIgnoreCase) && MsiQueryProductState(code) == 5)
                products.Add(code);
        }
        return products;
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new Win32Exception((int)result, "Could not read Windows Installer package information.");
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string code);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(string code, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
}
