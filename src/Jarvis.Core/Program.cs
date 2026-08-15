using System.Diagnostics;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var dataDirectory = ReadOption(args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jarvis");
        Directory.CreateDirectory(dataDirectory);
        var dataMarker = Path.Combine(dataDirectory, ".jarvis-data-root");
        if (!File.Exists(dataMarker)) File.WriteAllText(dataMarker, "Jarvis local data root");
        if (args.Any(argument => string.Equals(
                argument, "--health-check", StringComparison.OrdinalIgnoreCase)))
            return RunHealthCheck(dataDirectory);

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            $"Local\\{CoreProtocol.PipeName}.SingleInstance",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Jarvis Core 已经在当前 Windows 会话中运行。",
                "Jarvis",
                MessageBoxButtons.OK,
            MessageBoxIcon.Information);
            return 0;
        }

        var desktopPath = ReadOption(args, "--desktop-path");

        var clock = new SystemClock();
        var reminderHub = new LocalReminderHub();
        SupervisionModule? supervision = null;
        CompanionModule? companion = null;
        try
        {
            PendingRestoreCoordinator.ApplyIfPendingAsync(dataDirectory).GetAwaiter().GetResult();
            var databasePath = Path.Combine(dataDirectory, "jarvis.db");
            supervision = SupervisionModule.OpenAsync(
                    databasePath,
                    clock,
                    new WindowsActivitySource(clock),
                    reminderHub)
                .GetAwaiter()
                .GetResult();
            companion = CompanionModule.OpenAsync(
                    databasePath,
                    supervision,
                    clock,
                    new LarkCliWorktimeChannel(),
                    new SiliconFlowCloudAiProvider(),
                    new WindowsCredentialStore("Jarvis/AI/"),
                    new WindowsBackupPasswordStore(),
                    new WindowsBaiduClientProbe())
                .GetAwaiter()
                .GetResult();

            using var context = new CoreApplicationContext(supervision, companion, desktopPath);
            System.Windows.Forms.Application.Run(context);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Jarvis Core 无法启动：{exception.Message}",
                "Jarvis Core",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            companion?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            supervision?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int RunHealthCheck(string dataDirectory)
    {
        SupervisionModule? supervision = null;
        CompanionModule? companion = null;
        CorePipeServer? pipeServer = null;
        Process? desktop = null;
        try
        {
            var databasePath = Path.Combine(dataDirectory, "jarvis.db");
            var clock = new SystemClock();
            supervision = SupervisionModule.OpenAsync(
                    databasePath, clock, new WindowsActivitySource(clock), new LocalReminderHub())
                .GetAwaiter().GetResult();
            companion = CompanionModule.OpenAsync(
                    databasePath, supervision, clock, new LarkCliWorktimeChannel(),
                    new SiliconFlowCloudAiProvider(), new WindowsCredentialStore("Jarvis/AI/"),
                    new WindowsBackupPasswordStore(), new WindowsBaiduClientProbe(),
                    configureExternalChannels: false)
                .GetAwaiter().GetResult();
            _ = supervision.GetSnapshotAsync().GetAwaiter().GetResult();
            _ = companion.SnapshotAsync().GetAwaiter().GetResult();
            var desktopPath = Path.Combine(AppContext.BaseDirectory, "Jarvis.Desktop.exe");
            if (!File.Exists(desktopPath)) return 2;

            pipeServer = new CorePipeServer(
                CoreProtocol.PipeName,
                new CoreCommandHandler(supervision, companion));
            pipeServer.Start();
            var startInfo = new ProcessStartInfo
            {
                FileName = desktopPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--health-check");
            desktop = Process.Start(startInfo);
            if (desktop is null || !desktop.WaitForExit(15000))
            {
                desktop?.Kill(entireProcessTree: true);
                desktop?.WaitForExit(5000);
                return 4;
            }

            return desktop.ExitCode == 0 && pipeServer.FatalError is null ? 0 : 5;
        }
        catch
        {
            return 3;
        }
        finally
        {
            if (desktop is { HasExited: false })
            {
                desktop.Kill(entireProcessTree: true);
                desktop.WaitForExit(5000);
            }
            pipeServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            companion?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            supervision?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
