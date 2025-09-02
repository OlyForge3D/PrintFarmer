using Xunit;

namespace Farm.Web.Client.Tests;

public class ToastServiceTests
{
    [Fact]
    public async Task Add_And_Dismiss_Raise_OnChangedAsync()
    {
        // Arrange
        var svc = new ToastService();
        var fired = 0;
        void Handler(object? _, EventArgs __) => fired++;
        svc.OnChanged += Handler;

        // Act
        svc.Success("hello", ttlMs: 50);

        // Assert: Add fired once and item present
        Assert.Equal(1, fired);
        Assert.Single(svc.Toasts);

        // Wait for auto-dismiss
        await Task.Delay(100);
        // Give the background dismissal a tick to invoke the event
        await Task.Yield();

        // Assert: Dismiss fired and list empty
        Assert.True(fired >= 2);
        Assert.Empty(svc.Toasts);

        // Cleanup
        svc.OnChanged -= Handler;
    }

    [Fact]
    public async Task Warning_Adds_WarningToast_And_Raises_OnChangedAsync()
    {
        var svc = new ToastService();
        var fired = 0;
        void Handler(object? _, EventArgs __) => fired++;
        svc.OnChanged += Handler;

        svc.Warning("be careful", ttlMs: 1_000); // long enough to avoid auto-dismiss during assertions

        Assert.Equal(1, fired);
        Assert.Single(svc.Toasts);
        Assert.Equal("be careful", svc.Toasts[0].Message);
        Assert.Equal("warning", svc.Toasts[0].Type);

        // Cleanup: ensure no auto-dismiss interference; cancel by manual dismiss
        svc.Dismiss(svc.Toasts[0].Id);
        await Task.Yield();
        svc.OnChanged -= Handler;
    }

    [Fact]
    public async Task ManualDismiss_Removes_And_Raises_OnChangedAsync()
    {
        var svc = new ToastService();
        var fired = 0;
        void Handler(object? _, EventArgs __) => fired++;
        svc.OnChanged += Handler;

        // Add with long TTL to prevent auto-dismiss while we test manual dismiss
        svc.Success("bye", ttlMs: 5_000);
        Assert.Equal(1, fired);
        Assert.Single(svc.Toasts);

        var id = svc.Toasts[0].Id;
        fired = 0; // reset to capture manual dismiss event only

        svc.Dismiss(id);
        await Task.Yield();

        Assert.Equal(1, fired);
        Assert.Empty(svc.Toasts);

        svc.OnChanged -= Handler;
    }

    [Fact]
    public void Error_Adds_ErrorToast_And_Raises_OnChanged()
    {
        var svc = new ToastService();
        var fired = 0;
        void Handler(object? _, EventArgs __) => fired++;
        svc.OnChanged += Handler;

        svc.Error("boom", ttlMs: 5_000);

        Assert.Equal(1, fired);
        Assert.Single(svc.Toasts);
        Assert.Equal("boom", svc.Toasts[0].Message);
        Assert.Equal("error", svc.Toasts[0].Type);

        // Cleanup
        svc.Dismiss(svc.Toasts[0].Id);
        svc.OnChanged -= Handler;
    }
}
