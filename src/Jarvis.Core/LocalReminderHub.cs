using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class LocalReminderHub : IReminderSink
{
    public event EventHandler<ReminderNotice>? Published;

    public ValueTask PublishAsync(ReminderNotice notice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Published?.Invoke(this, notice);
        return ValueTask.CompletedTask;
    }
}
