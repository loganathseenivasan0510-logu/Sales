namespace SalesofLoga.Desktop.Services;

public sealed record Toast(Guid Id, string Message, string Kind);

/// <summary>Kurzmeldungen pro Browserfenster (Scoped = pro Blazor-Circuit).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _items = new();
    public IReadOnlyList<Toast> Items { get { lock (_items) return _items.ToList(); } }
    public event Action? Changed;

    public void Show(string message, string kind = "info")
    {
        var t = new Toast(Guid.NewGuid(), message, kind);
        lock (_items)
        {
            if (_items.Any(x => x.Message == message)) return;
            _items.Add(t);
        }
        Changed?.Invoke();
        _ = Task.Delay(kind == "error" ? 7000 : 3500).ContinueWith(_ => { lock (_items) _items.Remove(t); Changed?.Invoke(); });
    }
}
