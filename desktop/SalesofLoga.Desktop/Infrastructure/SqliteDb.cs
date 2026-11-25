using System.Runtime.InteropServices;
using System.Text;
using N = SalesofLoga.Desktop.Infrastructure.SqliteNative;

namespace SalesofLoga.Desktop.Infrastructure;

public sealed class SqliteException : Exception
{
    public int Code { get; }
    public SqliteException(int code, string message) : base($"SQLite error {code}: {message}") { Code = code; }
    public bool IsConstraint => (Code & 0xFF) == N.SQLITE_CONSTRAINT;
}

/// <summary>Eine geöffnete SQLite-Verbindung. Nicht thread-safe – pro Vorgang öffnen und schließen.</summary>
public sealed class SqliteConnection : IDisposable
{
    private IntPtr _db;

    public static string LibraryVersion
    {
        get { N.EnsureResolver(); return Marshal.PtrToStringUTF8(N.libversion()) ?? "?"; }
    }

    public SqliteConnection(string path)
    {
        N.EnsureResolver();
        var rc = N.open_v2(Utf8z(path), out _db, N.SQLITE_OPEN_READWRITE | N.SQLITE_OPEN_CREATE | N.SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != N.SQLITE_OK)
        {
            var msg = _db != IntPtr.Zero ? Marshal.PtrToStringUTF8(N.errmsg(_db)) : "open failed";
            if (_db != IntPtr.Zero) N.close_v2(_db);
            _db = IntPtr.Zero;
            throw new SqliteException(rc, $"{msg} ({path})");
        }
        N.busy_timeout(_db, 8000);
        Execute("PRAGMA foreign_keys = ON;");
    }

    internal IntPtr Handle => _db;

