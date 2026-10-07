namespace Pos.Engine;

public static class Receipt
{
    public const int Width = 34;

    public static IReadOnlyList<string> Render(Sale sale, string storeName = "TILL COFFEE & SHOP")
    {
        var lines = new List<string>
        {
            Center(storeName),
            Center(sale.At.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)),
            Row("Receipt", sale.ReceiptNo),
            Row("Cashier", sale.Cashier),
            Rule(),
        };

        foreach (var l in sale.Lines)
        {
            lines.Add(Row($"{l.Quantity} x {Trim(l.Name, 20)}", Money.Format(l.UnitPriceCents * l.Quantity)));
        }

        lines.Add(Rule());
        lines.Add(Row("Subtotal", Money.Format(sale.GrossCents)));
        if (sale.DiscountCents > 0)
        {
            lines.Add(Row("Discounts", Money.Format(-sale.DiscountCents)));
        }

        lines.Add(Row("Tax", Money.Format(sale.TaxCents)));
        if (sale.TipCents > 0)
        {
            lines.Add(Row("Tip", Money.Format(sale.TipCents)));
        }

        lines.Add(Row("TOTAL", Money.Format(sale.DueCents)));
        lines.Add(Rule());
        if (sale.CashReceivedCents > 0)
        {
            lines.Add(Row("Cash", Money.Format(sale.CashReceivedCents)));
        }

        if (sale.CardChargedCents > 0)
        {
            lines.Add(Row("Card", Money.Format(sale.CardChargedCents)));
        }

        if (sale.ChangeCents > 0)
        {
            lines.Add(Row("Change", Money.Format(sale.ChangeCents)));
        }

        foreach (var r in sale.Refunds)
        {
            lines.Add(Rule());
            lines.Add(Row($"Refund #{r.Number} ({r.Method})", Money.Format(-r.AmountCents)));
        }

        lines.Add(Rule());
        lines.Add(Center("Thank you!"));
        return lines;
    }

    private static string Rule() => new('-', Width);

    private static string Center(string text) => text.Length >= Width ? text : new string(' ', (Width - text.Length) / 2) + text;

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + ".";

    private static string Row(string left, string right)
    {
        var gap = Width - left.Length - right.Length;
        return gap >= 1 ? left + new string(' ', gap) + right : $"{left} {right}";
    }
}
