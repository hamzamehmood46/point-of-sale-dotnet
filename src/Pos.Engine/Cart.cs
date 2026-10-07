namespace Pos.Engine;

public abstract record Discount
{
    public static Discount Percent(decimal percent) => new PercentDiscount(percent);

    public static Discount Amount(long cents) => new AmountDiscount(cents);

    /// <summary>The discount this applies to <paramref name="baseCents"/>, never more than the base itself.</summary>
    public abstract long On(long baseCents);
}

public sealed record PercentDiscount : Discount
{
    public PercentDiscount(decimal percent)
    {
        if (percent < 0 || percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "A percentage discount must be between 0 and 100.");
        }

        Value = percent;
    }

    public decimal Value { get; }

    public override long On(long baseCents) => Math.Min(baseCents, Money.Round(baseCents * Value / 100m));

    public override string ToString() => $"{Value:0.##}% off";
}

public sealed record AmountDiscount : Discount
{
    public AmountDiscount(long cents)
    {
        if (cents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cents), "A discount cannot be negative.");
        }

        Cents = cents;
    }

    public long Cents { get; }

    public override long On(long baseCents) => Math.Min(baseCents, Cents);

    public override string ToString() => $"{Money.Format(Cents)} off";
}

public sealed class CartLine
{
    internal CartLine(Product product, int quantity)
    {
        Product = product;
        Quantity = quantity;
    }

    public Product Product { get; }

    public int Quantity { get; internal set; }

    public Discount? Discount { get; internal set; }
}

public sealed record LineTotal(
    string Sku, string Name, int Quantity, long UnitPriceCents, TaxClass Tax,
    long GrossCents, long LineDiscountCents, long CartDiscountShareCents, long NetCents, long TaxCents)
{
    public long TotalCents => NetCents + TaxCents;
}

public sealed record Totals(
    IReadOnlyList<LineTotal> Lines, long GrossCents, long DiscountCents, long NetCents, long TaxCents)
{
    public long TotalCents => NetCents + TaxCents;
}

public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    public IReadOnlyList<CartLine> Lines => _lines;

    public Discount? CartDiscount { get; private set; }

    public bool IsEmpty => _lines.Count == 0;

    public int ItemCount => _lines.Sum(l => l.Quantity);

    public void Add(Product product, int quantity = 1)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        var line = Find(product.Sku);
        if (line is null)
        {
            _lines.Add(new CartLine(product, quantity));
        }
        else
        {
            line.Quantity += quantity;
        }
    }

    /// <summary>Setting a quantity of zero or less removes the line.</summary>
    public void SetQuantity(string sku, int quantity)
    {
        var line = Find(sku) ?? throw new InvalidOperationException($"{sku} is not in the cart.");
        if (quantity <= 0)
        {
            _lines.Remove(line);
        }
        else
        {
            line.Quantity = quantity;
        }
    }

    public void Remove(string sku) => _lines.RemoveAll(l => l.Product.Sku == sku);

    public void SetLineDiscount(string sku, Discount? discount) =>
        (Find(sku) ?? throw new InvalidOperationException($"{sku} is not in the cart.")).Discount = discount;

    public void SetCartDiscount(Discount? discount) => CartDiscount = discount;

    public void Clear()
    {
        _lines.Clear();
        CartDiscount = null;
    }

    public Totals Calculate(TaxPolicy tax) => Pricing.Calculate(_lines, CartDiscount, tax);

    private CartLine? Find(string sku) => _lines.FirstOrDefault(l => l.Product.Sku == sku);
}

public static class Pricing
{
    /// <summary>
    /// Order of operations: line discount, then the cart-wide discount (shared across lines in proportion to what
    /// each line is still worth, with the cents balanced exactly), then tax per line on the final net amount.
    /// </summary>
    public static Totals Calculate(IReadOnlyList<CartLine> lines, Discount? cartDiscount, TaxPolicy tax)
    {
        var gross = lines.Select(l => l.Product.PriceCents * l.Quantity).ToArray();
        var lineDiscount = lines.Select((l, i) => l.Discount?.On(gross[i]) ?? 0).ToArray();
        var afterLine = gross.Select((g, i) => g - lineDiscount[i]).ToArray();

        var cartAmount = cartDiscount?.On(afterLine.Sum()) ?? 0;
        var share = Money.Allocate(cartAmount, afterLine);

        var result = new List<LineTotal>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var product = lines[i].Product;
            var net = afterLine[i] - share[i];
            var taxCents = Money.Round(net * tax.RateFor(product.Tax));
            result.Add(new LineTotal(product.Sku, product.Name, lines[i].Quantity, product.PriceCents, product.Tax,
                gross[i], lineDiscount[i], share[i], net, taxCents));
        }

        return new Totals(result, gross.Sum(), lineDiscount.Sum() + share.Sum(), result.Sum(l => l.NetCents), result.Sum(l => l.TaxCents));
    }
}
