using System.Globalization;

namespace SalesofLoga.Desktop.Data;

public sealed record AttachmentInfo(string Id, string FileName, string Mime, long Size);
public sealed record NewAttachment(string FileName, string Mime, byte[] Data);
public sealed record AuditEntry(long Id, string Ts, string UserName, string Action, string Entity, string? RecordId, string? Label, string? Changes);

public sealed class SalesRecord
{
    public string Id { get; set; } = "";
    /// <summary>Feldwerte: Date/Text/Select als string, Money als decimal (Hauptwährungseinheit), Int als long.</summary>
    public Dictionary<string, object?> Values { get; } = new();
    public string? Currency { get; set; }
    public AttachmentInfo? Attachment { get; set; }
    public string CreatedAt { get; set; } = "";
    public string CreatedById { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public string? UpdatedByName { get; set; }

    public string Text(string field) => Values.TryGetValue(field, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
    public decimal? Money(string field) => Values.TryGetValue(field, out var v) && v is decimal d ? d : null;
    public long? Int(string field) => Values.TryGetValue(field, out var v) && v is long l ? l : null;
}

public sealed class ValidationException : Exception
{
    public IReadOnlyDictionary<string, string> Errors { get; }
    public ValidationException(IDictionary<string, string> errors) : base("Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Key}={e.Value}")))
        => Errors = new Dictionary<string, string>(errors);
}

public sealed class PermissionDeniedException : Exception
{
    public PermissionDeniedException(string message) : base(message) { }
}

/// <summary>Pflegbare Stammdaten (in der Datenbank, Tabelle settings).</summary>
public sealed class MasterData
{
    public List<string> Customers { get; set; } = new() { "TP Solar", "EMMVEE", "Premier", "Renew", "Websol", "Tata Bangalore", "Jupiter" };
    public List<string> QuotationStatuses { get; set; } = new() { "Open", "Closed" };
    public List<string> PoStatuses { get; set; } = new() { "Open", "In Progress", "Delivered", "Closed", "Cancelled" };
    public List<string> Currencies { get; set; } = new() { "INR", "EUR", "USD" };
    public string DefaultCurrency { get; set; } = "INR";
    /// <summary>Windows-Konten (DOMAIN\user) oder UPNs, die fremde Datensätze ändern dürfen.</summary>
    public List<string> Admins { get; set; } = new();
    public int LowStockThreshold { get; set; } = 5;
    public int MaxAttachmentMB { get; set; } = 20;

    public IReadOnlyList<string> OptionsFor(OptionSource s) => s switch
    {
        OptionSource.Customers => Customers,
        OptionSource.QuotationStatuses => QuotationStatuses,
        OptionSource.PoStatuses => PoStatuses,
        _ => Array.Empty<string>()
    };
}

public sealed record DashboardStats(
    int OpenQuotations, IReadOnlyList<(string Currency, decimal Sum)> OpenQuotationValue,
    int ClosedQuotations, IReadOnlyList<(string Currency, decimal Sum)> ClosedQuotationValue,
    int CustomerPos, IReadOnlyList<(string Currency, decimal Sum)> CustomerPoValue,
    int GermanPos, IReadOnlyList<(string Currency, decimal Sum)> GermanPoValue,
    int StockItems, long StockQty,
    IReadOnlyList<(string PartId, string Description, long Qty)> LowStock);

public sealed record ImportResult(int Records, int Skipped, int Attachments, int RejectedAttachments, int AuditEntries, IReadOnlyList<string> Messages);
