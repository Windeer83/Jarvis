using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Jarvis.Desktop;

public partial class DesktopPetWindow : Window
{
    private const int HotkeyId = 0x4A52;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint KeyJ = 0x4A;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private static readonly TimeSpan AutoMoveInterval = TimeSpan.FromMinutes(2);
    private readonly DesktopPetSettingsStore _settingsStore;
    private readonly DispatcherTimer _visibilityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autoMoveTimer = new() { Interval = AutoMoveInterval };
    private readonly Random _random = new();
    private DesktopPetSettings _settings;
    private DesktopPetProjection _projection = DesktopPetProjection.Disconnected;
    private IntPtr _handle;
    private bool _automaticHidden;
    private bool _isDragging;
    private bool _applicationExit;
    private bool _hotkeyRegistered;
    private DesktopPetVisualState? _visualState;
    private Guid? _reportedPromptId;

    public DesktopPetWindow(DesktopPetSettingsStore? settingsStore = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore ?? new DesktopPetSettingsStore();
        _settings = _settingsStore.Load();
        ApplyScale();
        AutoMoveMenu.IsChecked = _settings.AutoMove;
        ProfessionalModeMenu.IsChecked = _settings.ProfessionalMode;
        ClickThroughMenu.IsChecked = _settings.ClickThrough;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseEnter += (_, _) => SpeechBubble.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => SpeechBubble.Visibility = Visibility.Collapsed;
        MouseLeftButtonDown += Pet_MouseLeftButtonDown;
        MouseLeftButtonUp += (_, _) => _isDragging = false;
        _visibilityTimer.Tick += (_, _) => UpdateAutomaticVisibility();
        _autoMoveTimer.Tick += (_, _) => AutoMoveWithinMonitor();
        _visibilityTimer.Start();
        _autoMoveTimer.Start();
        ApplyProjection(_projection);
    }

    public event EventHandler? RestoreRequested;
    public event EventHandler? CreateCommitmentRequested;
    public event EventHandler? StartRestRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<DesktopPetProfessionalModeChangedEventArgs>? ProfessionalModeChanged;
    public event EventHandler<ProactivePromptPresentedEventArgs>? ProactivePromptPresented;

    public DesktopPetSettings Settings => _settings;

    public void ApplyProjection(DesktopPetProjection projection)
    {
        if (_projection.ProactivePromptId != projection.ProactivePromptId)
        {
            _reportedPromptId = null;
        }

        _projection = projection;
        StatusText.Text = projection.Status;
        DetailText.Text = projection.Detail;
        StateBadgeText.Text = Badge(projection);
        MenuStatus.Header = projection.Status;
        if (_visualState != projection.VisualState)
        {
            CharacterImage.Source = LoadImage(projection.VisualState);
            _visualState = projection.VisualState;
        }
        ToolTip = projection.Status + Environment.NewLine + projection.Detail;
        UpdateAutomaticVisibility();
        TryReportPromptPresentation();
    }

    public void ShowPet()
    {
        Show();
        _automaticHidden = false;
        Opacity = 1;
        IsHitTestVisible = !_settings.ClickThrough;
        Topmost = true;
        UpdateAutomaticVisibility();
        TryReportPromptPresentation();
    }

    public void RetryProactivePromptPresentation(Guid promptId)
    {
        if (_reportedPromptId == promptId)
        {
            _reportedPromptId = null;
        }
    }

    public void SetProfessionalMode(bool enabled)
    {
        if (_settings.ProfessionalMode == enabled)
        {
            return;
        }

        _settings = _settings with { ProfessionalMode = enabled };
        ProfessionalModeMenu.IsChecked = enabled;
        SaveSettings();
        UpdateAutomaticVisibility();
    }

