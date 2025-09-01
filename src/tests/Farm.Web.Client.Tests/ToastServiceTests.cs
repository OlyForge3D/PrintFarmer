using System;
using System.Threading.Tasks;
using Farm.Web.Client;
using Xunit;

namespace Farm.Web.Client.Tests;

public class ToastServiceTests
{
    [Fact]
    public async Task Add_And_Dismiss_Raise_OnChanged()
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
}
