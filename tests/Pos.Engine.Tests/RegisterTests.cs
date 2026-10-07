using Pos.Engine;

namespace Pos.Engine.Tests;

public sealed class RegisterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeCardProcessor _cards = new();

    private Register NewRegister(bool openShift = true, IEnumerable<Product>? catalog = null)
    {
        var register = new Register(catalog ?? SampleCatalog.Products, cards: _cards, clock: () => Now);
        if (openShift)
        {
            register.OpenShift("Sam", 10000); // $100.00 float
        }

        return register;
    }

    private static Cart CartOf(params (string Sku, int Qty)[] items)
    {
        var cart = new Cart();
        foreach (var (sku, qty) in items)
        {
            cart.Add(SampleCatalog.Products.Single(p => p.Sku == sku), qty);
        }

        return cart;
    }

    private static CheckoutResult PayCash(Register r, Cart cart, long cash, string key, long tip = 0) =>
        r.Checkout(cart, [Tender.Cash(cash)], tip, key);

    [Fact]
    public void A_cash_sale_gives_change_takes_stock_and_is_queued_for_sync()
    {
        var r = NewRegister();

        var result = PayCash(r, CartOf(("LATTE", 2)), 1000, "k1"); // total 9.88

        Assert.True(result.Success);
        var sale = result.Sale!;
        Assert.Equal(988, sale.DueCents);
        Assert.Equal(12, sale.ChangeCents);
        Assert.Equal(988, sale.CashKeptCents);
        Assert.Equal("R-000001", sale.ReceiptNo);
        Assert.Equal(38, r.StockOf("LATTE"));
        var queued = Assert.Single(r.Queue.Pending);
        Assert.Equal(("k1", SyncKind.Sale, 988L), (queued.Key, queued.Kind, queued.AmountCents));
    }

    [Fact]
    public void A_tip_is_added_to_what_the_customer_owes()
    {
        var r = NewRegister();

        var sale = PayCash(r, CartOf(("LATTE", 2)), 1200, "k1", tip: 150).Sale!;

        Assert.Equal(988, sale.TotalCents);
        Assert.Equal(1138, sale.DueCents);
        Assert.Equal(62, sale.ChangeCents);
    }

    [Fact]
    public void Paying_too_little_is_refused_and_changes_nothing()
    {
        var r = NewRegister();

        var result = PayCash(r, CartOf(("LATTE", 2)), 500, "k1");

        Assert.Equal(CheckoutOutcome.Underpaid, result.Outcome);
        Assert.Equal("$4.88 still to pay.", result.Message);
        Assert.Empty(r.Sales);
        Assert.Empty(r.Queue.Pending);
        Assert.Equal(40, r.StockOf("LATTE"));
    }

    [Fact]
    public void A_sale_can_be_split_between_card_and_cash()
    {
        var r = NewRegister();

        var sale = r.Checkout(CartOf(("LATTE", 2)), [Tender.Card(400), Tender.Cash(600)], 0, "k1").Sale!;

        Assert.Equal(400, sale.CardChargedCents);
        Assert.Equal(588, sale.CashKeptCents);
        Assert.Equal(12, sale.ChangeCents);
        Assert.Equal(sale.DueCents, sale.CardChargedCents + sale.CashKeptCents);
    }

    [Fact]
    public void A_card_cannot_be_charged_more_than_is_due()
    {
        var r = NewRegister();

        var result = r.Checkout(CartOf(("LATTE", 2)), [Tender.Card(2000)], 0, "k1");

        Assert.Equal(CheckoutOutcome.Invalid, result.Outcome);
        Assert.Empty(r.Sales);
    }

    [Fact]
    public void A_declined_card_means_no_sale_and_earlier_approvals_are_released()
    {
        var r = NewRegister();

        var result = r.Checkout(CartOf(("LATTE", 2)), [Tender.Card(400, "tok_ok"), Tender.Card(100, "tok_decline"), Tender.Cash(500)], 0, "k1");

        Assert.Equal(CheckoutOutcome.CardDeclined, result.Outcome);
        Assert.Equal("Card declined", result.Message);
        Assert.Equal(0, _cards.ActiveAuthorizations);
        Assert.Empty(r.Sales);
        Assert.Equal(40, r.StockOf("LATTE"));
    }

    [Fact]
    public void Cards_are_not_charged_when_the_cash_part_is_too_small()
    {
        var r = NewRegister();

        var result = r.Checkout(CartOf(("LATTE", 2)), [Tender.Card(400), Tender.Cash(100)], 0, "k1");

        Assert.Equal(CheckoutOutcome.Underpaid, result.Outcome);
        Assert.Equal(0, _cards.ActiveAuthorizations);
    }

    [Fact]
    public void A_sale_paid_fully_by_card_has_no_change()
    {
        var r = NewRegister();

        var sale = r.Checkout(CartOf(("LATTE", 2)), [Tender.Card(988)], 0, "k1").Sale!;

        Assert.Equal(988, sale.CardChargedCents);
        Assert.Equal(0, sale.ChangeCents);
        Assert.Equal(1, _cards.ActiveAuthorizations);
    }

    [Fact]
    public void Selling_needs_an_open_shift_a_key_and_a_non_empty_cart()
    {
        var closed = NewRegister(openShift: false);
        Assert.Equal(CheckoutOutcome.NoOpenShift, PayCash(closed, CartOf(("MUG", 1)), 2000, "k").Outcome);

        var r = NewRegister();
        Assert.Equal(CheckoutOutcome.EmptyCart, PayCash(r, new Cart(), 2000, "k").Outcome);
        Assert.Equal(CheckoutOutcome.Invalid, PayCash(r, CartOf(("MUG", 1)), 2000, " ").Outcome);
        Assert.Equal(CheckoutOutcome.Invalid, r.Checkout(CartOf(("MUG", 1)), [Tender.Cash(0)], 0, "k").Outcome);
        Assert.Equal(CheckoutOutcome.Invalid, PayCash(r, CartOf(("MUG", 1)), 2000, "k", tip: -1).Outcome);
    }

    [Fact]
    public void Selling_more_than_is_in_stock_is_refused()
    {
        var r = NewRegister();

        var result = PayCash(r, CartOf(("GRINDER", 6)), 100000, "k1");

        Assert.Equal(CheckoutOutcome.OutOfStock, result.Outcome);
        Assert.Equal("Only 5 of Hand grinder left.", result.Message);
        Assert.Equal(5, r.StockOf("GRINDER"));
    }

    [Fact]
    public void Items_without_a_stock_limit_can_always_be_sold()
    {
        var r = NewRegister();

        Assert.True(PayCash(r, CartOf(("GIFT-25", 3)), 7500, "k1").Success);
    }

    [Fact]
    public void Repeating_a_sale_with_the_same_key_returns_the_original_and_charges_once()
    {
        var r = NewRegister();
        var cart = CartOf(("LATTE", 2));

        var first = r.Checkout(cart, [Tender.Card(988)], 0, "same-key");
        var again = r.Checkout(cart, [Tender.Card(988)], 0, "same-key");

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Same(first.Sale, again.Sale);
        Assert.Single(r.Sales);
        Assert.Equal(38, r.StockOf("LATTE"));
        Assert.Single(r.Queue.Pending);
        Assert.Equal(1, _cards.ActiveAuthorizations);
    }

    [Fact]
    public void Receipt_numbers_are_sequential()
    {
        var r = NewRegister();

        var a = PayCash(r, CartOf(("MUG", 1)), 2000, "a").Sale!;
        var b = PayCash(r, CartOf(("MUG", 1)), 2000, "b").Sale!;

        Assert.Equal(["R-000001", "R-000002"], new[] { a.ReceiptNo, b.ReceiptNo });
    }

    // ---- refunds ----

    [Fact]
    public void Refunding_in_two_steps_returns_exactly_the_amount_paid()
    {
        var r = NewRegister();
        var sale = PayCash(r, CartOf(("LATTE", 2)), 1000, "k1").Sale!; // net 950 + tax 38

        var first = r.Refund(sale.Id, [("LATTE", 1)], TenderKind.Cash, "wrong drink");
        Assert.True(first.Success);
        Assert.Equal(494, first.Refund!.AmountCents); // 475 + 19
        Assert.Equal(SaleStatus.PartiallyRefunded, sale.Status);

        var second = r.Refund(sale.Id, [("LATTE", 1)], TenderKind.Cash, "wrong drink");
        Assert.Equal(494, second.Refund!.AmountCents);
        Assert.Equal(SaleStatus.Refunded, sale.Status);
        Assert.Equal(sale.TotalCents, sale.RefundedCents);
    }

    [Fact]
    public void Odd_cents_never_leave_a_stray_cent_after_a_full_refund_in_pieces()
    {
        var catalog = new[] { new Product("X", "Item", 100, TaxClass.Exempt, "T") };
        var r = NewRegister(catalog: catalog);
        var cart = new Cart();
        cart.Add(catalog[0], 3);
        cart.SetCartDiscount(Discount.Amount(1)); // net 299 over 3 units
        var sale = PayCash(r, cart, 1000, "k").Sale!;

        var amounts = new[] { 1, 1, 1 }.Select(_ => r.Refund(sale.Id, [("X", 1)], TenderKind.Cash, "r").Refund!.AmountCents).ToArray();

        Assert.Equal([100L, 100L, 99L], amounts);
        Assert.Equal(299, amounts.Sum());
    }

    [Fact]
    public void You_cannot_refund_more_than_was_sold_or_more_than_is_left()
    {
        var r = NewRegister();
        var sale = PayCash(r, CartOf(("LATTE", 2)), 1000, "k1").Sale!;

        Assert.Equal(RefundOutcome.ExceedsSale, r.Refund(sale.Id, [("LATTE", 3)], TenderKind.Cash, "x").Outcome);
        Assert.Equal(RefundOutcome.ExceedsSale, r.Refund(sale.Id, [("MUG", 1)], TenderKind.Cash, "x").Outcome);

        Assert.True(r.Refund(sale.Id, [("LATTE", 2)], TenderKind.Cash, "x").Success);
        var extra = r.Refund(sale.Id, [("LATTE", 1)], TenderKind.Cash, "x");
        Assert.Equal(RefundOutcome.ExceedsSale, extra.Outcome);
        Assert.Equal("Only 0 of Latte can still be refunded.", extra.Message);
    }

    [Fact]
    public void Refund_requests_are_validated()
    {
        var r = NewRegister();
        var sale = PayCash(r, CartOf(("LATTE", 2)), 1000, "k1").Sale!;

        Assert.Equal(RefundOutcome.NotFound, r.Refund(Guid.NewGuid(), [("LATTE", 1)], TenderKind.Cash, "x").Outcome);
        Assert.Equal(RefundOutcome.Invalid, r.Refund(sale.Id, [], TenderKind.Cash, "x").Outcome);
        Assert.Equal(RefundOutcome.Invalid, r.Refund(sale.Id, [("LATTE", 0)], TenderKind.Cash, "x").Outcome);
        Assert.Equal(RefundOutcome.Invalid, r.Refund(sale.Id, [("LATTE", 1), ("LATTE", 1)], TenderKind.Cash, "x").Outcome);
    }

    [Fact]
    public void A_card_refund_cannot_exceed_what_was_charged_to_the_card()
    {
        var r = NewRegister();
        var cashSale = PayCash(r, CartOf(("LATTE", 2)), 1000, "cash").Sale!;
        var cardSale = r.Checkout(CartOf(("LATTE", 2)), [Tender.Card(988)], 0, "card").Sale!;

        Assert.Equal(RefundOutcome.ExceedsCardCharge, r.Refund(cashSale.Id, [("LATTE", 1)], TenderKind.Card, "x").Outcome);
        Assert.True(r.Refund(cardSale.Id, [("LATTE", 2)], TenderKind.Card, "x").Success);
        Assert.Equal(RefundOutcome.ExceedsSale, r.Refund(cardSale.Id, [("LATTE", 1)], TenderKind.Card, "x").Outcome);
    }

    [Fact]
    public void Refunded_goods_return_to_stock_and_tips_are_not_refunded()
    {
        var r = NewRegister();
        var sale = PayCash(r, CartOf(("LATTE", 2)), 1200, "k1", tip: 150).Sale!;
        Assert.Equal(38, r.StockOf("LATTE"));

        r.Refund(sale.Id, [("LATTE", 2)], TenderKind.Cash, "x");

        Assert.Equal(40, r.StockOf("LATTE"));
        Assert.Equal(988, sale.RefundedCents); // the 1.50 tip stays
        Assert.Equal(1138, sale.DueCents);
    }

    [Fact]
    public void A_refund_is_queued_as_its_own_event_and_needs_an_open_shift()
    {
        var r = NewRegister();
        var sale = PayCash(r, CartOf(("LATTE", 2)), 1000, "k1").Sale!;

        r.Refund(sale.Id, [("LATTE", 1)], TenderKind.Cash, "x");

        var refundEvent = r.Queue.Pending[1];
        Assert.Equal(SyncKind.Refund, refundEvent.Kind);
        Assert.Equal(494, refundEvent.AmountCents);
        Assert.EndsWith(":refund:1", refundEvent.Key);

        r.CloseShift(0);
        Assert.Equal(RefundOutcome.NoOpenShift, r.Refund(sale.Id, [("LATTE", 1)], TenderKind.Cash, "x").Outcome);
    }

    // ---- shifts and the cash drawer ----

    [Fact]
    public void The_drawer_reconciles_float_plus_cash_sales_minus_cash_refunds()
    {
        var r = NewRegister();
        var cashSale = PayCash(r, CartOf(("LATTE", 2)), 1000, "a").Sale!;   // 9.88 kept in drawer, 0.12 change given
        r.Checkout(CartOf(("MUG", 1)), [Tender.Card(1512)], 0, "b");        // card: not in the drawer
        r.Refund(cashSale.Id, [("LATTE", 1)], TenderKind.Cash, "x");        // 4.94 paid out

        var x = r.XReport();

        Assert.Equal(2, x.SalesCount);
        Assert.Equal(2350, x.GrossCents);
        Assert.Equal(150, x.TaxCents);
        Assert.Equal(988, x.CashSalesCents);
        Assert.Equal(1512, x.CardSalesCents);
        Assert.Equal(1, x.RefundsCount);
        Assert.Equal(494, x.CashRefundsCents);
        Assert.Equal(10000 + 988 - 494, x.ExpectedCashCents);
        Assert.Null(x.OverShortCents);
        Assert.NotNull(r.CurrentShift);
    }

    [Fact]
    public void Closing_the_shift_reports_over_or_short()
    {
        var r = NewRegister();
        PayCash(r, CartOf(("LATTE", 2)), 1000, "a");

        var closing = r.CloseShift(countedCashCents: 10978); // expected 10000 + 988 = 10988

        Assert.Equal(10988, closing.ExpectedCashCents);
        Assert.Equal(-10, closing.OverShortCents);
        Assert.Null(r.CurrentShift);
        Assert.Equal(CheckoutOutcome.NoOpenShift, PayCash(r, CartOf(("MUG", 1)), 2000, "b").Outcome);
    }

    [Fact]
    public void A_new_shift_starts_with_clean_totals()
    {
        var r = NewRegister();
        PayCash(r, CartOf(("MUG", 1)), 2000, "a");
        r.CloseShift(12000);

        r.OpenShift("Alex", 5000);
        PayCash(r, CartOf(("MUG", 1)), 2000, "b");

        var x = r.XReport();
        Assert.Equal(1, x.SalesCount);
        Assert.Equal(5000 + 1512, x.ExpectedCashCents);
    }

    [Fact]
    public void Shift_rules_are_enforced()
    {
        var r = NewRegister();

        Assert.Throws<InvalidOperationException>(() => r.OpenShift("Alex", 0));
        r.CloseShift(10000);
        Assert.Throws<InvalidOperationException>(() => r.XReport());
        Assert.Throws<ArgumentOutOfRangeException>(() => r.OpenShift("Alex", -1));
    }

    [Fact]
    public void Every_sale_balances_whatever_the_cart_discounts_and_payment_mix()
    {
        var rng = new Random(11);
        var r = NewRegister();
        var products = SampleCatalog.Products.ToList();

        for (var i = 0; i < 300; i++)
        {
            var cart = new Cart();
            foreach (var p in products.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4)))
            {
                cart.Add(p, 1);
                if (rng.Next(3) == 0)
                {
                    cart.SetLineDiscount(p.Sku, Discount.Percent(rng.Next(0, 60)));
                }
            }

            if (rng.Next(2) == 0)
            {
                cart.SetCartDiscount(Discount.Amount(rng.Next(0, 800)));
            }

            var due = cart.Calculate(r.Tax).TotalCents + 0;
            var cardPart = rng.Next(0, 2) == 0 ? 0 : rng.Next(1, (int)Math.Max(2, due));
            var tenders = new List<Tender>();
            if (cardPart > 0 && cardPart <= due)
            {
                tenders.Add(Tender.Card(cardPart));
            }
            else
            {
                cardPart = 0;
            }

            tenders.Add(Tender.Cash(due - cardPart + rng.Next(0, 500) + 1));

            var result = r.Checkout(cart, tenders, 0, $"k{i}");
            if (!result.Success)
            {
                Assert.Equal(CheckoutOutcome.OutOfStock, result.Outcome); // only stock can stop these
                continue;
            }

            var sale = result.Sale!;
            Assert.Equal(sale.DueCents, sale.CashKeptCents + sale.CardChargedCents);
            Assert.True(sale.ChangeCents >= 0);
            Assert.Equal(sale.NetCents + sale.TaxCents + sale.TipCents, sale.DueCents);
        }
    }
}