    public void StopForApplicationExit()
    {
        if (_applicationExit)
        {
            return;
        }

        _applicationExit = true;
        _visibilityTimer.Stop();
        _autoMoveTimer.Stop();
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        _handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProc);
        _hotkeyRegistered = RegisterHotKey(_handle, HotkeyId, ModAlt | ModControl, KeyJ);
        ClickThroughMenu.IsEnabled = _hotkeyRegistered;
        if (!_hotkeyRegistered && _settings.ClickThrough)
        {
            _settings = _settings with { ClickThrough = false };
            ClickThroughMenu.IsChecked = false;
            SaveSettings();
        }
        ApplyClickThrough();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        BeginStoryboard((System.Windows.Media.Animation.Storyboard)FindResource("BreathingStoryboard"));
        if (_settings.Left is not null && _settings.Top is not null)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
            SnapAndSave();
            return;
        }

        ResetPosition(save: true);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_handle != IntPtr.Zero && _hotkeyRegistered)
        {
            UnregisterHotKey(_handle, HotkeyId);
        }
    }

    private void Pet_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_settings.ClickThrough || eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var originalLeft = Left;
        var originalTop = Top;
        _isDragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _isDragging = false;
            SnapAndSave();
        }

        var moved = Math.Abs(Left - originalLeft) + Math.Abs(Top - originalTop);
        if (moved < 4)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PetMenu_Opened(object sender, RoutedEventArgs eventArgs)
    {
        AutoMoveMenu.IsChecked = _settings.AutoMove;
        ProfessionalModeMenu.IsChecked = _settings.ProfessionalMode;
        ClickThroughMenu.IsChecked = _settings.ClickThrough;
        var process = DesktopPetSettings.NormalizeProcess(_projection.ForegroundProcess);
        HideCurrentProcessMenu.IsEnabled = process.Length > 0 &&
                                           !process.Equals("Jarvis.Desktop", StringComparison.OrdinalIgnoreCase);
        HideCurrentProcessMenu.Header = HideCurrentProcessMenu.IsEnabled
            ? $"在 {process} 中自动隐藏"
            : "在当前软件中自动隐藏";
    }

    private void OpenPanelMenu_Click(object sender, RoutedEventArgs eventArgs) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void CreateCommitmentMenu_Click(object sender, RoutedEventArgs eventArgs) =>
        CreateCommitmentRequested?.Invoke(this, EventArgs.Empty);

    private void StartRestMenu_Click(object sender, RoutedEventArgs eventArgs) =>
        StartRestRequested?.Invoke(this, EventArgs.Empty);

    private void ExitMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        var answer = MessageBox.Show(
            "完全退出会停止 Core 与当前监督。确定退出 Jarvis 吗？",
            "完全退出 Jarvis",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HideMenu_Click(object sender, RoutedEventArgs eventArgs) => Hide();

    private void AutoMoveMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings = _settings with { AutoMove = AutoMoveMenu.IsChecked };
        SaveSettings();
    }

    private void ProfessionalModeMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings = _settings with { ProfessionalMode = ProfessionalModeMenu.IsChecked };
        SaveSettings();
        UpdateAutomaticVisibility();
        ProfessionalModeChanged?.Invoke(
            this,
            new DesktopPetProfessionalModeChangedEventArgs(_settings.ProfessionalMode));
    }

    private void ClickThroughMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings = _settings with { ClickThrough = ClickThroughMenu.IsChecked };
        SaveSettings();
        ApplyClickThrough();
    }

    private void HideCurrentProcessMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        var process = DesktopPetSettings.NormalizeProcess(_projection.ForegroundProcess);
        if (process.Length == 0 || process.Equals("Jarvis.Desktop", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings = _settings with
        {
            AutoHideProcesses = _settings.HiddenProcesses.Append(process).ToArray()
        };
        _settings = _settings.Normalize();
        SaveSettings();
        UpdateAutomaticVisibility();
    }

    private void ScaleMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            return;
        }

        _settings = _settings with { Scale = scale };
        ApplyScale();
        SnapAndSave();
    }

    private void ResetMenu_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings = new DesktopPetSettings();
        ApplyScale();
        ApplyClickThrough();
        ResetPosition(save: true);
        ShowPet();
    }

    private void ApplyScale()
    {
        Width = 270 * _settings.Scale;
        Height = 390 * _settings.Scale;
    }

    private void ApplyClickThrough()
    {
        IsHitTestVisible = !_settings.ClickThrough && !_automaticHidden;
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(_handle, GwlExStyle);
        style = _settings.ClickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(_handle, GwlExStyle, style);
    }

    private void UpdateAutomaticVisibility()
    {
        if (!IsVisible || _isDragging)
        {
            return;
        }

        var process = DesktopPetSettings.NormalizeProcess(ForegroundPresentationDetector.ForegroundProcessName());
        var hiddenForProcess = _settings.HiddenProcesses.Contains(process, StringComparer.OrdinalIgnoreCase);
        var professionalApplication = _settings.ProfessionalMode && IsProfessionalApplication(process);
        var shouldHide = ForegroundPresentationDetector.IsFullscreen() ||
                         hiddenForProcess || professionalApplication;
        if (shouldHide == _automaticHidden)
        {
            TryReportPromptPresentation();
            return;
        }

        _automaticHidden = shouldHide;
        Opacity = shouldHide ? 0 : 1;
        IsHitTestVisible = !shouldHide && !_settings.ClickThrough;
        TryReportPromptPresentation();
    }

    private void TryReportPromptPresentation()
    {
        if (!IsVisible || _automaticHidden || Opacity <= 0 ||
            _projection.ProactivePromptId is not { } promptId ||
            _reportedPromptId == promptId)
        {
            return;
        }

        _reportedPromptId = promptId;
        ProactivePromptPresented?.Invoke(this, new ProactivePromptPresentedEventArgs(promptId));
    }

    private void AutoMoveWithinMonitor()
    {
        if (!_settings.AutoMove || _automaticHidden || _projection.VisualState is
            DesktopPetVisualState.Working or DesktopPetVisualState.Reminder)
        {
            return;
        }

        var work = CurrentWorkArea();
        Left = Math.Clamp(Left + _random.Next(-18, 19), work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top + _random.Next(-12, 13), work.Top, Math.Max(work.Top, work.Bottom - Height));
        SnapAndSave();
    }

    private void SnapAndSave()
    {
        var work = CurrentWorkArea();
        var snapped = DesktopPetSnap.ConstrainAndSnap(
            Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height,
            work.Left, work.Top, work.Right, work.Bottom);
        Left = snapped.Left;
        Top = snapped.Top;
        _settings = _settings with { Left = Left, Top = Top };
        SaveSettings();
    }

    private void ResetPosition(bool save)
    {
        var work = CurrentWorkArea();
        Left = Math.Max(work.Left, work.Right - Width - 24);
        Top = Math.Max(work.Top, work.Bottom - Height - 24);
        if (save)
        {
            _settings = _settings with { Left = Left, Top = Top };
            SaveSettings();
        }
    }

    private Rect CurrentWorkArea()
    {
        if (_handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var monitor = MonitorFromWindow(_handle, 2);
        var info = new MonitorInformation { Size = (uint)Marshal.SizeOf<MonitorInformation>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ToolTip = $"{_projection.Status}\n桌宠设置暂时无法保存：{exception.Message}";
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _settings = _settings with { ClickThrough = !_settings.ClickThrough };
            ClickThroughMenu.IsChecked = _settings.ClickThrough;
            SaveSettings();
            ApplyClickThrough();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static BitmapImage LoadImage(DesktopPetVisualState state)
    {
        var name = state switch
        {
            DesktopPetVisualState.Working => "work",
            DesktopPetVisualState.Reminder => "reminder",
            DesktopPetVisualState.Happy => "happy",
            DesktopPetVisualState.Caring => "caring",
            _ => "idle"
        };
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(
            $"pack://application:,,,/Jarvis.Desktop;component/Assets/Pet/jarvis-{name}.png");
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string Badge(DesktopPetProjection projection) => projection.OverlayState switch
    {
        DesktopPetOverlayState.Resting => "休息中",
        DesktopPetOverlayState.Talking => "对话中",
        DesktopPetOverlayState.Sleeping => "休眠 / 无法观察",
        _ => projection.VisualState switch
        {
            DesktopPetVisualState.Working => "工作",
            DesktopPetVisualState.Reminder => "提醒",
            DesktopPetVisualState.Happy => "完成",
            DesktopPetVisualState.Caring => "关怀",
            _ => "空闲"
        }
    };

    private static bool IsProfessionalApplication(string process) => process.Equals("powerpnt", StringComparison.OrdinalIgnoreCase) ||
                                                                      process.Equals("zoom", StringComparison.OrdinalIgnoreCase) ||
                                                                      process.Equals("teams", StringComparison.OrdinalIgnoreCase) ||
                                                                      process.Equals("obs64", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);
}
