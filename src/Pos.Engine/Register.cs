namespace Pos.Engine;

public sealed class Register
{
    private readonly Dictionary<string, Product> _catalog;
    private readonly Dictionary<string, int> _stock;
    private readonly List<Sale> _sales = [];
    private readonly Dictionary<string, Sale> _byKey = [];
    private readonly ICardProcessor _cards;
    private readonly Func<DateTimeOffset> _clock;
    private int _receiptCounter;
    private int _refundCounter;

    public Register(
        IEnumerable<Product> catalog,
        TaxPolicy? tax = null,
        ICardProcessor? cards = null,
        Func<DateTimeOffset>? clock = null,
        OfflineQueue? queue = null)
    {
        _catalog = catalog.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
        _stock = _catalog.Values.ToDictionary(p => p.Sku, p => p.Stock, StringComparer.OrdinalIgnoreCase);
        Tax = tax ?? TaxPolicy.Default;
        _cards = cards ?? new FakeCardProcessor();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Queue = queue ?? new OfflineQueue();
    }

    public TaxPolicy Tax { get; }

    public OfflineQueue Queue { get; }

    public Shift? CurrentShift { get; private set; }

    public IReadOnlyList<Sale> Sales => _sales;

    public IReadOnlyCollection<Product> Products => _catalog.Values;

    public int StockOf(string sku) => _stock.GetValueOrDefault(sku);

    public Sale? FindSale(Guid id) => _sales.FirstOrDefault(s => s.Id == id);

