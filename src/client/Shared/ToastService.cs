namespace Farm.Web.Client;

public class ToastService
{
    // Use EventHandler to satisfy CA1003
    public event EventHandler? OnChanged;

    private readonly List<ToastItem> _items = new();
    public IReadOnlyList<ToastItem> Toasts => _items;

    public void Success(string message, int ttlMs = 3500) => Add(message, "success", ttlMs);
    public void Error(string message, int ttlMs = 5000) => Add(message, "error", ttlMs);
    public void Warning(string message, int ttlMs = 3500) => Add(message, "warning", ttlMs);

    private void Add(string message, string type, int ttlMs)
    {
        var t = new ToastItem(Guid.NewGuid(), message, type);
    _items.Add(t);
        OnChanged?.Invoke(this, EventArgs.Empty);
        _ = AutoDismissAsync(t.Id, ttlMs);
    }

    public void Dismiss(Guid id)
    {
        var idx = _items.FindIndex(x => x.Id == id);
        if (idx >= 0)
        {
            _items.RemoveAt(idx);
            OnChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AutoDismissAsync(Guid id, int ttlMs)
    {
        try
        {
            await Task.Delay(ttlMs);
            Dismiss(id);
        }
#pragma warning disable CA1031 // Intentionally ignore all exceptions in fire-and-forget operation
        catch { }
#pragma warning restore CA1031
    }
}
