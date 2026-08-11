using System.Windows;
using Jarvis.Contracts;

namespace Jarvis.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _shutdown;
    private Task? _activationTask;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        var mutexName = $"Local\\{CoreProtocol.PipeName}.Desktop.SingleInstance";
        _singleInstance = new Mutex(initiallyOwned: true, mutexName, out var isFirstInstance);
        _ownsSingleInstance = isFirstInstance;
        var activationName = $"Local\\{CoreProtocol.PipeName}.Desktop.Activate";
        if (!isFirstInstance)
        {
            if (EventWaitHandle.TryOpenExisting(activationName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }

            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            activationName);
        _shutdown = new CancellationTokenSource();

        MainWindow = new MainWindow();
        MainWindow.Show();
        _activationTask = Task.Run(() => WaitForActivation(_shutdown.Token));
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        if (_shutdown is not null)
        {
            _shutdown.Cancel();
            _activationEvent?.Set();
            try
            {
                _activationTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        _activationEvent?.Dispose();
        _shutdown?.Dispose();
        if (_singleInstance is not null)
        {
            if (_ownsSingleInstance)
            {
                _singleInstance.ReleaseMutex();
            }

            _singleInstance.Dispose();
        }

        base.OnExit(eventArgs);
    }

    private void WaitForActivation(CancellationToken cancellationToken)
    {
        var activationEvent = _activationEvent!;
        var handles = new[] { activationEvent, cancellationToken.WaitHandle };
        while (!cancellationToken.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled != 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is null)
                {
                    return;
                }

                if (MainWindow.WindowState == WindowState.Minimized)
                {
                    MainWindow.WindowState = WindowState.Normal;
                }

                MainWindow.Show();
                MainWindow.Activate();
            });
        }
    }
}
