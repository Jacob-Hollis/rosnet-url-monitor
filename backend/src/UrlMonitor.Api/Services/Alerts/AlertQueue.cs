using System.Threading.Channels;

namespace UrlMonitor.Api.Services.Alerts;

/// <summary>
/// Ids of saved, Pending alert events waiting to be emailed. Check runs only enqueue, so a slow or
/// unreachable SMTP server never holds up checking; <see cref="Background.AlertDeliveryService"/> sends them.
/// </summary>
public interface IAlertQueue
{
    void Enqueue(IEnumerable<long> alertEventIds);

    /// <summary>Waits until at least one id is queued, then returns everything queued so far.</summary>
    ValueTask<IReadOnlyList<long>> DequeueBatchAsync(CancellationToken cancellationToken);

    /// <summary>Returns everything queued right now without waiting (empty if nothing is queued).</summary>
    IReadOnlyList<long> DequeueAvailable();
}

public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(IEnumerable<long> alertEventIds)
    {
        foreach (var id in alertEventIds) _channel.Writer.TryWrite(id);
    }

    public async ValueTask<IReadOnlyList<long>> DequeueBatchAsync(CancellationToken cancellationToken)
    {
        return await _channel.Reader.WaitToReadAsync(cancellationToken) ? DequeueAvailable() : [];
    }

    public IReadOnlyList<long> DequeueAvailable()
    {
        var ids = new List<long>();
        while (_channel.Reader.TryRead(out var id)) ids.Add(id);
        return ids;
    }
}
