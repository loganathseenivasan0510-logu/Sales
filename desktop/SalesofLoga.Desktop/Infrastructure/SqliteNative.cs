using System.Reflection;
using System.Runtime.InteropServices;

namespace SalesofLoga.Desktop.Infrastructure;

/// <summary>
/// Minimaler P/Invoke-Zugriff auf die SQLite-C-API.
/// Windows: das mit Windows 10/11 ausgelieferte System32\winsqlite3.dll (keine Fremd-DLL nötig).
/// Linux/macOS (Entwicklung/Tests): libsqlite3 des Systems.
/// </summary>
internal static class SqliteNative
{
    private const string Lib = "sqlite3";

    public const int SQLITE_OK = 0;
    public const int SQLITE_BUSY = 5;
    public const int SQLITE_CONSTRAINT = 19;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;

    public const int SQLITE_OPEN_READWRITE = 0x00000002;
    public const int SQLITE_OPEN_CREATE = 0x00000004;
    public const int SQLITE_OPEN_FULLMUTEX = 0x00010000;

    public const int SQLITE_INTEGER = 1;
    public const int SQLITE_FLOAT = 2;
    public const int SQLITE_TEXT = 3;
    public const int SQLITE_BLOB = 4;
    public const int SQLITE_NULL = 5;

    public static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    private static int _resolverSet;

    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "winsqlite3.dll", "sqlite3.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so" };
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, assembly, DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories, out var h))
                return h;
            if (NativeLibrary.TryLoad(c, out h))
                return h;
        }
        throw new DllNotFoundException("SQLite library not found (expected winsqlite3.dll on Windows 10/11).");
    }

    [DllImport(Lib, EntryPoint = "sqlite3_libversion")] public static extern IntPtr libversion();
    [DllImport(Lib, EntryPoint = "sqlite3_open_v2")] public static extern int open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, EntryPoint = "sqlite3_close_v2")] public static extern int close_v2(IntPtr db);
    [DllImport(Lib, EntryPoint = "sqlite3_errmsg")] public static extern IntPtr errmsg(IntPtr db);
    [DllImport(Lib, EntryPoint = "sqlite3_extended_errcode")] public static extern int extended_errcode(IntPtr db);
    [DllImport(Lib, EntryPoint = "sqlite3_busy_timeout")] public static extern int busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, EntryPoint = "sqlite3_changes")] public static extern int changes(IntPtr db);

    [DllImport(Lib, EntryPoint = "sqlite3_prepare_v2")] public static extern int prepare_v2(IntPtr db, byte[] sql, int nBytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, EntryPoint = "sqlite3_step")] public static extern int step(IntPtr stmt);
    [DllImport(Lib, EntryPoint = "sqlite3_finalize")] public static extern int finalize(IntPtr stmt);
    [DllImport(Lib, EntryPoint = "sqlite3_bind_parameter_index")] public static extern int bind_parameter_index(IntPtr stmt, byte[] name);
    [DllImport(Lib, EntryPoint = "sqlite3_bind_null")] public static extern int bind_null(IntPtr stmt, int index);
    [DllImport(Lib, EntryPoint = "sqlite3_bind_int64")] public static extern int bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib, EntryPoint = "sqlite3_bind_double")] public static extern int bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib, EntryPoint = "sqlite3_bind_text")] public static extern int bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    [DllImport(Lib, EntryPoint = "sqlite3_bind_blob")] public static extern int bind_blob(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);

    [DllImport(Lib, EntryPoint = "sqlite3_column_count")] public static extern int column_count(IntPtr stmt);
    [DllImport(Lib, EntryPoint = "sqlite3_column_name")] public static extern IntPtr column_name(IntPtr stmt, int col);
    [DllImport(Lib, EntryPoint = "sqlite3_column_type")] public static extern int column_type(IntPtr stmt, int col);
    [DllImport(Lib, EntryPoint = "sqlite3_column_int64")] public static extern long column_int64(IntPtr stmt, int col);
    [DllImport(Lib, EntryPoint = "sqlite3_column_double")] public static extern double column_double(IntPtr stmt, int col);
    [DllImport(Lib, EntryPoint = "sqlite3_column_text")] public static extern IntPtr column_text(IntPtr stmt, int col);
    [DllImport(Lib, EntryPoint = "sqlite3_column_blob")] public static extern IntPtr column_blob(IntPtr stmt, int col);
    [DllImport(Lib, EntryPoint = "sqlite3_column_bytes")] public static extern int column_bytes(IntPtr stmt, int col);

    [DllImport(Lib, EntryPoint = "sqlite3_backup_init")] public static extern IntPtr backup_init(IntPtr dest, byte[] destName, IntPtr source, byte[] sourceName);
    [DllImport(Lib, EntryPoint = "sqlite3_backup_step")] public static extern int backup_step(IntPtr backup, int pages);
    [DllImport(Lib, EntryPoint = "sqlite3_backup_finish")] public static extern int backup_finish(IntPtr backup);
}
