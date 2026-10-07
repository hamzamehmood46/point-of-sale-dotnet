namespace Pos.Engine;

public enum TenderKind
{
    Cash,
    Card,
}

/// <summary>For cash, the amount handed over (change is worked out). For a card, the amount to charge.</summary>
public sealed record Tender(TenderKind Kind, long AmountCents, string? CardToken = null)
{
    public static Tender Cash(long cents) => new(TenderKind.Cash, cents);

    public static Tender Card(long cents, string token = "tok_visa") => new(TenderKind.Card, cents, token);
}

public enum SaleStatus
{
    Completed,
    PartiallyRefunded,
    Refunded,
}

public sealed record SaleLine(string Sku, string Name, int Quantity, long UnitPriceCents, long NetCents, long TaxCents);

public sealed record RefundLine(string Sku, int Quantity, long NetCents, long TaxCents);

public sealed record Refund(int Number, DateTimeOffset At, IReadOnlyList<RefundLine> Lines, long AmountCents, TenderKind Method, string Reason);

public sealed class Sale
{
    public required Guid Id { get; init; }

    public required Guid ShiftId { get; init; }

    public required string ReceiptNo { get; init; }

    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Cashier { get; init; }

    public required IReadOnlyList<SaleLine> Lines { get; init; }

    public required long GrossCents { get; init; }

    public required long DiscountCents { get; init; }

    public required long NetCents { get; init; }

    public required long TaxCents { get; init; }

    public required long TipCents { get; init; }

    /// <summary>Cash the customer handed over.</summary>
    public required long CashReceivedCents { get; init; }

    public required long ChangeCents { get; init; }

    public required long CardChargedCents { get; init; }

    public List<Refund> Refunds { get; } = [];

    public long TotalCents => NetCents + TaxCents;

    /// <summary>What the customer owed: the total plus any tip.</summary>
    public long DueCents => TotalCents + TipCents;

    public long CashKeptCents => CashReceivedCents - ChangeCents;

    public long RefundedCents => Refunds.Sum(r => r.AmountCents);

    public long RefundedToCardCents => Refunds.Where(r => r.Method == TenderKind.Card).Sum(r => r.AmountCents);

    public int RefundedQuantity(string sku) => Refunds.SelectMany(r => r.Lines).Where(l => l.Sku == sku).Sum(l => l.Quantity);

    public SaleStatus Status =>
        Refunds.Count == 0 ? SaleStatus.Completed
        : Lines.All(l => RefundedQuantity(l.Sku) >= l.Quantity) ? SaleStatus.Refunded
        : SaleStatus.PartiallyRefunded;
}

public enum CheckoutOutcome
{
    Completed,
    EmptyCart,
    NoOpenShift,
    Invalid,
    OutOfStock,
    Underpaid,
    CardDeclined,
}

public sealed record CheckoutResult(CheckoutOutcome Outcome, Sale? Sale = null, string? Message = null, bool Replayed = false)
{
    public bool Success => Outcome == CheckoutOutcome.Completed;
}

public enum RefundOutcome
{
    Completed,
    NotFound,
    NoOpenShift,
    Invalid,
    ExceedsSale,
    ExceedsCardCharge,
}

public sealed record RefundResult(RefundOutcome Outcome, Refund? Refund = null, string? Message = null)
{
    public bool Success => Outcome == RefundOutcome.Completed;
}

public sealed record CardAuth(bool Approved, string Id, string? Reason = null);

public interface ICardProcessor
{
    CardAuth Authorize(string? token, long amountCents);

    void Void(string authId);
}

/// <summary>A stand-in card terminal: "tok_decline" is declined, any other "tok_..." token is approved.</summary>
public sealed class FakeCardProcessor : ICardProcessor
{
    private readonly HashSet<string> _active = [];
    private int _next;

    public int ActiveAuthorizations => _active.Count;

    public CardAuth Authorize(string? token, long amountCents)
    {
        if (token is null || !token.StartsWith("tok_", StringComparison.Ordinal))
        {
            return new CardAuth(false, "", "Invalid card");
        }

        if (token == "tok_decline")
        {
            return new CardAuth(false, "", "Card declined");
        }

        var id = $"auth_{++_next}";
        _active.Add(id);
        return new CardAuth(true, id);
    }

    public void Void(string authId) => _active.Remove(authId);
}

public sealed record Shift(Guid Id, string Cashier, DateTimeOffset OpenedAt, long OpeningFloatCents);

public sealed record ShiftReport(
    Shift Shift, int SalesCount, long GrossCents, long DiscountCents, long NetSalesCents, long TaxCents, long TipsCents,
    long CashSalesCents, long CardSalesCents, int RefundsCount, long CashRefundsCents, long CardRefundsCents,
    long ExpectedCashCents, long? CountedCashCents)
{
    /// <summary>Counted cash minus expected cash: negative means the drawer is short.</summary>
    public long? OverShortCents => CountedCashCents - ExpectedCashCents;
}
