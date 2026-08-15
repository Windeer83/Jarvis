using Jarvis.Contracts;

namespace Jarvis.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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
            return;
        }

        var dataDirectory = ReadOption(args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jarvis");
        var desktopPath = ReadOption(args, "--desktop-path");

        var clock = new SystemClock();
        var reminderHub = new LocalReminderHub();
        SupervisionModule? supervision = null;
        CompanionModule? companion = null;
        try
        {
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
                    new WindowsAiCredentialStore())
                .GetAwaiter()
                .GetResult();

            using var context = new CoreApplicationContext(supervision, companion, desktopPath);
            System.Windows.Forms.Application.Run(context);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Jarvis Core 无法启动：{exception.Message}",
                "Jarvis Core",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
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
