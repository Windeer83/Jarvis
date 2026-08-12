using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Jarvis.Contracts;

namespace Jarvis.Core;

public sealed class WindowsActivitySource(IClock clock) : IActivitySource
{
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera"
    };
    private const ulong ActiveThresholdMilliseconds = 60_000;
    private const int WtsCurrentServerHandle = 0;
    private const int WtsSessionInfoEx = 25;
    private const int WtsInfoExLevelOne = 1;
    private const int WtsSessionStateLocked = 0;
    private const int WtsSessionStateUnlocked = 1;

    public ValueTask<ActivityObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!IsCurrentSessionUnlocked())
            {
                return ValueTask.FromResult(Unobservable());
            }

            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref input))
            {
                return ValueTask.FromResult(Unobservable());
            }

            var foreground = GetForegroundProcess();
            var foregroundWebsiteDomain = foreground.ProcessName is not null &&
                                          BrowserProcessNames.Contains(foreground.ProcessName)
                ? BrowserHostnameReader.Read(foreground.Window)
                : null;
            var idleMilliseconds = unchecked((uint)Environment.TickCount - input.Time);
            return ValueTask.FromResult(new ActivityObservation(
                ActivityAvailability.Available,
                idleMilliseconds < ActiveThresholdMilliseconds,
                foreground.ProcessName,
                clock.Now,
                ForegroundWebsiteDomain: foregroundWebsiteDomain,
                IdleDuration: TimeSpan.FromMilliseconds(idleMilliseconds)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ValueTask.FromResult(Unobservable());
        }
    }

    private ActivityObservation Unobservable() => new(
        ActivityAvailability.Unobservable,
        IsUserActive: false,
        ForegroundProcess: null,
        clock.Now,
        ForegroundWebsiteDomain: null,
        IdleDuration: null);

    private static (IntPtr Window, string? ProcessName) GetForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return (IntPtr.Zero, null);
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return (window, null);
        }

        using var process = Process.GetProcessById((int)processId);
        return (window, process.ProcessName);
    }

    private static bool IsCurrentSessionUnlocked()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        if (!WTSQuerySessionInformation(
                new IntPtr(WtsCurrentServerHandle),
                sessionId,
                WtsSessionInfoEx,
                out var buffer,
                out var bytesReturned))
        {
            return false;
        }

        try
        {
            if (buffer == IntPtr.Zero || bytesReturned < Marshal.SizeOf<WtsInfoEx>())
            {
                return false;
            }

            var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);
            if (info.Level != WtsInfoExLevelOne || info.Data.Level1.SessionId != (uint)sessionId)
            {
                return false;
            }

            return info.Data.Level1.SessionFlags switch
            {
                WtsSessionStateUnlocked => true,
                WtsSessionStateLocked => false,
                _ => false
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoEx
    {
        public int Level;
        public WtsInfoExLevel Data;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel
    {
        [FieldOffset(0)]
        public WtsInfoExLevel1 Level1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
        public string WinStationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string DomainName;

        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
