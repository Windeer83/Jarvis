using Jarvis.Contracts;

namespace Jarvis.Core.Tests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

internal sealed class FakeActivitySource : IActivitySource
{
    public int ObservationCount { get; private set; }

    public ActivityObservation Next { get; set; } = new(
        ActivityAvailability.Available,
        IsUserActive: true,
        ForegroundProcess: "devenv.exe",
        ObservedAt: DateTimeOffset.UnixEpoch);

    public ValueTask<ActivityObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservationCount++;
        return ValueTask.FromResult(Next);
    }
}

internal sealed class FakeReminderSink : IReminderSink
{
    public List<ReminderNotice> Notices { get; } = [];

    public ValueTask PublishAsync(ReminderNotice notice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Notices.Add(notice);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingReminderSink : IReminderSink
{
    public int AttemptCount { get; private set; }

    public ValueTask PublishAsync(ReminderNotice notice, CancellationToken cancellationToken)
    {
        AttemptCount++;
        return ValueTask.FromException(new InvalidOperationException("simulated reminder failure"));
    }
}

internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "Jarvis.Core.Tests",
        Guid.NewGuid().ToString("N"));

    public TemporaryDatabase()
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "jarvis-test.db");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
