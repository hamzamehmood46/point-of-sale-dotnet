namespace Pos.Engine;

/// <summary>
/// All money in this engine is a whole number of cents (<see cref="long"/>), never a floating-point value, so
/// totals add up exactly. Where a rate produces a fraction of a cent it is rounded half away from zero.
/// </summary>
public static class Money
{
    public static long Round(decimal cents) => (long)Math.Round(cents, 0, MidpointRounding.AwayFromZero);

    public static string Format(long cents)
    {
        var sign = cents < 0 ? "-" : "";
        var abs = Math.Abs(cents);
        var dollars = abs / 100;
        var text = dollars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return $"{sign}${text}.{abs % 100:00}";
    }

    /// <summary>
    /// Splits <paramref name="amount"/> across <paramref name="weights"/> proportionally so that the parts add up to
    /// exactly <paramref name="amount"/> (largest-remainder method). Ties go to the earlier item.
    /// </summary>
    public static long[] Allocate(long amount, IReadOnlyList<long> weights)
    {
        var parts = new long[weights.Count];
        var total = weights.Sum();
        if (amount == 0 || total == 0)
        {
            return parts;
        }

        var remainders = new long[weights.Count];
        long assigned = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            var scaled = amount * weights[i];
            parts[i] = scaled / total;
            remainders[i] = scaled % total;
            assigned += parts[i];
        }

        var left = amount - assigned;
        foreach (var i in Enumerable.Range(0, weights.Count).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)left))
        {
            parts[i]++;
        }

        return parts;
    }
}
