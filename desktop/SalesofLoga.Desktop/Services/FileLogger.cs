using System.Collections.Concurrent;
using System.Text;

namespace SalesofLoga.Desktop.Services;

/// <summary>
/// Schlankes Datei-Logging ohne Fremdpakete: eine Datei pro Tag (logs\salesofloga-YYYYMMDD.log),
/// Aufbewahrung 14 Tage. Wird zusätzlich zur Konsole verwendet und in der App unter Settings → Logs angezeigt.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10000);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Cleanup();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_dir, $"salesofloga-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(n, this));

    internal void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* Logging darf die App nie stoppen */ }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "salesofloga-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { /* ignore */ }
    }

    /// <summary>Letzte Zeilen der aktuellen Logdatei (für die Anzeige in der App).</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        try
        {
            if (!File.Exists(CurrentFile)) return Array.Empty<string>();
            using var fs = new FileStream(CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var all = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) all.Add(l);
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch (Exception ex) { return new[] { $"(log could not be read: {ex.Message})" }; }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _p;
        public FileLogger(string category, FileLoggerProvider p)
        {
            _category = category.StartsWith("SalesofLoga.Desktop.") ? category["SalesofLoga.Desktop.".Length..] : category;
            _p = p;
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var lvl = level switch { LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => level.ToString() };
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(lvl).Append(' ')
              .Append('[').Append(_category).Append("] ").Append(formatter(state, ex));
            if (ex != null) sb.AppendLine().Append(ex);
            _p.Enqueue(sb.ToString());
        }
    }
}
