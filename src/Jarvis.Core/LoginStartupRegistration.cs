using Microsoft.Win32;

namespace Jarvis.Core;

internal sealed class LoginStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Jarvis Core";
    private readonly string? _coreExecutablePath;

    public LoginStartupRegistration(string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            _coreExecutablePath = Path.GetFullPath(processPath);
        }
    }

    public bool CanConfigure => _coreExecutablePath is not null;

    public bool IsEnabled()
    {
        if (_coreExecutablePath is null) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var current = key?.GetValue(ValueName) as string;
        return string.Equals(current, Command, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (_coreExecutablePath is null)
            throw new InvalidOperationException("当前开发宿主不是已安装的 Jarvis Core，不能配置登录自启动。");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private string Command => $"\"{_coreExecutablePath}\"";
}
