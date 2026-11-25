using System.Text.RegularExpressions;

namespace SalesofLoga.Desktop.Data;

public enum FieldType { Date, Text, Select, Money, Int, File }
public enum SearchMode { None, Contains, Equals }
public enum OptionSource { None, Customers, QuotationStatuses, PoStatuses }

public sealed record FieldDef(
    string Name,
    string Column,
    string Label,
    FieldType Type,
    bool Required = false,
    bool Unique = false,
    int MaxLength = 0,
    Regex? Pattern = null,
    SearchMode Search = SearchMode.None,
    OptionSource Options = OptionSource.None,
    string? Placeholder = null,
    string? Hint = null,
    bool SuggestQuotations = false,
    string[]? Mimes = null,
    string[]? Exts = null,
    long Max = 0);

public sealed record EntityDef(
    string Key,          // interner Schlüssel, z. B. "quotation"
    string Table,        // SQLite-Tabelle
    string Slug,         // URL, z. B. "quotation"
    string Nav,          // Menütext
    string Singular,     // "Quotation"
    string KeyField,     // fachliche Nummer, z. B. "qNo"
    IReadOnlyList<FieldDef> Fields)
{
    public FieldDef? FileField => Fields.FirstOrDefault(f => f.Type == FieldType.File);
    public FieldDef DateField => Fields.First(f => f.Type == FieldType.Date);
    public FieldDef KeyDef => Fields.First(f => f.Name == KeyField);
    public IEnumerable<FieldDef> DataFields => Fields.Where(f => f.Type != FieldType.File);
}

/// <summary>Fachliches Datenmodell – entspricht js/schema.js der Web-Version (Import-kompatibel).</summary>
public static class Schema
{
    private static readonly Regex Ref = new(@"^[A-Za-z0-9][A-Za-z0-9 ._\-/]*$", RegexOptions.Compiled);
    private static readonly string[] PdfMimes = { "application/pdf" };
    private static readonly string[] PdfExts = { "pdf" };
    private static readonly string[] ImgMimes = { "image/png", "image/jpeg", "image/webp" };
    private static readonly string[] ImgExts = { "png", "jpg", "jpeg", "webp" };

    public static readonly EntityDef Quotation = new("quotation", "quotations", "quotation", "Quotation Entry", "Quotation", "qNo", new FieldDef[]
    {
        new("qDate", "q_date", "Quotation Date", FieldType.Date, Required: true),
        new("qNo", "q_no", "Quotation No", FieldType.Text, Required: true, Unique: true, MaxLength: 40, Pattern: Ref, Search: SearchMode.Contains, Placeholder: "e.g. QT-2026-0815"),
        new("customer", "customer", "Customer", FieldType.Select, Required: true, Search: SearchMode.Equals, Options: OptionSource.Customers),
        new("status", "status", "Status", FieldType.Select, Required: true, Search: SearchMode.Equals, Options: OptionSource.QuotationStatuses),
        new("value", "value", "Value", FieldType.Money, Required: true),
        new("attachment", "attachment_id", "Quotation PDF", FieldType.File, Mimes: PdfMimes, Exts: PdfExts),
    });

    public static readonly EntityDef PoGermany = new("poGermany", "po_germany", "po-germany", "PO Germany Entry", "German PO", "poNo", new FieldDef[]
    {
        new("poDate", "po_date", "PO Date", FieldType.Date, Required: true),
        new("poNo", "po_no", "German PO No", FieldType.Text, Required: true, Unique: true, MaxLength: 40, Pattern: Ref, Search: SearchMode.Contains),
        new("customer", "customer", "Customer", FieldType.Select, Required: true, Search: SearchMode.Equals, Options: OptionSource.Customers),
        new("status", "status", "Status", FieldType.Select, Required: true, Search: SearchMode.Equals, Options: OptionSource.PoStatuses),
        new("value", "value", "Value", FieldType.Money, Required: true),
        new("attachment", "attachment_id", "PO PDF", FieldType.File, Mimes: PdfMimes, Exts: PdfExts),
    });

    public static readonly EntityDef PoCustomer = new("poCustomer", "po_customer", "po-customer", "PO Customer Entry", "Customer PO", "poNo", new FieldDef[]
    {
        new("poDate", "po_date", "PO Date", FieldType.Date, Required: true),
        new("qNo", "q_no", "Quotation No", FieldType.Text, MaxLength: 40, Pattern: Ref, Search: SearchMode.Contains, SuggestQuotations: true, Hint: "Links the PO to a quotation – customer is filled in automatically."),
        new("poNo", "po_no", "Customer PO No", FieldType.Text, Required: true, Unique: true, MaxLength: 40, Pattern: Ref, Search: SearchMode.Contains),
        new("customer", "customer", "Customer", FieldType.Select, Required: true, Search: SearchMode.Equals, Options: OptionSource.Customers),
        new("status", "status", "Status", FieldType.Select, Required: true, Search: SearchMode.Equals, Options: OptionSource.PoStatuses),
        new("value", "value", "Value", FieldType.Money, Required: true),
        new("attachment", "attachment_id", "PO PDF", FieldType.File, Mimes: PdfMimes, Exts: PdfExts),
    });

    public static readonly EntityDef Stock = new("stock", "stock", "stock", "CT India Stocks", "Stock item", "partId", new FieldDef[]
    {
        new("stockDate", "stock_date", "Stock Update Date", FieldType.Date, Required: true),
        new("partId", "part_id", "Part ID", FieldType.Text, Required: true, Unique: true, MaxLength: 40, Pattern: Ref, Search: SearchMode.Contains),
        new("description", "description", "Part Description", FieldType.Text, Required: true, MaxLength: 200, Search: SearchMode.Contains),
        new("qty", "qty", "Quantity", FieldType.Int, Required: true, Max: 1_000_000),
        new("attachment", "attachment_id", "Image", FieldType.File, Mimes: ImgMimes, Exts: ImgExts),
    });

    public static readonly IReadOnlyList<EntityDef> All = new[] { Quotation, PoGermany, PoCustomer, Stock };

    public static EntityDef? BySlug(string? slug) => All.FirstOrDefault(e => e.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    public static EntityDef ByKey(string key) => All.First(e => e.Key == key);

    /// <summary>Sammlungsname der Web-Version (für den JSON-Import)</summary>
    public static string JsonCollection(EntityDef e) => e.Key switch
    {
        "quotation" => "quotations",
        _ => e.Key
    };
}
