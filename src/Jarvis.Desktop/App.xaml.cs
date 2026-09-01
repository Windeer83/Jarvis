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
    private DesktopPetWindow? _desktopPet;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        if (eventArgs.Args.Any(argument => string.Equals(
                argument, "--health-check", StringComparison.OrdinalIgnoreCase)))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await new CoreClient()
                .SendAsync(new CoreRequest(CoreOperations.GetSnapshot), timeout.Token);
            Environment.Exit(response.Success && response.Snapshot is not null ? 0 : 2);
        }

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

        var window = new MainWindow();
        var desktopPet = new DesktopPetWindow();
        MainWindow = window;
        _desktopPet = desktopPet;
        window.DesktopPetProjectionChanged += (_, projection) => desktopPet.ApplyProjection(projection);
        window.CompanionPersonaSettingsChanged += (_, args) =>
            desktopPet.SetProfessionalMode(args.Settings.ProfessionalMode);
        desktopPet.RestoreRequested += (_, _) => window.OpenConversation();
        desktopPet.CreateCommitmentRequested += (_, _) => window.OpenCommitmentCreation();
        desktopPet.StartRestRequested += async (_, _) => await window.StartDefaultTimedRestAsync();
        desktopPet.ProfessionalModeChanged += async (_, args) =>
            await window.ConfigureProfessionalModeAsync(args.ProfessionalMode);
        desktopPet.ProactivePromptPresented += async (_, args) =>
        {
            if (!await window.AcknowledgeProactivePromptAsync(args.PromptId))
            {
                desktopPet.RetryProactivePromptPresentation(args.PromptId);
            }
        };
        desktopPet.ExitRequested += async (_, _) =>
        {
            if (await window.RequestProductExitAsync())
            {
                window.StopForApplicationExit();
                desktopPet.StopForApplicationExit();
                Shutdown();
            }
        };
        window.Show();
        window.OpenConversation();
        desktopPet.Show();
        desktopPet.ApplyProjection(window.CurrentDesktopPetProjection());
        _activationTask = Task.Run(() => WaitForActivation(_shutdown.Token));
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        if (MainWindow is Jarvis.Desktop.MainWindow window)
        {
            window.StopForApplicationExit();
        }

        _desktopPet?.StopForApplicationExit();

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
                if (MainWindow is not Jarvis.Desktop.MainWindow window)
                {
                    return;
                }

                window.RestoreConfigurationWindow();
                _desktopPet?.ShowPet();
            });
        }
    }
}