    public Shift OpenShift(string cashier, long openingFloatCents)
    {
        if (CurrentShift is not null)
        {
            throw new InvalidOperationException("A shift is already open. Close it first.");
        }

        if (openingFloatCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openingFloatCents), "The opening float cannot be negative.");
        }

        return CurrentShift = new Shift(Guid.NewGuid(), cashier, _clock(), openingFloatCents);
    }

    public CheckoutResult Checkout(Cart cart, IReadOnlyList<Tender> tenders, long tipCents, string idempotencyKey)
    {
        if (CurrentShift is null)
        {
            return new CheckoutResult(CheckoutOutcome.NoOpenShift, Message: "Open a shift before selling.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new CheckoutResult(CheckoutOutcome.Invalid, Message: "A sale needs an idempotency key.");
        }

        // A double tap or a retry carries the same key: hand back the original sale instead of selling twice.
        if (_byKey.TryGetValue(idempotencyKey, out var existing))
        {
            return new CheckoutResult(CheckoutOutcome.Completed, existing, Replayed: true);
        }

        if (cart.IsEmpty)
        {
            return new CheckoutResult(CheckoutOutcome.EmptyCart, Message: "The cart is empty.");
        }

        if (tipCents < 0 || tenders.Any(t => t.AmountCents <= 0))
        {
            return new CheckoutResult(CheckoutOutcome.Invalid, Message: "Tips cannot be negative and every payment must be above zero.");
        }

        foreach (var line in cart.Lines)
        {
            var left = StockOf(line.Product.Sku);
            if (line.Quantity > left)
            {
                return new CheckoutResult(CheckoutOutcome.OutOfStock, Message: $"Only {left} of {line.Product.Name} left.");
            }
        }

        var totals = cart.Calculate(Tax);
        var due = totals.TotalCents + tipCents;
        var cardTotal = tenders.Where(t => t.Kind == TenderKind.Card).Sum(t => t.AmountCents);
        var cashHanded = tenders.Where(t => t.Kind == TenderKind.Cash).Sum(t => t.AmountCents);

        if (cardTotal > due)
        {
            return new CheckoutResult(CheckoutOutcome.Invalid, Message: "A card cannot be charged more than the amount due.");
        }

        var cashNeeded = due - cardTotal;
        if (cashHanded < cashNeeded)
        {
            return new CheckoutResult(CheckoutOutcome.Underpaid, Message: $"{Money.Format(cashNeeded - cashHanded)} still to pay.");
        }

        // Cards are charged only once everything else is known to work, and any approved charge is released if a later one fails.
        var authorizations = new List<string>();
        foreach (var card in tenders.Where(t => t.Kind == TenderKind.Card))
        {
            var auth = _cards.Authorize(card.CardToken, card.AmountCents);
            if (!auth.Approved)
            {
                authorizations.ForEach(_cards.Void);
                return new CheckoutResult(CheckoutOutcome.CardDeclined, Message: auth.Reason ?? "Card declined");
            }

            authorizations.Add(auth.Id);
        }

        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            ShiftId = CurrentShift.Id,
            ReceiptNo = $"R-{++_receiptCounter:000000}",
            IdempotencyKey = idempotencyKey,
            At = _clock(),
            Cashier = CurrentShift.Cashier,
            Lines = totals.Lines.Select(l => new SaleLine(l.Sku, l.Name, l.Quantity, l.UnitPriceCents, l.NetCents, l.TaxCents)).ToList(),
            GrossCents = totals.GrossCents,
            DiscountCents = totals.DiscountCents,
            NetCents = totals.NetCents,
            TaxCents = totals.TaxCents,
            TipCents = tipCents,
            CashReceivedCents = cashHanded,
            ChangeCents = cashHanded - cashNeeded,
            CardChargedCents = cardTotal,
        };

        foreach (var line in totals.Lines)
        {
            _stock[line.Sku] -= line.Quantity;
        }

        _sales.Add(sale);
        _byKey[idempotencyKey] = sale;
        Queue.Enqueue(new SyncEvent(idempotencyKey, SyncKind.Sale, sale.Id, sale.DueCents, sale.At));
        return new CheckoutResult(CheckoutOutcome.Completed, sale);
    }

    /// <summary>
    /// Refunds whole or part of a sale. Quantities cannot exceed what is still unrefunded, a card refund cannot exceed
    /// what was charged to the card, tips are not refunded, and refunded goods go back into stock.
    /// </summary>
    public RefundResult Refund(Guid saleId, IReadOnlyList<(string Sku, int Quantity)> items, TenderKind method, string reason)
    {
        if (CurrentShift is null)
        {
            return new RefundResult(RefundOutcome.NoOpenShift, Message: "Open a shift before refunding.");
        }

        var sale = FindSale(saleId);
        if (sale is null)
        {
            return new RefundResult(RefundOutcome.NotFound, Message: "Sale not found.");
        }

        if (items.Count == 0 || items.Any(i => i.Quantity <= 0) || items.Select(i => i.Sku).Distinct().Count() != items.Count)
        {
            return new RefundResult(RefundOutcome.Invalid, Message: "Choose at least one item, with a positive quantity each.");
        }

        var lines = new List<RefundLine>();
        foreach (var (sku, quantity) in items)
        {
            var original = sale.Lines.FirstOrDefault(l => l.Sku == sku);
            if (original is null)
            {
                return new RefundResult(RefundOutcome.ExceedsSale, Message: $"{sku} was not part of this sale.");
            }

            var already = sale.RefundedQuantity(sku);
            var remaining = original.Quantity - already;
            if (quantity > remaining)
            {
                return new RefundResult(RefundOutcome.ExceedsSale, Message: $"Only {remaining} of {original.Name} can still be refunded.");
            }

            var refundedNet = sale.Refunds.SelectMany(r => r.Lines).Where(l => l.Sku == sku).Sum(l => l.NetCents);
            var refundedTax = sale.Refunds.SelectMany(r => r.Lines).Where(l => l.Sku == sku).Sum(l => l.TaxCents);

            // Refunding the last units returns exactly what is left, so rounding can never leave a stray cent behind.
            var net = quantity == remaining ? original.NetCents - refundedNet : Money.Round((decimal)original.NetCents * quantity / original.Quantity);
            var tax = quantity == remaining ? original.TaxCents - refundedTax : Money.Round((decimal)original.TaxCents * quantity / original.Quantity);
            lines.Add(new RefundLine(sku, quantity, net, tax));
        }

        var amount = lines.Sum(l => l.NetCents + l.TaxCents);
        if (method == TenderKind.Card && amount > sale.CardChargedCents - sale.RefundedToCardCents)
        {
            return new RefundResult(RefundOutcome.ExceedsCardCharge, Message: "That is more than was charged to the card.");
        }

        var refund = new Refund(++_refundCounter, _clock(), lines, amount, method, reason);
        sale.Refunds.Add(refund);
        foreach (var line in lines)
        {
            _stock[line.Sku] += line.Quantity;
        }

        Queue.Enqueue(new SyncEvent($"{sale.Id}:refund:{refund.Number}", SyncKind.Refund, sale.Id, amount, refund.At));
        return new RefundResult(RefundOutcome.Completed, refund);
    }

    /// <summary>The running totals for the open shift, without closing it.</summary>
    public ShiftReport XReport() => BuildReport(counted: null);

    public ShiftReport CloseShift(long countedCashCents)
    {
        var report = BuildReport(countedCashCents);
        CurrentShift = null;
        return report;
    }

    private ShiftReport BuildReport(long? counted)
    {
        var shift = CurrentShift ?? throw new InvalidOperationException("No shift is open.");
        var sales = _sales.Where(s => s.ShiftId == shift.Id).ToList();
        var refunds = sales.SelectMany(s => s.Refunds).ToList();
        var cashSales = sales.Sum(s => s.CashKeptCents);
        var cashRefunds = refunds.Where(r => r.Method == TenderKind.Cash).Sum(r => r.AmountCents);

        return new ShiftReport(
            shift, sales.Count, sales.Sum(s => s.GrossCents), sales.Sum(s => s.DiscountCents), sales.Sum(s => s.NetCents),
            sales.Sum(s => s.TaxCents), sales.Sum(s => s.TipCents), cashSales, sales.Sum(s => s.CardChargedCents),
            refunds.Count, cashRefunds, refunds.Where(r => r.Method == TenderKind.Card).Sum(r => r.AmountCents),
            shift.OpeningFloatCents + cashSales - cashRefunds, counted);
    }
}
