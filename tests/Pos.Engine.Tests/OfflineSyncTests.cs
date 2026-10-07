using Pos.Engine;

namespace Pos.Engine.Tests;

public sealed class OfflineSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static Register RegisterWithSales(int count, out long totalDue)
    {
        var register = new Register(SampleCatalog.Products, clock: () => Now);
        register.OpenShift("Sam", 10000);
        totalDue = 0;
        for (var i = 0; i < count; i++)
        {
            var cart = new Cart();
            cart.Add(SampleCatalog.Products.Single(p => p.Sku == "MUG"));
            var sale = register.Checkout(cart, [Tender.Cash(5000)], 0, $"sale-{i}").Sale!;
            totalDue += sale.DueCents;
        }

        return register;
    }

    [Fact]
    public async Task Everything_is_delivered_once_the_back_office_is_reachable()
    {
        var register = RegisterWithSales(3, out var due);
        var server = new InMemorySalesServer();

        var result = await register.Queue.FlushAsync(server);

        Assert.Equal(3, result.Sent);
        Assert.Equal(0, result.Remaining);
        Assert.False(result.Stopped);
        Assert.Equal(3, server.SalesCount);
        Assert.Equal(due, server.NetRevenueCents);
        Assert.Empty(register.Queue.Pending);
    }

    [Fact]
    public async Task While_offline_sales_wait_in_the_queue_and_are_all_delivered_later()
    {
        var register = RegisterWithSales(3, out var due);
        var server = new InMemorySalesServer { Online = false };

        var blocked = await register.Queue.FlushAsync(server);

        Assert.True(blocked.Stopped);
        Assert.Equal(0, blocked.Sent);
        Assert.Equal(3, blocked.Remaining);
        Assert.Equal(3, register.Queue.Pending.Count);
        Assert.Equal(0, server.SalesCount);

        server.Online = true;
        var later = await register.Queue.FlushAsync(server);

        Assert.Equal(3, later.Sent);
        Assert.Equal(due, server.NetRevenueCents);
    }

    [Fact]
    public async Task Order_is_preserved_when_delivery_stops_part_way()
    {
        var register = RegisterWithSales(4, out var due);
        var server = new FailsOnNthServer(failOnCall: 3);

        var first = await register.Queue.FlushAsync(server);

        Assert.Equal(2, first.Sent);
        Assert.Equal(2, first.Remaining);
        Assert.Equal(["sale-2", "sale-3"], register.Queue.Pending.Select(e => e.Key).ToArray());

        var second = await register.Queue.FlushAsync(server);

        Assert.Equal(2, second.Sent);
        Assert.Equal(4, server.Inner.SalesCount);
        Assert.Equal(due, server.Inner.NetRevenueCents);
    }

    [Fact]
    public async Task A_lost_reply_does_not_double_count_the_sale_when_it_is_retried()
    {
        var register = RegisterWithSales(3, out var due);
        var server = new InMemorySalesServer { LoseNextReplies = 1 };

        var first = await register.Queue.FlushAsync(server);

        // The first sale reached the server, but the register never heard back, so it still holds it.
        Assert.True(first.Stopped);
        Assert.Equal(3, register.Queue.Pending.Count);
        Assert.Equal(1, server.SalesCount);

        var retry = await register.Queue.FlushAsync(server);

        Assert.Equal(3, retry.Sent);
        Assert.Equal(1, retry.DuplicatesAcknowledged);
        Assert.Equal(3, server.SalesCount);          // not 4
        Assert.Equal(1, server.DuplicatesBlocked);
        Assert.Equal(due, server.NetRevenueCents);   // counted exactly once
    }

    [Fact]
    public async Task Refunds_reduce_the_back_office_total()
    {
        var register = RegisterWithSales(1, out var due);
        var sale = register.Sales[0];
        register.Refund(sale.Id, [("MUG", 1)], TenderKind.Cash, "faulty");
        var server = new InMemorySalesServer();

        await register.Queue.FlushAsync(server);

        Assert.Equal(1, server.SalesCount);
        Assert.Equal(1, server.RefundsCount);
        Assert.Equal(due - sale.TotalCents, server.NetRevenueCents);
    }

    [Fact]
    public async Task The_queue_survives_a_reload_and_still_delivers_exactly_once()
    {
        var register = RegisterWithSales(2, out var due);
        var saved = register.Queue.Serialize();

        var restored = OfflineQueue.Restore(saved);

        Assert.Equal(register.Queue.Pending, restored.Pending);

        var server = new InMemorySalesServer();
        await register.Queue.FlushAsync(server);   // the original tab got through...
        await restored.FlushAsync(server);         // ...and the reloaded one resends the same events

        Assert.Equal(2, server.SalesCount);
        Assert.Equal(2, server.DuplicatesBlocked);
        Assert.Equal(due, server.NetRevenueCents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    public void Damaged_or_missing_storage_gives_an_empty_queue_instead_of_a_crash(string? stored)
    {
        Assert.Empty(OfflineQueue.Restore(stored).Pending);
    }

    private sealed class FailsOnNthServer : ISalesServer
    {
        private readonly int _failOnCall;
        private int _calls;

        public FailsOnNthServer(int failOnCall) => _failOnCall = failOnCall;

        public InMemorySalesServer Inner { get; } = new();

        public Task<SubmitResult> SubmitAsync(SyncEvent syncEvent, CancellationToken ct = default) =>
            ++_calls == _failOnCall ? throw new ServerUnavailableException() : Inner.SubmitAsync(syncEvent, ct);
    }
}
