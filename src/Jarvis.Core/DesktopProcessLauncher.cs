using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Core;

internal static class DesktopProcessLauncher
{
    public static ProcessStartInfo CreateStartInfo(
        string desktopPath,
        string? currentProcessPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopPath);

        var dotnetRoot = GetDotnetRoot(currentProcessPath);
        if (dotnetRoot is not null)
        {
            var startInfo = new ProcessStartInfo(desktopPath)
            {
                UseShellExecute = false
            };
            startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
            startInfo.Environment[
                $"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}"] = dotnetRoot;
            return startInfo;
        }

        return new ProcessStartInfo(desktopPath) { UseShellExecute = true };
    }

    private static string? GetDotnetRoot(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(processPath);
    }
}
