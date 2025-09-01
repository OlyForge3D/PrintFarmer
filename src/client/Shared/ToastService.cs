using System.Collections.Concurrent;

namespace Farm.Web.Client;

public class ToastService
{
    public event Action? OnChanged;

    public record ToastItem(Guid Id, string Message, string Type);

    private readonly List<ToastItem> items = new();
    public IReadOnlyList<ToastItem> Toasts => items;

    public void Success(string message, int ttlMs = 3500) => Add(message, "success", ttlMs);
    public void Error(string message, int ttlMs = 5000) => Add(message, "error", ttlMs);

    private void Add(string message, string type, int ttlMs)
    {
        var t = new ToastItem(Guid.NewGuid(), message, type);
        items.Add(t);
        OnChanged?.Invoke();
        _ = AutoDismissAsync(t.Id, ttlMs);
    }

    public void Dismiss(Guid id)
    {
        var idx = items.FindIndex(x => x.Id == id);
        if (idx >= 0)
        {
            items.RemoveAt(idx);
            OnChanged?.Invoke();
        }
    }

    private async Task AutoDismissAsync(Guid id, int ttlMs)
    {
        try
        {
            await Task.Delay(ttlMs);
            Dismiss(id);
        }
        catch { }
    }
}
