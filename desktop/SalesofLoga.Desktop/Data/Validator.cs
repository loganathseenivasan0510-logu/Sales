using System.Globalization;
using System.Text.RegularExpressions;

namespace SalesofLoga.Desktop.Data;

/// <summary>Serverseitige Validierung – identische Regeln wie die Web-Version, plus Prüfung der Datei-Signatur.</summary>
public static class Validator
{
    private static readonly Regex DateRx = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex MoneyRx = new(@"^\d{1,13}(\.\d{1,2})?$", RegexOptions.Compiled);
    private static readonly Regex IntRx = new(@"^\d{1,9}$", RegexOptions.Compiled);

    /// <summary>
    /// Prüft Rohwerte aus dem Formular. Eindeutigkeit wird über <paramref name="isDuplicate"/> geprüft
    /// (in der Schreibtransaktion), damit keine Race Condition entsteht.
    /// </summary>
    public static Dictionary<string, string> Validate(
        EntityDef e, IReadOnlyDictionary<string, string> raw, string? currency, NewAttachment? file,
        MasterData md, Func<FieldDef, string, string?> isDuplicate, SalesRecord? existing)
    {
        var errors = new Dictionary<string, string>();
        foreach (var f in e.Fields)
        {
            if (f.Type == FieldType.File)
            {
                if (file == null) continue;
                var err = CheckFile(f, file, md.MaxAttachmentMB);
                if (err != null) errors[f.Name] = err;
                continue;
            }

            var v = (raw.TryGetValue(f.Name, out var s) ? s : "")?.Trim() ?? "";
            if (v.Length == 0)
            {
                if (f.Required) errors[f.Name] = "Required";
                continue;
            }

            string? error = f.Type switch
            {
                FieldType.Date => !DateRx.IsMatch(v) || !DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? "Invalid date"
                    : d.Year < 2000 || d.Year > 2100 ? "Date out of range" : null,
                FieldType.Money => !MoneyRx.IsMatch(v) ? "Positive amount, max. 2 decimals, no thousands separators"
                    : !md.Currencies.Contains(currency ?? "") ? "Invalid currency" : null,
                FieldType.Int => !IntRx.IsMatch(v) ? "Whole number ≥ 0"
                    : f.Max > 0 && long.Parse(v, CultureInfo.InvariantCulture) > f.Max ? $"Max. {f.Max}" : null,
                FieldType.Select => md.OptionsFor(f.Options).Contains(v) || (existing != null && existing.Text(f.Name) == v) ? null : "Invalid selection",
                _ => f.MaxLength > 0 && v.Length > f.MaxLength ? $"Max. {f.MaxLength} characters"
                    : f.Pattern != null && !f.Pattern.IsMatch(v) ? "Only letters, digits, space and . _ - / allowed"
                    : v.Any(char.IsControl) ? "Control characters are not allowed" : null
            };
            if (error == null && f.Unique)
            {
                var owner = isDuplicate(f, v);
                if (owner != null) error = $"{f.Label} already exists (created by {owner})";
            }
            if (error != null) errors[f.Name] = error;
        }
        return errors;
    }

    /// <summary>Endung, MIME-Typ, Größe UND Dateisignatur (Magic Bytes) müssen passen.</summary>
    public static string? CheckFile(FieldDef f, NewAttachment file, int maxMb)
    {
        var ext = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
        if (f.Exts != null && !f.Exts.Contains(ext)) return $"Only {string.Join(", ", f.Exts).ToUpperInvariant()} files are allowed.";
        if (file.Data.Length == 0) return "File is empty.";
        if (file.Data.LongLength > maxMb * 1024L * 1024L) return $"File is larger than {maxMb} MB.";
        var detected = DetectMime(file.Data);
        if (detected == null || (f.Mimes != null && !f.Mimes.Contains(detected)))
            return "File content does not match the file type (signature check failed).";
        return null;
    }

    public static string? DetectMime(byte[] d)
    {
        bool Starts(params byte[] sig) => d.Length >= sig.Length && sig.Select((b, i) => d[i] == b).All(x => x);
        if (Starts(0x25, 0x50, 0x44, 0x46, 0x2D)) return "application/pdf";                 // %PDF-
        if (Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
        if (Starts(0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (d.Length >= 12 && Starts(0x52, 0x49, 0x46, 0x46) && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "image/webp";
        return null;
    }

    public static decimal ParseMoney(string v) => decimal.Parse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
