using System.Runtime.InteropServices;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class DesktopLaunchScenarios
{
    [Fact]
    public void Bundled_dotnet_runtime_is_forwarded_to_the_framework_dependent_desktop()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"Jarvis-DesktopLaunch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var dotnetHost = Path.Combine(directory, "dotnet.exe");
            var desktopExe = Path.Combine(directory, "Jarvis.Desktop.exe");
            File.WriteAllText(dotnetHost, string.Empty);
            File.WriteAllText(desktopExe, string.Empty);

            var startInfo = DesktopProcessLauncher.CreateStartInfo(desktopExe, dotnetHost);

            var architectureVariable = $"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}";
            Assert.Equal(desktopExe, startInfo.FileName);
            Assert.False(startInfo.UseShellExecute);
            Assert.Empty(startInfo.ArgumentList);
            Assert.Equal(directory, startInfo.Environment["DOTNET_ROOT"]);
            Assert.Equal(directory, startInfo.Environment[architectureVariable]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Core_apphost_keeps_the_normal_desktop_apphost_launch()
    {
        var desktopExe = Path.GetFullPath("Jarvis.Desktop.exe");
        var coreExe = Path.GetFullPath("Jarvis.Core.exe");

        var startInfo = DesktopProcessLauncher.CreateStartInfo(desktopExe, coreExe);

        Assert.Equal(desktopExe, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
    }
}
