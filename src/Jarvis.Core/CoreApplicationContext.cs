using System.Diagnostics;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class CoreApplicationContext : System.Windows.Forms.ApplicationContext
{
    private readonly SupervisionModule _supervision;
    private readonly CompanionModule _companion;
    private readonly CorePipeServer _pipeServer;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _loginStartupItem;
    private readonly LoginStartupRegistration _loginStartup;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string? _configuredDesktopPath;
    private bool _tickRunning;
    private bool _disposed;
    private int _exitRequested;

    public CoreApplicationContext(
        SupervisionModule supervision,
        CompanionModule companion,
        string? configuredDesktopPath)
    {
        _supervision = supervision;
        _companion = companion;
        _configuredDesktopPath = configuredDesktopPath;
        _loginStartup = new LoginStartupRegistration(Environment.ProcessPath);

        _statusItem = new ToolStripMenuItem("正在读取 Core 状态…") { Enabled = false };
        var openDesktopItem = new ToolStripMenuItem("打开 Jarvis Desktop");
        openDesktopItem.Click += (_, _) => TryStartDesktop(showError: true);
        _loginStartupItem = new ToolStripMenuItem("Windows 登录后启动（推荐）")
        {
            CheckOnClick = true,
            Checked = _loginStartup.IsEnabled(),
            Enabled = _loginStartup.CanConfigure
        };
        _loginStartupItem.Click += (_, _) => SetLoginStartupFromTray();
        var exitItem = new ToolStripMenuItem("完全退出 Jarvis");
        exitItem.Click += (_, _) => ConfirmAndExit();

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Jarvis Core：正在启动",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            openDesktopItem,
            _loginStartupItem,
            exitItem
        ]);
        _trayIcon.DoubleClick += (_, _) => TryStartDesktop(showError: true);

        _pipeServer = new CorePipeServer(
            CoreProtocol.PipeName,
            new CoreCommandHandler(
                _supervision,
                _companion,
                productExitRequested: RequestProductExit,
                loginStartupReader: () => _loginStartup.IsEnabled(),
                loginStartupWriter: SetLoginStartup));
        _pipeServer.Start();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        _ = RefreshProjectionAsync();
        TryStartDesktop(showError: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private async void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref _exitRequested, 0) == 1)
        {
            ExitThread();
            return;
        }

        if (_tickRunning)
        {
            return;
        }

        _tickRunning = true;
        try
        {
            await _supervision.TickAsync();
            await _companion.AdvanceAsync();
            await RefreshProjectionAsync();
        }
        catch (Exception exception)
        {
            SetTrayText("Jarvis Core：状态更新失败");
            _statusItem.Text = $"状态更新失败：{exception.Message}";
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private void RequestProductExit() => Interlocked.Exchange(ref _exitRequested, 1);

    private void SetLoginStartup(bool enabled)
    {
        _loginStartup.SetEnabled(enabled);
        _loginStartupItem.Checked = _loginStartup.IsEnabled();
    }

    private void SetLoginStartupFromTray()
    {
        try
        {
            SetLoginStartup(_loginStartupItem.Checked);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _loginStartupItem.Checked = _loginStartup.IsEnabled();
            MessageBox.Show(
                $"无法修改登录自启动：{exception.Message}",
                "Jarvis Core",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task RefreshProjectionAsync()
    {
        var snapshot = await _supervision.GetSnapshotAsync();
        var companion = await _companion.SnapshotAsync();
        var activeComputer = snapshot.Commitments.SingleOrDefault(commitment =>
            commitment.Id == snapshot.ActiveComputerCommitmentId);
        var activeOffline = snapshot.Commitments.FirstOrDefault(commitment =>
            commitment.Kind == CommitmentKind.Offline &&
            commitment.Phase == CommitmentPhase.ActiveUnsupervised);
        var next = snapshot.Commitments
            .Where(commitment => commitment.Phase == CommitmentPhase.Scheduled)
            .OrderBy(commitment => commitment.StartAt)
            .FirstOrDefault();

        var status = activeComputer is not null
            ? $"{(activeComputer.Phase == CommitmentPhase.PreparationBuffer ? "准备缓冲" : "监督中")}：{Title(activeComputer)}"
            : activeOffline is not null
                ? $"线下进行中：{Title(activeOffline)}"
                : next is not null
                    ? $"下一项 {next.StartAt.ToLocalTime():t}：{Title(next)}"
                    : "当前没有进行中的工作承诺";

        if (_pipeServer.FatalError is not null)
        {
            status = "桌面连接已停止：需重启 Jarvis Core（核心监督继续）";
        }
        else if (companion.BackupProjection.AttentionRequired)
        {
            status += " · 本地备份等待百度网盘客户端处理（云端状态未知）";
        }

        _statusItem.Text = status;
        SetTrayText($"Jarvis Core：{status}");
    }

    private void TryStartDesktop(bool showError)
    {
        var path = ResolveDesktopPath();
        if (path is null)
        {
            if (showError)
            {
                MessageBox.Show(
                    "当前构建中没有找到 Jarvis.Desktop.exe，请单独启动 Desktop 项目。",
                    "Jarvis Core",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return;
        }

        try
        {
            Process.Start(DesktopProcessLauncher.CreateStartInfo(path, Environment.ProcessPath));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (showError)
            {
                MessageBox.Show(
                    $"无法打开 Jarvis Desktop：{exception.Message}",
                    "Jarvis Core",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private string? ResolveDesktopPath()
    {
        if (!string.IsNullOrWhiteSpace(_configuredDesktopPath) && File.Exists(_configuredDesktopPath))
        {
            return Path.GetFullPath(_configuredDesktopPath);
        }

        var sibling = Path.Combine(AppContext.BaseDirectory, "Jarvis.Desktop.exe");
        return File.Exists(sibling) ? sibling : null;
    }

    private void ConfirmAndExit()
    {
        var answer = MessageBox.Show(
            "完全退出会同时停止当前监督。确定要退出 Jarvis 吗？",
            "完全退出 Jarvis",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            ExitThread();
        }
    }

    private void SetTrayText(string text)
    {
        _trayIcon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    private static string Title(CommitmentView commitment) =>
        commitment.InputGoal ?? commitment.OutcomeGoal ?? "未命名承诺";
}
