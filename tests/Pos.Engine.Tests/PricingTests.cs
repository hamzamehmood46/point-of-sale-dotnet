using Pos.Engine;

namespace Pos.Engine.Tests;

public sealed class PricingTests
{
    private static readonly TaxPolicy Tax = TaxPolicy.Default; // standard 8%, reduced 4%

    private static Product Item(string sku, long price, TaxClass tax = TaxClass.Exempt) => new(sku, sku, price, tax, "Test");

    private static Product Catalog(string sku) => SampleCatalog.Products.Single(p => p.Sku == sku);

    [Fact]
    public void Reduced_rate_tax_is_applied_to_food_and_drink()
    {
        var cart = new Cart();
        cart.Add(Catalog("LATTE"), 2); // 2 x 4.75 = 9.50, 4% = 0.38

        var totals = cart.Calculate(Tax);

        Assert.Equal(950, totals.NetCents);
        Assert.Equal(38, totals.TaxCents);
        Assert.Equal(988, totals.TotalCents);
    }

    [Fact]
    public void Standard_rate_applies_to_shop_items_and_exempt_items_pay_no_tax()
    {
        var cart = new Cart();
        cart.Add(Catalog("MUG"));   // 14.00 at 8% = 1.12
        cart.Add(Catalog("BEANS")); // 16.50 exempt

        var totals = cart.Calculate(Tax);

        Assert.Equal(112, totals.TaxCents);
        Assert.Equal(1400 + 1650 + 112, totals.TotalCents);
        Assert.Equal(0, totals.Lines.Single(l => l.Sku == "BEANS").TaxCents);
    }

    [Fact]
    public void Tax_rounds_half_away_from_zero()
    {
        var policy = new TaxPolicy(0.05m, 0m);
        var cart = new Cart();
        cart.Add(Item("A", 10, TaxClass.Standard));  // 0.50 cents of tax rounds up to 1
        cart.Add(Item("B", 30, TaxClass.Standard));  // 1.50 rounds up to 2

        var totals = cart.Calculate(policy);

        Assert.Equal(1, totals.Lines[0].TaxCents);
        Assert.Equal(2, totals.Lines[1].TaxCents);
    }

    [Fact]
    public void A_percentage_line_discount_reduces_the_amount_that_is_taxed()
    {
        var cart = new Cart();
        cart.Add(Catalog("LATTE"), 2);
        cart.SetLineDiscount("LATTE", Discount.Percent(10)); // 9.50 - 0.95 = 8.55, tax 4% = 0.342 -> 0.34

        var totals = cart.Calculate(Tax);

        Assert.Equal(95, totals.DiscountCents);
        Assert.Equal(855, totals.NetCents);
        Assert.Equal(34, totals.TaxCents);
        Assert.Equal(889, totals.TotalCents);
    }

    [Fact]
    public void A_fixed_discount_can_never_exceed_the_line_it_applies_to()
    {
        var cart = new Cart();
        cart.Add(Catalog("LATTE"));
        cart.SetLineDiscount("LATTE", Discount.Amount(1000));

        var totals = cart.Calculate(Tax);

        Assert.Equal(475, totals.DiscountCents);
        Assert.Equal(0, totals.NetCents);
        Assert.Equal(0, totals.TotalCents);
    }

    [Fact]
    public void A_cart_discount_is_shared_across_lines_and_the_cents_balance_exactly()
    {
        var cart = new Cart();
        cart.Add(Item("A", 100));
        cart.Add(Item("B", 100));
        cart.Add(Item("C", 100));
        cart.SetCartDiscount(Discount.Amount(100)); // 100 / 3 lines: 34 + 33 + 33

        var totals = cart.Calculate(Tax);

        Assert.Equal([34L, 33L, 33L], totals.Lines.Select(l => l.CartDiscountShareCents).ToArray());
        Assert.Equal(100, totals.DiscountCents);
        Assert.Equal(200, totals.NetCents);
    }

