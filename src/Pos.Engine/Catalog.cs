namespace Pos.Engine;

public enum TaxClass
{
    Standard,
    Reduced,
    Exempt,
}

public sealed record Product(string Sku, string Name, long PriceCents, TaxClass Tax, string Category, int Stock = int.MaxValue);

/// <summary>Rates are fractions: 0.08 is 8%. Tax is worked out per line on the discounted amount, then rounded.</summary>
public sealed record TaxPolicy(decimal StandardRate, decimal ReducedRate)
{
    public static TaxPolicy Default { get; } = new(0.08m, 0.04m);

    public decimal RateFor(TaxClass taxClass) => taxClass switch
    {
        TaxClass.Standard => StandardRate,
        TaxClass.Reduced => ReducedRate,
        _ => 0m,
    };
}

public static class SampleCatalog
{
    public static IReadOnlyList<Product> Products { get; } =
    [
        new("ESPRESSO", "Espresso", 325, TaxClass.Reduced, "Drinks", 40),
        new("LATTE", "Latte", 475, TaxClass.Reduced, "Drinks", 40),
        new("COLD-BREW", "Cold brew", 450, TaxClass.Reduced, "Drinks", 30),
        new("CROISSANT", "Croissant", 350, TaxClass.Reduced, "Food", 12),
        new("BAGEL", "Bagel", 325, TaxClass.Reduced, "Food", 12),
        new("SANDWICH", "Egg sandwich", 895, TaxClass.Reduced, "Food", 10),
        new("MUG", "Ceramic mug", 1400, TaxClass.Standard, "Shop", 15),
        new("TOTE", "Tote bag", 1800, TaxClass.Standard, "Shop", 8),
        new("GRINDER", "Hand grinder", 4200, TaxClass.Standard, "Shop", 5),
        new("BEANS", "Coffee beans 340g", 1650, TaxClass.Exempt, "Shop", 20),
        new("GIFT-25", "Gift card $25", 2500, TaxClass.Exempt, "Shop"),
    ];
}
