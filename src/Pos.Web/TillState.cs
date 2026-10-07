using Microsoft.JSInterop;
using Pos.Engine;

namespace Pos.Web;

/// <summary>
/// Everything the screens share: the register, the simulated back office, the open cart and the sync log.
/// Sales and refunds are queued first and delivered whenever the connection allows; the queue is saved in the
/// browser so unsent sales survive a reload.
/// </summary>
public sealed class TillState
{
    private const string StorageKey = "till.queue.v1";
    private readonly IJSRuntime _js;
    private bool _retrying;
    private TimeSpan _offset;

    public TillState(IJSRuntime js) => _js = js;

    public Register Register { get; private set; } = null!;

    public InMemorySalesServer Server { get; } = new();

    public Cart Cart { get; } = new();

    public bool Ready { get; private set; }

    public bool Online { get; private set; } = true;

    public int RestoredEvents { get; private set; }

    /// <summary>Net value of events carried over from a previous visit, so the books comparison stays honest.</summary>
    public long RestoredNetCents { get; private set; }

    public List<string> Log { get; } = [];

    public string? Toast { get; private set; }

    public event Action? Changed;

    public async Task InitAsync()
    {
        if (Ready)
        {
            return;
        }

        var stored = await _js.InvokeAsync<string?>("tillStore.get", StorageKey);
        var queue = OfflineQueue.Restore(stored);
        RestoredEvents = queue.Pending.Count;
        RestoredNetCents =
            queue.Pending.Where(e => e.Kind == SyncKind.Sale).Sum(e => e.AmountCents) -
            queue.Pending.Where(e => e.Kind == SyncKind.Refund).Sum(e => e.AmountCents);

        // WebAssembly has no time-zone data, so ask the browser for its offset to show local times on receipts.
        var offset = TimeSpan.FromMinutes(await _js.InvokeAsync<int>("tillStore.tzOffsetMinutes"));
        _offset = offset;
        Register = new Register(SampleCatalog.Products, queue: queue, clock: () => DateTimeOffset.UtcNow.ToOffset(offset));
        Register.OpenShift("Sam", 10000);
        Ready = true;
        if (RestoredEvents > 0)
        {
            AddLog($"Found {RestoredEvents} unsent {(RestoredEvents == 1 ? "event" : "events")} from your last visit and put them back in the queue.");
            await SyncAsync();
        }

        Changed?.Invoke();
    }

    public async Task SetOnlineAsync(bool online)
    {
        Online = online;
        Server.Online = online;
        AddLog(online ? "Back online." : "Went offline. Sales will queue on this register.");
        Changed?.Invoke();
        if (online)
        {
            await SyncAsync();
        }
    }

    public void LoseNextReply(bool on)
    {
        Server.LoseNextReplies = on ? 1 : 0;
        Changed?.Invoke();
    }

    public async Task<CheckoutResult> CheckoutAsync(IReadOnlyList<Tender> tenders, long tipCents, string key)
    {
        var result = Register.Checkout(Cart, tenders, tipCents, key);
        if (result.Success && !result.Replayed)
        {
            Cart.Clear();
            await SaveAsync();
            Changed?.Invoke();
            _ = SyncAsync();
        }

        return result;
    }

    public async Task<RefundResult> RefundAsync(Guid saleId, IReadOnlyList<(string Sku, int Quantity)> items, TenderKind method, string reason)
    {
        var result = Register.Refund(saleId, items, method, reason);
        if (result.Success)
        {
            await SaveAsync();
            Changed?.Invoke();
            _ = SyncAsync();
        }

        return result;
    }

    public async Task SyncAsync(bool manual = false)
    {
        var queue = Register.Queue;
        if (queue.Pending.Count == 0)
        {
            if (manual)
            {
                AddLog("Nothing is waiting to sync.");
            }

            return;
        }

        if (!Online)
        {
            if (manual)
            {
                AddLog($"Offline: {queue.Pending.Count} waiting. They stay safe on this register until the connection returns.");
            }

            return;
        }

        var result = await queue.FlushAsync(Server);
        if (result.Sent > 0)
        {
            AddLog($"Sent {result.Sent} to the back office" +
                   (result.DuplicatesAcknowledged > 0 ? $" ({result.DuplicatesAcknowledged} was already there, so it was not counted twice)." : "."));
        }

        if (result.Stopped)
        {
            AddLog($"Delivery stopped: {result.StoppedBecause}. {result.Remaining} still waiting.");
        }

        await SaveAsync();
        Changed?.Invoke();

        if (result.Stopped && Online && !_retrying)
        {
            _retrying = true;
            try
            {
                await Task.Delay(1200);
                AddLog("Retrying automatically...");
                await SyncAsync();
            }
            finally
            {
                _retrying = false;
            }
        }
    }

    public ShiftReport CloseShift(long countedCents)
    {
        Cart.Clear();
        var report = Register.CloseShift(countedCents);
        Changed?.Invoke();
        return report;
    }

    public void OpenShift(string cashier, long floatCents)
    {
        Register.OpenShift(string.IsNullOrWhiteSpace(cashier) ? "Cashier" : cashier.Trim(), floatCents);
        Changed?.Invoke();
    }

    public async Task ShowToastAsync(string message)
    {
        Toast = message;
        Changed?.Invoke();
        await Task.Delay(2600);
        if (Toast == message)
        {
            Toast = null;
            Changed?.Invoke();
        }
    }

    public void NotifyChanged() => Changed?.Invoke();

    private async Task SaveAsync() => await _js.InvokeVoidAsync("tillStore.set", StorageKey, Register.Queue.Serialize());

    private void AddLog(string line)
    {
        Log.Insert(0, $"{DateTimeOffset.UtcNow.ToOffset(_offset):HH:mm:ss}  {line}");
        if (Log.Count > 40)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }
}
