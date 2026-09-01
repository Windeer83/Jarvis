using Jarvis.Contracts;

namespace Jarvis.Core;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface IActivitySource
{
    ValueTask<ActivityObservation> ObserveAsync(CancellationToken cancellationToken);
}

public interface IReminderSink
{
    ValueTask PublishAsync(ReminderNotice notice, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
