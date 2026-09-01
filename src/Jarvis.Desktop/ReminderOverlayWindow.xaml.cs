using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace Jarvis.Desktop;

public partial class ReminderOverlayWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int NoActivateStyle = 0x08000000;
    private const int ToolWindowStyle = 0x00000080;

    public ReminderOverlayWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => PositionAtScreenEdge();
    }

    public event EventHandler? RestoreRequested;

    public void Present(string message)
    {
        MessageText.Text = message;
        PositionAtScreenEdge();
        if (!IsVisible)
        {
            Show();
        }
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        var handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        _ = SetWindowLongPtr(
            handle,
            ExtendedStyleIndex,
            new IntPtr(styles | NoActivateStyle | ToolWindowStyle));
    }

    private void Overlay_Click(object sender, MouseButtonEventArgs eventArgs) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void PositionAtScreenEdge()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 18);
        Top = Math.Max(workArea.Top, workArea.Bottom - (ActualHeight > 0 ? ActualHeight : 120) - 18);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr window, int index);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr window, int index, IntPtr newValue);

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, newValue)
            : SetWindowLong32(window, index, newValue);
}
