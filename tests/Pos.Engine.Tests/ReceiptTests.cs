using Pos.Engine;

namespace Pos.Engine.Tests;

public sealed class ReceiptTests
{
    private static Sale SaleWith(Action<Cart>? shape = null, long tip = 0, IReadOnlyList<Tender>? tenders = null)
    {
        var register = new Register(SampleCatalog.Products, clock: () => new DateTimeOffset(2026, 10, 7, 9, 5, 0, TimeSpan.Zero));
        register.OpenShift("Sam", 0);
        var cart = new Cart();
        cart.Add(SampleCatalog.Products.Single(p => p.Sku == "LATTE"), 2);
        shape?.Invoke(cart);
        return register.Checkout(cart, tenders ?? [Tender.Cash(5000)], tip, "k").Sale!;
    }

    private static string Text(Sale sale) => string.Join("\n", Receipt.Render(sale));

    [Fact]
    public void The_receipt_shows_items_totals_and_change()
    {
        var text = Text(SaleWith());

        Assert.Contains("R-000001", text);
        Assert.Contains("2 x Latte", text);
        Assert.Contains("$9.50", text);
        Assert.Contains("Tax", text);
        Assert.Contains("$0.38", text);
        Assert.Contains("TOTAL", text);
        Assert.Contains("$9.88", text);
        Assert.Contains("Change", text);
        Assert.Contains("$40.12", text);
    }

    [Fact]
    public void Discount_and_tip_lines_only_appear_when_they_apply()
    {
        var plain = Text(SaleWith());
        Assert.DoesNotContain("Discounts", plain);
        Assert.DoesNotContain("Tip", plain);

        var fancy = Text(SaleWith(c => c.SetCartDiscount(Discount.Percent(10)), tip: 100));
        Assert.Contains("Discounts", fancy);
        Assert.Contains("-$0.95", fancy);
        Assert.Contains("Tip", fancy);
    }

    [Fact]
    public void A_card_sale_lists_the_card_and_no_change()
    {
        var text = Text(SaleWith(tenders: [Tender.Card(988)]));

        Assert.Contains("Card", text);
        Assert.DoesNotContain("Change", text);
    }

    [Fact]
    public void Refunds_are_listed_on_the_receipt()
    {
        var register = new Register(SampleCatalog.Products);
        register.OpenShift("Sam", 0);
        var cart = new Cart();
        cart.Add(SampleCatalog.Products.Single(p => p.Sku == "LATTE"), 2);
        var sale = register.Checkout(cart, [Tender.Cash(1000)], 0, "k").Sale!;
        register.Refund(sale.Id, [("LATTE", 1)], TenderKind.Cash, "x");

        Assert.Contains("Refund #1 (Cash)", Text(sale));
        Assert.Contains("-$4.94", Text(sale));
    }

    [Fact]
    public void No_line_is_wider_than_the_receipt_paper_even_with_long_names()
    {
        var long_ = new Product("LONG", "A very long product name that keeps going and going", 123456, TaxClass.Standard, "T");
        var register = new Register([long_]);
        register.OpenShift("Sam", 0);
        var cart = new Cart();
        cart.Add(long_, 12);
        var sale = register.Checkout(cart, [Tender.Cash(10_000_000)], 0, "k").Sale!;

        Assert.All(Receipt.Render(sale), line => Assert.True(line.Length <= Receipt.Width + 8, line));
    }
}
