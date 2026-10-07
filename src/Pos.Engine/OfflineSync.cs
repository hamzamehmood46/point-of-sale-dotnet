using System.Text.Json;

namespace Pos.Engine;

public enum SyncKind
{
    Sale,
    Refund,
}

/// <summary>
/// One thing the back office must learn about. The key is generated on the register when the sale happens, so the
/// same event always carries the same key no matter how many times it is sent.
/// </summary>
public sealed record SyncEvent(string Key, SyncKind Kind, Guid SaleId, long AmountCents, DateTimeOffset At);

public enum SubmitResult
{
    Accepted,
    Duplicate,
}

public sealed class ServerUnavailableException : Exception
{
    public ServerUnavailableException(string message = "The back office is unreachable")
        : base(message)
    {
    }
}

public interface ISalesServer
{
    Task<SubmitResult> SubmitAsync(SyncEvent syncEvent, CancellationToken ct = default);
}

public sealed record FlushResult(int Sent, int DuplicatesAcknowledged, int Remaining, bool Stopped, string? StoppedBecause);

/// <summary>
/// The register's outbox. Every sale and refund is queued here first, so nothing is lost while offline, and is
/// removed only after the back office confirms it. Order is preserved: if one event cannot be sent, the ones
/// behind it wait.
/// </summary>
public sealed class OfflineQueue
{
    private readonly List<SyncEvent> _pending = [];

    public IReadOnlyList<SyncEvent> Pending => _pending;

    public int Synced { get; private set; }

    public void Enqueue(SyncEvent syncEvent) => _pending.Add(syncEvent);

    public async Task<FlushResult> FlushAsync(ISalesServer server, CancellationToken ct = default)
    {
        int sent = 0, duplicates = 0;
        foreach (var evt in _pending.ToList())
        {
            try
            {
                var result = await server.SubmitAsync(evt, ct);
                if (result == SubmitResult.Duplicate)
                {
                    duplicates++; // the server already had it (an earlier reply was lost): safe to treat as delivered
                }
            }
            catch (ServerUnavailableException e)
            {
                return new FlushResult(sent, duplicates, _pending.Count, true, e.Message);
            }

            _pending.Remove(evt);
            sent++;
            Synced++;
        }

        return new FlushResult(sent, duplicates, _pending.Count, false, null);
    }

    public string Serialize() => JsonSerializer.Serialize(_pending);

    public static OfflineQueue Restore(string? json)
    {
        var queue = new OfflineQueue();
        if (string.IsNullOrWhiteSpace(json))
        {
            return queue;
        }

        try
        {
            var events = JsonSerializer.Deserialize<List<SyncEvent>>(json);
            if (events is not null)
            {
                queue._pending.AddRange(events.Where(e => e is not null && !string.IsNullOrEmpty(e.Key)));
            }
        }
        catch (JsonException)
        {
            // Damaged storage must never stop the till from opening.
        }

        return queue;
    }
}

/// <summary>
/// A simulated back office that de-duplicates by key. It can be taken offline, and can be told to "lose" its reply
/// after storing an event, which is exactly the situation that makes naive retries double-count.
/// </summary>
public sealed class InMemorySalesServer : ISalesServer
{
    private readonly Dictionary<string, SyncEvent> _byKey = [];

    public bool Online { get; set; } = true;

    /// <summary>How many upcoming replies to drop after the event has been stored.</summary>
    public int LoseNextReplies { get; set; }

    public int DuplicatesBlocked { get; private set; }

    public int SalesCount => _byKey.Values.Count(e => e.Kind == SyncKind.Sale);

    public int RefundsCount => _byKey.Values.Count(e => e.Kind == SyncKind.Refund);

    public long NetRevenueCents =>
        _byKey.Values.Where(e => e.Kind == SyncKind.Sale).Sum(e => e.AmountCents) -
        _byKey.Values.Where(e => e.Kind == SyncKind.Refund).Sum(e => e.AmountCents);

    public Task<SubmitResult> SubmitAsync(SyncEvent syncEvent, CancellationToken ct = default)
    {
        if (!Online)
        {
            throw new ServerUnavailableException();
        }

        SubmitResult result;
        if (_byKey.ContainsKey(syncEvent.Key))
        {
            DuplicatesBlocked++;
            result = SubmitResult.Duplicate;
        }
        else
        {
            _byKey[syncEvent.Key] = syncEvent;
            result = SubmitResult.Accepted;
        }

        if (LoseNextReplies > 0)
        {
            LoseNextReplies--;
            throw new ServerUnavailableException("The connection dropped before the reply arrived");
        }

        return Task.FromResult(result);
    }
}