    private static byte[] Utf8z(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref b, b.Length + 1);
        return b;
    }

    private void Check(int rc)
    {
        if (rc == N.SQLITE_OK || rc == N.SQLITE_ROW || rc == N.SQLITE_DONE) return;
        throw new SqliteException(N.extended_errcode(_db), Marshal.PtrToStringUTF8(N.errmsg(_db)) ?? "unknown");
    }

    private IntPtr Prepare(string sql, IReadOnlyDictionary<string, object?>? args)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        Check(N.prepare_v2(_db, bytes, bytes.Length, out var stmt, IntPtr.Zero));
        if (args == null) return stmt;
        try
        {
            foreach (var (key, value) in args)
            {
                var idx = N.bind_parameter_index(stmt, Utf8z(key.StartsWith('@') || key.StartsWith(':') || key.StartsWith('$') ? key : "@" + key));
                if (idx == 0) continue; // Parameter wird in diesem Statement nicht verwendet
                int rc = value switch
                {
                    null => N.bind_null(stmt, idx),
                    string s => BindText(stmt, idx, s),
                    byte[] blob => N.bind_blob(stmt, idx, blob, blob.Length, N.SQLITE_TRANSIENT),
                    bool bo => N.bind_int64(stmt, idx, bo ? 1 : 0),
                    int i => N.bind_int64(stmt, idx, i),
                    long l => N.bind_int64(stmt, idx, l),
                    double d => N.bind_double(stmt, idx, d),
                    decimal m => N.bind_double(stmt, idx, (double)m),
                    DateTimeOffset dto => BindText(stmt, idx, dto.ToString("O")),
                    _ => BindText(stmt, idx, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")
                };
                Check(rc);
            }
        }
        catch
        {
            N.finalize(stmt);
            throw;
        }
        return stmt;
    }

    private static int BindText(IntPtr stmt, int idx, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return N.bind_text(stmt, idx, b, b.Length, N.SQLITE_TRANSIENT);
    }

    /// <summary>Führt ein oder mehrere Statements ohne Ergebnis aus.</summary>
    public int Execute(string sql, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (args == null && sql.Count(c => c == ';') > 1)
        {
            // Mehrere Statements (Schema-Skripte) nacheinander ausführen
            foreach (var part in SplitStatements(sql)) Execute(part);
            return 0;
        }
        var stmt = Prepare(sql, args);
        try
        {
            int rc;
            while ((rc = N.step(stmt)) == N.SQLITE_ROW) { }
            Check(rc);
            return N.changes(_db);
        }
        finally { N.finalize(stmt); }
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        // Einfache Aufteilung für Schema-Skripte (keine Semikolons in Strings/Triggern verwenden)
        foreach (var s in sql.Split(';'))
        {
            var t = s.Trim();
            if (t.Length > 0) yield return t + ";";
        }
    }

    public List<T> Query<T>(string sql, IReadOnlyDictionary<string, object?>? args, Func<SqliteRow, T> map)
    {
        var stmt = Prepare(sql, args);
        var list = new List<T>();
        try
        {
            var row = new SqliteRow(stmt);
            int rc;
            while ((rc = N.step(stmt)) == N.SQLITE_ROW) list.Add(map(row));
            Check(rc);
        }
        finally { N.finalize(stmt); }
        return list;
    }

    public object? Scalar(string sql, IReadOnlyDictionary<string, object?>? args = null)
        => Query(sql, args, r => r.Value(0)).FirstOrDefault();

    /// <summary>Schreibtransaktion (BEGIN IMMEDIATE sperrt sofort für andere Schreiber).</summary>
    public T InTransaction<T>(Func<T> work)
    {
        Execute("BEGIN IMMEDIATE;");
        try
        {
            var result = work();
            Execute("COMMIT;");
            return result;
        }
        catch
        {
            try { Execute("ROLLBACK;"); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Konsistente Online-Sicherung in eine neue Datei (sqlite3_backup_* API).</summary>
    public void BackupTo(string destinationPath)
    {
        using var dest = new SqliteConnection(destinationPath);
        var main = Utf8z("main");
        var bk = N.backup_init(dest._db, main, _db, main);
        if (bk == IntPtr.Zero) throw new SqliteException(N.extended_errcode(dest._db), Marshal.PtrToStringUTF8(N.errmsg(dest._db)) ?? "backup_init failed");
        int rc;
        do { rc = N.backup_step(bk, 256); if (rc == N.SQLITE_BUSY) Thread.Sleep(50); }
        while (rc == N.SQLITE_OK || rc == N.SQLITE_BUSY);
        var fin = N.backup_finish(bk);
        if (rc != N.SQLITE_DONE || fin != N.SQLITE_OK) throw new SqliteException(fin, "backup failed");
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero) { N.close_v2(_db); _db = IntPtr.Zero; }
    }
}

public sealed class SqliteRow
{
    private readonly IntPtr _stmt;
    internal SqliteRow(IntPtr stmt) { _stmt = stmt; }

    public int Count => N.column_count(_stmt);
    public string Name(int i) => Marshal.PtrToStringUTF8(N.column_name(_stmt, i)) ?? "";
    public bool IsNull(int i) => N.column_type(_stmt, i) == N.SQLITE_NULL;

    public object? Value(int i) => N.column_type(_stmt, i) switch
    {
        N.SQLITE_INTEGER => N.column_int64(_stmt, i),
        N.SQLITE_FLOAT => N.column_double(_stmt, i),
        N.SQLITE_TEXT => Text(i),
        N.SQLITE_BLOB => Blob(i),
        _ => null
    };

    public string? Text(int i)
    {
        if (IsNull(i)) return null;
        var p = N.column_text(_stmt, i);
        var len = N.column_bytes(_stmt, i);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p, len);
    }

    public long? Int64(int i) => IsNull(i) ? null : N.column_int64(_stmt, i);

    public byte[]? Blob(int i)
    {
        if (IsNull(i)) return null;
        var p = N.column_blob(_stmt, i);
        var len = N.column_bytes(_stmt, i);
        var buf = new byte[len];
        if (len > 0) Marshal.Copy(p, buf, 0, len);
        return buf;
    }

    /// <summary>Spaltenindex per Name (für SELECT * Abfragen).</summary>
    public Dictionary<string, int> Columns()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Count; i++) d[Name(i)] = i;
        return d;
    }
}
