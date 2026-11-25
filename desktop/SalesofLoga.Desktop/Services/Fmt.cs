using System.Globalization;

namespace SalesofLoga.Desktop.Services;

public static class Fmt
{
    private static readonly CultureInfo EnIn = SafeCulture("en-IN");
    private static readonly CultureInfo EnGb = SafeCulture("en-GB");

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); } catch { return CultureInfo.InvariantCulture; }
    }

    /// <summary>"2026-11-24" → "24 Nov 2026"</summary>
    public static string Date(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd MMM yyyy", EnGb) : iso ?? "";

    public static string DateTime(string? isoTs) =>
        DateTimeOffset.TryParse(isoTs, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToLocalTime().ToString("dd MMM yyyy, HH:mm", EnGb) : isoTs ?? "";

    public static string Money(decimal? value, string? currency)
    {
        if (value == null) return "";
        var sym = currency switch { "INR" => "₹", "EUR" => "€", "USD" => "$", _ => (currency ?? "") + " " };
        var culture = currency == "INR" ? EnIn : EnGb;
        return sym + value.Value.ToString("#,##0.00", culture);
    }

    public static string Number(long? n) => n?.ToString("#,##0", EnIn) ?? "";

    public static string Bytes(long n) => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024.0:0} KB" : $"{n / 1048576.0:0.0} MB";

    public static string Sums(IEnumerable<(string Currency, decimal Sum)> sums)
    {
        var s = string.Join(" · ", sums.Select(x => Money(x.Sum, x.Currency)));
        return s.Length == 0 ? "–" : s;
    }
}