    [Fact]
    public void Line_and_cart_discounts_combine_in_that_order()
    {
        var cart = new Cart();
        cart.Add(Item("A", 1000));
        cart.Add(Item("B", 1000));
        cart.SetLineDiscount("A", Discount.Percent(10)); // A is now worth 900
        cart.SetCartDiscount(Discount.Percent(10));      // 10% of 1900 = 190, shared 900 : 1000

        var totals = cart.Calculate(Tax);

        Assert.Equal(90, totals.Lines[0].CartDiscountShareCents);
        Assert.Equal(100, totals.Lines[1].CartDiscountShareCents);
        Assert.Equal(100 + 190, totals.DiscountCents);
        Assert.Equal(1710, totals.NetCents);
    }

    [Fact]
    public void Gross_minus_discounts_always_equals_net_and_total_equals_net_plus_tax()
    {
        var rng = new Random(2026);
        var catalog = SampleCatalog.Products;
        for (var round = 0; round < 300; round++)
        {
            var cart = new Cart();
            foreach (var product in catalog.OrderBy(_ => rng.Next()).Take(rng.Next(1, 6)))
            {
                cart.Add(product, rng.Next(1, 6));
                if (rng.Next(3) == 0)
                {
                    cart.SetLineDiscount(product.Sku, rng.Next(2) == 0 ? Discount.Percent(rng.Next(0, 101)) : Discount.Amount(rng.Next(0, 3000)));
                }
            }

            if (rng.Next(2) == 0)
            {
                cart.SetCartDiscount(rng.Next(2) == 0 ? Discount.Percent(rng.Next(0, 101)) : Discount.Amount(rng.Next(0, 5000)));
            }

            var t = cart.Calculate(Tax);

            Assert.Equal(t.GrossCents - t.DiscountCents, t.NetCents);
            Assert.Equal(t.NetCents, t.Lines.Sum(l => l.NetCents));
            Assert.Equal(t.NetCents + t.TaxCents, t.TotalCents);
            Assert.All(t.Lines, l => Assert.True(l.NetCents >= 0 && l.TaxCents >= 0));
        }
    }

    [Fact]
    public void Allocation_always_adds_up_to_the_amount()
    {
        var rng = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var weights = Enumerable.Range(0, rng.Next(1, 8)).Select(_ => (long)rng.Next(1, 5000)).ToList();
            var amount = rng.Next(0, 20000);

            Assert.Equal(amount, Money.Allocate(amount, weights).Sum());
        }
    }

    [Fact]
    public void Allocation_with_nothing_to_share_returns_zeros()
    {
        Assert.Equal([0L, 0L], Money.Allocate(500, [0, 0]));
        Assert.Equal([0L, 0L], Money.Allocate(0, [100, 200]));
    }

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(5, "$0.05")]
    [InlineData(988, "$9.88")]
    [InlineData(123456, "$1,234.56")]
    [InlineData(-250, "-$2.50")]
    public void Money_is_formatted_in_dollars_and_cents(long cents, string expected) => Assert.Equal(expected, Money.Format(cents));

    [Fact]
    public void Discounts_reject_nonsense_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Discount.Percent(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => Discount.Percent(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Discount.Amount(-5));
    }

    [Fact]
    public void Adding_the_same_product_twice_merges_into_one_line()
    {
        var cart = new Cart();
        cart.Add(Catalog("MUG"));
        cart.Add(Catalog("MUG"), 2);

        Assert.Single(cart.Lines);
        Assert.Equal(3, cart.Lines[0].Quantity);
        Assert.Equal(3, cart.ItemCount);
    }

    [Fact]
    public void Setting_a_quantity_to_zero_removes_the_line_and_clear_resets_discounts()
    {
        var cart = new Cart();
        cart.Add(Catalog("MUG"));
        cart.SetCartDiscount(Discount.Percent(10));

        cart.SetQuantity("MUG", 0);
        Assert.True(cart.IsEmpty);

        cart.Add(Catalog("MUG"));
        cart.Clear();
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.CartDiscount);
    }

    [Fact]
    public void An_empty_cart_totals_zero()
    {
        var totals = new Cart().Calculate(Tax);

        Assert.Equal(0, totals.TotalCents);
        Assert.Empty(totals.Lines);
    }

    [Fact]
    public void Quantities_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cart().Add(Catalog("MUG"), 0));
    }
}
