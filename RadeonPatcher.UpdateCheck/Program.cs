using System.Diagnostics;

if (args.Length == 0 || !File.Exists(args[0]))
{
    return;
}

Process.Start(new ProcessStartInfo(args[0])
{
    UseShellExecute = true,
    Arguments = "--check-updates"
});
