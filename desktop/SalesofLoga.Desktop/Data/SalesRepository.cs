using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SalesofLoga.Desktop.Infrastructure;
using SalesofLoga.Desktop.Services;

namespace SalesofLoga.Desktop.Data;

/// <summary>
/// Datenzugriff auf die SQLite-Datei. Alle SQL-Statements sind parametrisiert; Tabellen- und
/// Spaltennamen stammen ausschließlich aus <see cref="Schema"/> (nie aus Benutzereingaben).
/// Jede Änderung läuft in einer Transaktion zusammen mit ihrem Audit-Eintrag.
/// </summary>
public sealed class SalesRepository
{
    public const int SchemaVersion = 1;
    public const int ApplicationId = 0x534C4741; // "SLGA" – kennzeichnet die Datei als SalesofLoga-DB

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dbPath;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string DbPath => _dbPath;

    public SalesRepository(string dbPath, ILogger log)
    {
        _dbPath = dbPath;
        _log = log;
    }

    private SqliteConnection Open() => new(_dbPath);
    private static Dictionary<string, object?> P(params (string, object?)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ Schema

    public void Initialize()
    {
        using var c = Open();
        var appId = Convert.ToInt64(c.Scalar("PRAGMA application_id;") ?? 0L);
        var version = Convert.ToInt64(c.Scalar("PRAGMA user_version;") ?? 0L);
        var tables = Convert.ToInt64(c.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table';") ?? 0L);
        if (tables > 0 && appId != ApplicationId)
            throw new InvalidOperationException($"{_dbPath} is not a SalesofLoga database (application_id {appId}).");
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Database schema {version} is newer than this app ({SchemaVersion}). Please update the app.");

        c.Execute("PRAGMA journal_mode = WAL;");
        c.Execute("PRAGMA synchronous = NORMAL;");
        if (version == SchemaVersion) return;

        c.InTransaction(() =>
        {
            c.Execute("""
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS attachments (
                    id TEXT PRIMARY KEY, entity TEXT NOT NULL, record_id TEXT, file_name TEXT NOT NULL, mime TEXT NOT NULL,
                    size INTEGER NOT NULL CHECK (size >= 0), sha256 TEXT NOT NULL, data BLOB NOT NULL,
                    uploaded_at TEXT NOT NULL, uploaded_by_id TEXT NOT NULL, uploaded_by_name TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, user_id TEXT NOT NULL, user_name TEXT NOT NULL,
                    action TEXT NOT NULL, entity TEXT NOT NULL, record_id TEXT, label TEXT, changes TEXT);
                CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit(ts);
                """);
            foreach (var e in Schema.All) c.Execute(TableDdl(e));
            c.Execute($"PRAGMA application_id = {ApplicationId};");
            c.Execute($"PRAGMA user_version = {SchemaVersion};");
            return 0;
        });
        _log.LogInformation("Database schema v{Version} created at {Path}", SchemaVersion, _dbPath);
    }

    private static string TableDdl(EntityDef e)
    {
        var cols = new List<string> { "id TEXT PRIMARY KEY" };
        foreach (var f in e.Fields)
        {
            var nn = f.Required ? " NOT NULL" : "";
            cols.Add(f.Type switch
            {
                FieldType.Money => $"value_minor INTEGER{nn} CHECK (value_minor >= 0), currency TEXT{nn}",
                FieldType.Int => $"{f.Column} INTEGER{nn} CHECK ({f.Column} >= 0)",
                FieldType.File => $"{f.Column} TEXT REFERENCES attachments(id)",
                _ => $"{f.Column} TEXT{nn}"
            });
        }
        cols.AddRange(new[]
        {
            "created_at TEXT NOT NULL", "created_by_id TEXT NOT NULL", "created_by_name TEXT NOT NULL",
            "updated_at TEXT", "updated_by_id TEXT", "updated_by_name TEXT",
            "deleted_at TEXT", "deleted_by_id TEXT", "deleted_by_name TEXT"
        });
        var key = e.KeyDef.Column;
        var view = string.Join(", ", e.Fields.Select(f => f.Type switch
        {
            FieldType.Money => "t.value_minor / 100.0 AS value, t.currency",
            FieldType.File => "a.file_name AS attachment_name",
            _ => $"t.{f.Column}"
        }));
        return $"""
            CREATE TABLE IF NOT EXISTS {e.Table} ({string.Join(", ", cols)});
            CREATE UNIQUE INDEX IF NOT EXISTS ux_{e.Table}_{key} ON {e.Table}({key} COLLATE NOCASE) WHERE deleted_at IS NULL;
            CREATE INDEX IF NOT EXISTS ix_{e.Table}_date ON {e.Table}({e.DateField.Column});
            CREATE VIEW IF NOT EXISTS v_{e.Table} AS SELECT t.id, {view}, t.created_by_name, t.created_at, t.updated_by_name, t.updated_at
                FROM {e.Table} t LEFT JOIN attachments a ON a.id = t.attachment_id WHERE t.deleted_at IS NULL;
            """;
    }

    // ------------------------------------------------------------------ Stammdaten

    public MasterData GetMaster()
    {
        using var c = Open();
        var json = c.Scalar("SELECT value FROM settings WHERE key = 'master';") as string;
        if (json == null) return new MasterData();
        try { return JsonSerializer.Deserialize<MasterData>(json) ?? new MasterData(); }
        catch (Exception ex) { _log.LogError(ex, "Master data unreadable – using defaults"); return new MasterData(); }
    }

    public void SaveMaster(MasterData md, AppUser user)
    {
        using var c = Open();
        c.InTransaction(() =>
        {
            c.Execute("INSERT INTO settings(key, value, updated_at, updated_by) VALUES('master', @v, @t, @u) ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at, updated_by = excluded.updated_by;",
                P(("v", JsonSerializer.Serialize(md, Json)), ("t", Now()), ("u", user.Name)));
            WriteAudit(c, user, "settings", "settings", null, "Master data", null);
            return 0;
        });
        _log.LogInformation("Master data changed by {User}", user.Account);
    }

    public bool IsAdmin(AppUser user, MasterData md)
    {
        if (md.Admins.Count == 0) return true; // Einzelplatz-Start: ohne konfigurierte Admins ist der lokale Benutzer Admin
        return md.Admins.Any(a => a.Equals(user.Account, StringComparison.OrdinalIgnoreCase) || (user.Upn.Length > 0 && a.Equals(user.Upn, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Ersteller oder Admin. Ohne konfigurierte Admins (Einzelplatz) ist der lokale Benutzer Admin.</summary>
    public bool CanEdit(SalesRecord r, AppUser user, MasterData md) =>
        r.CreatedById.Equals(user.Id, StringComparison.OrdinalIgnoreCase) || IsAdmin(user, md);

    // ------------------------------------------------------------------ Lesen

    public List<SalesRecord> List(EntityDef e, IReadOnlyDictionary<string, string>? filters = null, string? onlyCreatedById = null, int limit = 5000)
    {
        var where = new List<string> { "t.deleted_at IS NULL" };
        var args = new Dictionary<string, object?>();
        if (filters != null)
        {
            foreach (var f in e.Fields.Where(f => f.Search != SearchMode.None))
            {
                if (!filters.TryGetValue(f.Name, out var v) || string.IsNullOrWhiteSpace(v)) continue;
                var p = "f_" + f.Name;
                if (f.Search == SearchMode.Equals) { where.Add($"t.{f.Column} = @{p} COLLATE NOCASE"); args[p] = v.Trim(); }
                else { where.Add($"t.{f.Column} LIKE @{p} ESCAPE '\\'"); args[p] = "%" + EscapeLike(v.Trim()) + "%"; }
            }
        }
        if (onlyCreatedById != null) { where.Add("t.created_by_id = @owner"); args["owner"] = onlyCreatedById; }
        args["limit"] = limit;
        using var c = Open();
        return c.Query($"""
            SELECT t.*, a.file_name AS att_name, a.mime AS att_mime, a.size AS att_size
            FROM {e.Table} t LEFT JOIN attachments a ON a.id = t.attachment_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY t.{e.DateField.Column} DESC, t.created_at DESC LIMIT @limit;
            """, args, r => Map(e, r));
    }

    public SalesRecord? Get(EntityDef e, string id)
    {
        using var c = Open();
        return Get(c, e, id);
    }

    private static SalesRecord? Get(SqliteConnection c, EntityDef e, string id) =>
        c.Query($"SELECT t.*, a.file_name AS att_name, a.mime AS att_mime, a.size AS att_size FROM {e.Table} t LEFT JOIN attachments a ON a.id = t.attachment_id WHERE t.id = @id AND t.deleted_at IS NULL;",
            P(("id", id)), r => Map(e, r)).FirstOrDefault();

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static SalesRecord Map(EntityDef e, SqliteRow row)
    {
        var col = row.Columns();
        string? S(string name) => col.TryGetValue(name, out var i) ? row.Text(i) : null;
        long? L(string name) => col.TryGetValue(name, out var i) ? row.Int64(i) : null;
        var rec = new SalesRecord
        {
            Id = S("id")!,
            CreatedAt = S("created_at") ?? "",
            CreatedById = S("created_by_id") ?? "",
            CreatedByName = S("created_by_name") ?? "",
            UpdatedAt = S("updated_at"),
            UpdatedByName = S("updated_by_name"),
        };
        foreach (var f in e.Fields)
        {
            switch (f.Type)
            {
                case FieldType.Money:
                    var minor = L("value_minor");
                    rec.Values[f.Name] = minor.HasValue ? minor.Value / 100m : null;
                    rec.Currency = S("currency");
                    break;
                case FieldType.Int:
                    rec.Values[f.Name] = L(f.Column);
                    break;
                case FieldType.File:
                    var attId = S(f.Column);
                    if (attId != null) rec.Attachment = new AttachmentInfo(attId, S("att_name") ?? "file", S("att_mime") ?? "application/octet-stream", L("att_size") ?? 0);
                    break;
                default:
                    rec.Values[f.Name] = S(f.Column);
                    break;
            }
        }
        return rec;
    }

    public List<(string QNo, string Customer, string Date)> QuotationLookup()
    {
        using var c = Open();
        return c.Query("SELECT q_no, customer, q_date FROM quotations WHERE deleted_at IS NULL ORDER BY q_date DESC LIMIT 2000;", null,
            r => (r.Text(0) ?? "", r.Text(1) ?? "", r.Text(2) ?? ""));
    }

    public (AttachmentInfo Info, byte[] Data)? GetAttachment(string id)
    {
        using var c = Open();
        return c.Query("SELECT id, file_name, mime, size, data FROM attachments WHERE id = @id;", P(("id", id)),
            r => ((AttachmentInfo, byte[])?)(new AttachmentInfo(r.Text(0)!, r.Text(1)!, r.Text(2)!, r.Int64(3) ?? 0), r.Blob(4) ?? Array.Empty<byte>()))
            .FirstOrDefault();
    }

    public List<AuditEntry> RecentAudit(int n)
    {
        using var c = Open();
        return c.Query("SELECT id, ts, user_name, action, entity, record_id, label, changes FROM audit ORDER BY id DESC LIMIT @n;", P(("n", n)),
            r => new AuditEntry(r.Int64(0) ?? 0, r.Text(1) ?? "", r.Text(2) ?? "", r.Text(3) ?? "", r.Text(4) ?? "", r.Text(5), r.Text(6), r.Text(7)));
    }

    public DashboardStats Stats(MasterData md)
    {
        using var c = Open();
        List<(string, decimal)> Sums(string table, string? where) =>
            c.Query($"SELECT currency, SUM(value_minor) FROM {table} WHERE deleted_at IS NULL {(where != null ? "AND " + where : "")} GROUP BY currency ORDER BY 2 DESC;", null,
                r => (r.Text(0) ?? "", (r.Int64(1) ?? 0) / 100m));
        long Count(string table, string? where) =>
            Convert.ToInt64(c.Scalar($"SELECT count(*) FROM {table} WHERE deleted_at IS NULL {(where != null ? "AND " + where : "")};") ?? 0L);

        var low = c.Query("SELECT part_id, description, qty FROM stock WHERE deleted_at IS NULL AND qty <= @t ORDER BY qty, part_id LIMIT 20;",
            P(("t", md.LowStockThreshold)), r => (r.Text(0) ?? "", r.Text(1) ?? "", r.Int64(2) ?? 0));
        return new DashboardStats(
            (int)Count("quotations", "status = 'Open'"), Sums("quotations", "status = 'Open'"),
            (int)Count("quotations", "status <> 'Open'"), Sums("quotations", "status <> 'Open'"),
            (int)Count("po_customer", null), Sums("po_customer", null),
            (int)Count("po_germany", null), Sums("po_germany", null),
            (int)Count("stock", null), Convert.ToInt64(c.Scalar("SELECT COALESCE(SUM(qty),0) FROM stock WHERE deleted_at IS NULL;") ?? 0L),
            low);
    }

    // ------------------------------------------------------------------ Schreiben

    public async Task<SalesRecord> CreateAsync(EntityDef e, IReadOnlyDictionary<string, string> raw, string? currency, NewAttachment? file, AppUser user)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var c = Open();
            var md = GetMaster();
            var id = Guid.NewGuid().ToString();
            c.InTransaction(() =>
            {
                ThrowIfInvalid(c, e, raw, currency, file, md, null);
                var attId = file != null ? InsertAttachment(c, e, id, file, user) : null;
                var cols = new List<string> { "id", "created_at", "created_by_id", "created_by_name" };
                var args = P(("id", id), ("created_at", Now()), ("created_by_id", user.Id), ("created_by_name", user.Name));
                AddFieldValues(e, raw, currency, attId, cols, args, includeAttachment: true);
                c.Execute($"INSERT INTO {e.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", cols.Select(x => "@" + x))});", args);
                WriteAudit(c, user, "create", e.Key, id, $"{e.Singular} {raw[e.KeyField].Trim()}", null);
                return 0;
            });
            _log.LogInformation("{User} created {Entity} {Key} ({Id})", user.Account, e.Key, raw[e.KeyField].Trim(), id);
            return Get(e, id)!;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<SalesRecord> UpdateAsync(EntityDef e, string id, IReadOnlyDictionary<string, string> raw, string? currency, NewAttachment? file, bool removeAttachment, AppUser user)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var c = Open();
            var md = GetMaster();
            c.InTransaction(() =>
            {
                var existing = Get(c, e, id) ?? throw new InvalidOperationException("This record was deleted in the meantime.");
                if (!CanEdit(existing, user, md))
                {
                    _log.LogWarning("Denied: {User} tried to change {Entity} {Id} owned by {Owner}", user.Account, e.Key, id, existing.CreatedById);
                    throw new PermissionDeniedException("You can only change records you created.");
                }
                ThrowIfInvalid(c, e, raw, currency, file, md, existing);

                var changes = new Dictionary<string, object?[]>();
                foreach (var f in e.DataFields)
                {
                    var newVal = raw.TryGetValue(f.Name, out var s) ? s.Trim() : "";
                    var oldVal = f.Type == FieldType.Money ? existing.Money(f.Name)?.ToString("0.00", CultureInfo.InvariantCulture) ?? "" : existing.Text(f.Name);
                    if (f.Type == FieldType.Money && newVal.Length > 0) newVal = Validator.ParseMoney(newVal).ToString("0.00", CultureInfo.InvariantCulture);
                    if (oldVal != newVal) changes[f.Name] = new object?[] { oldVal, newVal };
                }
                if (e.DataFields.Any(f => f.Type == FieldType.Money) && existing.Currency != currency) changes["currency"] = new object?[] { existing.Currency, currency };

                string? attId = existing.Attachment?.Id;
                if (file != null) { attId = InsertAttachment(c, e, id, file, user); changes["attachment"] = new object?[] { existing.Attachment?.FileName, file.FileName }; }
                else if (removeAttachment && existing.Attachment != null) { attId = null; changes["attachment"] = new object?[] { existing.Attachment.FileName, null }; }

                var cols = new List<string>();
                var args = P(("id", id), ("updated_at", Now()), ("updated_by_id", user.Id), ("updated_by_name", user.Name));
                AddFieldValues(e, raw, currency, attId, cols, args, includeAttachment: true);
                var set = string.Join(", ", cols.Select(x => $"{x} = @{x}").Concat(new[] { "updated_at = @updated_at", "updated_by_id = @updated_by_id", "updated_by_name = @updated_by_name" }));
                c.Execute($"UPDATE {e.Table} SET {set} WHERE id = @id AND deleted_at IS NULL;", args);
                WriteAudit(c, user, "update", e.Key, id, $"{e.Singular} {raw[e.KeyField].Trim()}", changes.Count > 0 ? JsonSerializer.Serialize(changes, Json) : null);
                return 0;
            });
            _log.LogInformation("{User} updated {Entity} {Id}", user.Account, e.Key, id);
            return Get(e, id)!;
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(EntityDef e, string id, AppUser user)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var c = Open();
            var md = GetMaster();
            c.InTransaction(() =>
            {
                var existing = Get(c, e, id);
                if (existing == null) return 0;
                if (!CanEdit(existing, user, md))
                {
                    _log.LogWarning("Denied: {User} tried to delete {Entity} {Id} owned by {Owner}", user.Account, e.Key, id, existing.CreatedById);
                    throw new PermissionDeniedException("You can only delete records you created.");
                }
                c.Execute($"UPDATE {e.Table} SET deleted_at = @t, deleted_by_id = @uid, deleted_by_name = @un WHERE id = @id;",
                    P(("t", Now()), ("uid", user.Id), ("un", user.Name), ("id", id)));
                WriteAudit(c, user, "delete", e.Key, id, $"{e.Singular} {existing.Text(e.KeyField)}", null);
                return 0;
            });
            _log.LogInformation("{User} deleted (soft) {Entity} {Id}", user.Account, e.Key, id);
        }
        finally { _writeLock.Release(); }
    }

    private void ThrowIfInvalid(SqliteConnection c, EntityDef e, IReadOnlyDictionary<string, string> raw, string? currency, NewAttachment? file, MasterData md, SalesRecord? existing)
    {
        var errors = Validator.Validate(e, raw, currency, file, md, (f, v) =>
            c.Query($"SELECT created_by_name FROM {e.Table} WHERE {f.Column} = @v COLLATE NOCASE AND deleted_at IS NULL AND id <> @id LIMIT 1;",
                P(("v", v), ("id", existing?.Id ?? "")), r => r.Text(0)).FirstOrDefault(), existing);
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static void AddFieldValues(EntityDef e, IReadOnlyDictionary<string, string> raw, string? currency, string? attId, List<string> cols, Dictionary<string, object?> args, bool includeAttachment)
    {
        foreach (var f in e.Fields)
        {
            var v = raw.TryGetValue(f.Name, out var s) ? s.Trim() : "";
            switch (f.Type)
            {
                case FieldType.Money:
                    cols.Add("value_minor"); args["value_minor"] = v.Length == 0 ? null : (long)(Validator.ParseMoney(v) * 100m);
                    cols.Add("currency"); args["currency"] = currency;
                    break;
                case FieldType.Int:
                    cols.Add(f.Column); args[f.Column] = v.Length == 0 ? null : long.Parse(v, CultureInfo.InvariantCulture);
                    break;
                case FieldType.File:
                    if (includeAttachment) { cols.Add(f.Column); args[f.Column] = attId; }
                    break;
                default:
                    cols.Add(f.Column); args[f.Column] = v.Length == 0 ? null : v;
                    break;
            }
        }
    }

    private static string InsertAttachment(SqliteConnection c, EntityDef e, string recordId, NewAttachment file, AppUser user)
    {
        var id = Guid.NewGuid().ToString();
        var name = SafeFileName(file.FileName);
        c.Execute("INSERT INTO attachments(id, entity, record_id, file_name, mime, size, sha256, data, uploaded_at, uploaded_by_id, uploaded_by_name) VALUES(@id, @e, @r, @n, @m, @s, @h, @d, @t, @uid, @un);",
            P(("id", id), ("e", e.Key), ("r", recordId), ("n", name), ("m", Validator.DetectMime(file.Data) ?? file.Mime), ("s", (long)file.Data.Length),
              ("h", Convert.ToHexString(SHA256.HashData(file.Data)).ToLowerInvariant()), ("d", file.Data), ("t", Now()), ("uid", user.Id), ("un", user.Name)));
        return id;
    }

    public static string SafeFileName(string name)
    {
        var n = Path.GetFileName(name ?? "file");
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '"', '<', '>', '|', ':', '*', '?', '\\', '/' }).ToHashSet();
        var sb = new StringBuilder();
        foreach (var ch in n) sb.Append(bad.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().Trim().Trim('.');
        return s.Length == 0 ? "file" : s.Length > 120 ? s[..120] : s;
    }

    private static void WriteAudit(SqliteConnection c, AppUser user, string action, string entity, string? recordId, string? label, string? changes) =>
        c.Execute("INSERT INTO audit(ts, user_id, user_name, action, entity, record_id, label, changes) VALUES(@ts, @uid, @un, @a, @e, @r, @l, @c);",
            P(("ts", Now()), ("uid", user.Id), ("un", user.Name), ("a", action), ("e", entity), ("r", recordId), ("l", label), ("c", changes)));

    public void Audit(AppUser user, string action, string label, string? details = null)
    {
        using var c = Open();
        WriteAudit(c, user, action, "system", null, label, details);
    }

    // ------------------------------------------------------------------ Wartung

    public void BackupTo(string destination)
    {
        using var c = Open();
        if (File.Exists(destination)) File.Delete(destination);
        c.BackupTo(destination);
    }

    /// <summary>Einmal pro Tag eine Sicherung anlegen; die letzten <paramref name="keep"/> behalten.</summary>
    public string? DailyBackup(string backupDir, int keep = 30)
    {
        var target = Path.Combine(backupDir, $"salesofloga-{DateTime.Now:yyyy-MM-dd}.db");
        if (File.Exists(target)) return null;
        BackupTo(target);
        foreach (var old in Directory.GetFiles(backupDir, "salesofloga-*.db").OrderByDescending(f => f).Skip(keep))
        {
            try { File.Delete(old); } catch (Exception ex) { _log.LogWarning(ex, "Old backup could not be deleted: {File}", old); }
        }
        _log.LogInformation("Daily backup written: {File}", target);
        return target;
    }

    public (string IntegrityCheck, int ForeignKeyViolations) CheckIntegrity()
    {
        using var c = Open();
        var integrity = string.Join("; ", c.Query("PRAGMA integrity_check;", null, r => r.Text(0) ?? ""));
        var fk = c.Query("PRAGMA foreign_key_check;", null, r => 1).Count;
        return (integrity, fk);
    }

    public long CountRecords(bool includeDeleted)
    {
        using var c = Open();
        return Schema.All.Sum(e => Convert.ToInt64(c.Scalar($"SELECT count(*) FROM {e.Table}{(includeDeleted ? "" : " WHERE deleted_at IS NULL")};") ?? 0L));
    }

    public long CountAudit(string action)
    {
        using var c = Open();
        return Convert.ToInt64(c.Scalar("SELECT count(*) FROM audit WHERE action = @a;", P(("a", action))) ?? 0L);
    }

    // ------------------------------------------------------------------ CSV

    public string ExportCsv(EntityDef e, IReadOnlyDictionary<string, string>? filters, string delimiter = ";")
    {
        var rows = List(e, filters);
        var sb = new StringBuilder("﻿");
        var head = new List<string>();
        foreach (var f in e.Fields)
        {
            if (f.Type == FieldType.File) head.Add($"{f.Label} (file name)");
            else if (f.Type == FieldType.Money) { head.Add(f.Label); head.Add("Currency"); }
            else head.Add(f.Label);
        }
        head.AddRange(new[] { "Created by", "Created at", "Last change by", "Last change at" });
        sb.AppendLine(string.Join(delimiter, head.Select(h => CsvCell(h, delimiter))));
        foreach (var r in rows)
        {
            var cells = new List<string>();
            foreach (var f in e.Fields)
            {
                if (f.Type == FieldType.File) cells.Add(r.Attachment?.FileName ?? "");
                else if (f.Type == FieldType.Money) { cells.Add(r.Money(f.Name)?.ToString("0.00", CultureInfo.InvariantCulture) ?? ""); cells.Add(r.Currency ?? ""); }
                else cells.Add(r.Text(f.Name));
            }
            cells.AddRange(new[] { r.CreatedByName, r.CreatedAt, r.UpdatedByName ?? "", r.UpdatedAt ?? "" });
            sb.AppendLine(string.Join(delimiter, cells.Select(x => CsvCell(x, delimiter))));
        }
        return sb.ToString();
    }

    /// <summary>Schutz gegen CSV-/Formel-Injection in Excel (=, +, -, @, Tab, CR am Zellanfang).</summary>
    public static string CsvCell(string v, string delimiter)
    {
        var s = v ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.Contains('"') || s.Contains('\n') || s.Contains('\r') || s.Contains(delimiter) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ------------------------------------------------------------------ Import aus der Web-Version

    /// <summary>
    /// Übernimmt salesofloga-data.json (+ attachments\) der HTML-Version. Bereits vorhandene IDs werden übersprungen,
    /// Anhänge durchlaufen dieselbe Signaturprüfung wie Uploads.
    /// </summary>
    public async Task<ImportResult> ImportWebJsonAsync(string folder, AppUser user)
    {
        var jsonPath = Directory.Exists(folder) ? Path.Combine(folder, "salesofloga-data.json") : folder;
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
        if (!File.Exists(jsonPath)) throw new FileNotFoundException("salesofloga-data.json not found.", jsonPath);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        var root = doc.RootElement;
        if (root.TryGetProperty("app", out var app) && app.GetString() != "SalesofLoga") throw new InvalidOperationException("Not a SalesofLoga data file.");

        int recs = 0, skipped = 0, atts = 0, rejected = 0, audits = 0;
        var messages = new List<string>();
        var md = GetMaster();

        await _writeLock.WaitAsync();
        try
        {
            using var c = Open();
            c.InTransaction(() =>
            {
                foreach (var e in Schema.All)
                {
                    if (!root.TryGetProperty(Schema.JsonCollection(e), out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        var id = Str(item, "id") ?? Guid.NewGuid().ToString();
                        if (Convert.ToInt64(c.Scalar($"SELECT count(*) FROM {e.Table} WHERE id = @id;", P(("id", id))) ?? 0L) > 0) { skipped++; continue; }

                        var raw = new Dictionary<string, string>();
                        foreach (var f in e.DataFields)
                        {
                            if (!item.TryGetProperty(f.Name, out var p) || p.ValueKind == JsonValueKind.Null) continue;
                            raw[f.Name] = p.ValueKind == JsonValueKind.Number ? p.GetRawText() : p.GetString() ?? "";
                        }
                        var currency = Str(item, "currency") ?? md.DefaultCurrency;
                        var deleted = Str(item, "deletedAt");
                        var errors = Validator.Validate(e, raw, currency, null, md, (f, v) => deleted != null ? null :
                            c.Query($"SELECT created_by_name FROM {e.Table} WHERE {f.Column} = @v COLLATE NOCASE AND deleted_at IS NULL LIMIT 1;", P(("v", v)), r => r.Text(0)).FirstOrDefault(),
                            null);
                        // Unbekannte Auswahlwerte (z. B. Kunde nicht mehr in der Liste) sind beim Import erlaubt
                        foreach (var f in e.Fields.Where(f => f.Type == FieldType.Select)) errors.Remove(f.Name);
                        if (errors.Count > 0) { skipped++; messages.Add($"{e.Singular} {raw.GetValueOrDefault(e.KeyField)}: {string.Join(", ", errors.Values)}"); continue; }

                        string? attId = null;
                        if (item.TryGetProperty("attachment", out var att) && att.ValueKind == JsonValueKind.Object && e.FileField != null)
                        {
                            var rel = Str(att, "path") ?? "";
                            var full = Path.GetFullPath(Path.Combine(baseDir, rel));
                            if (!full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                            {
                                rejected++; messages.Add($"Attachment missing or outside import folder: {rel}");
                            }
                            else
                            {
                                var file = new NewAttachment(Str(att, "name") ?? Path.GetFileName(full), Str(att, "type") ?? "", File.ReadAllBytes(full));
                                var err = Validator.CheckFile(e.FileField, file, md.MaxAttachmentMB);
                                if (err != null) { rejected++; messages.Add($"Attachment rejected ({file.FileName}): {err}"); }
                                else { attId = InsertAttachment(c, e, id, file, user); atts++; }
                            }
                        }

                        var by = item.TryGetProperty("createdBy", out var cb) && cb.ValueKind == JsonValueKind.Object ? cb : default;
                        var cols = new List<string> { "id", "created_at", "created_by_id", "created_by_name", "deleted_at", "deleted_by_name" };
                        var args = P(("id", id), ("created_at", Str(item, "createdAt") ?? Now()),
                            ("created_by_id", by.ValueKind == JsonValueKind.Object ? Str(by, "id") ?? user.Id : user.Id),
                            ("created_by_name", by.ValueKind == JsonValueKind.Object ? Str(by, "name") ?? user.Name : user.Name),
                            ("deleted_at", deleted),
                            ("deleted_by_name", item.TryGetProperty("deletedBy", out var db) && db.ValueKind == JsonValueKind.Object ? Str(db, "name") : null));
                        AddFieldValues(e, raw, currency, attId, cols, args, includeAttachment: true);
                        c.Execute($"INSERT INTO {e.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", cols.Select(x => "@" + x))});", args);
                        recs++;
                    }
                }

                if (root.TryGetProperty("audit", out var audit) && audit.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in audit.EnumerateArray())
                    {
                        var u = a.TryGetProperty("user", out var uu) && uu.ValueKind == JsonValueKind.Object ? uu : default;
                        c.Execute("INSERT INTO audit(ts, user_id, user_name, action, entity, record_id, label, changes) VALUES(@ts, @uid, @un, @a, @e, @r, @l, @c);",
                            P(("ts", Str(a, "ts") ?? Now()), ("uid", u.ValueKind == JsonValueKind.Object ? Str(u, "id") ?? "?" : "?"),
                              ("un", u.ValueKind == JsonValueKind.Object ? Str(u, "name") ?? "?" : "?"), ("a", Str(a, "action") ?? "?"),
                              ("e", Str(a, "entity") ?? "?"), ("r", Str(a, "recordId")), ("l", Str(a, "label")),
                              ("c", a.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Object ? ch.GetRawText() : null)));
                        audits++;
                    }
                }
                WriteAudit(c, user, "import", "all", null, $"Import from {Path.GetFileName(baseDir)}", JsonSerializer.Serialize(new { recs, skipped, atts, rejected }));
                return 0;
            });
        }
        finally { _writeLock.Release(); }

        _log.LogInformation("Import by {User} from {Folder}: {Records} records, {Skipped} skipped, {Attachments} attachments, {Rejected} rejected",
            user.Account, baseDir, recs, skipped, atts, rejected);
        foreach (var m in messages) _log.LogWarning("Import: {Message}", m);
        return new ImportResult(recs, skipped, atts, rejected, audits, messages);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
