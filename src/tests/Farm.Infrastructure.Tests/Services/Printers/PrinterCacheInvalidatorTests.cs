using System;
using System.Collections.Generic;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Unit tests for <see cref="PrinterCacheInvalidator"/>, the pub/sub fan-out used to notify
/// backend polling services when a printer row has been edited (issue #1763). This is the
/// invalidation half of the polling-cache perf fix, so it must be correct on its own: a missed
/// or duplicated notification would either serve stale credentials or defeat the whole point of
/// caching.
/// </summary>
public class PrinterCacheInvalidatorTests
{
    private readonly PrinterCacheInvalidator _invalidator = new(NullLogger<PrinterCacheInvalidator>.Instance);

    [Fact]
    public void Invalidate_NoSubscribers_IsNoOp()
    {
        Action act = () => _invalidator.Invalidate(Guid.NewGuid());

        act.Should().NotThrow();
    }

    [Fact]
    public void Invalidate_SingleSubscriber_FiresWithCorrectPrinterId()
    {
        Guid printerId = Guid.NewGuid();
        Guid? received = null;
        _invalidator.Subscribe(id => received = id);

        _invalidator.Invalidate(printerId);

        received.Should().Be(printerId);
    }

    [Fact]
    public void Invalidate_MultipleSubscribers_AllFire()
    {
        Guid printerId = Guid.NewGuid();
        var received = new List<Guid>();
        _invalidator.Subscribe(id => received.Add(id));
        _invalidator.Subscribe(id => received.Add(id));
        _invalidator.Subscribe(id => received.Add(id));

        _invalidator.Invalidate(printerId);

        received.Should().Equal(printerId, printerId, printerId);
    }

    [Fact]
    public void Unsubscribe_RemovedSubscriber_DoesNotFireAgain()
    {
        Guid printerId = Guid.NewGuid();
        int callCount = 0;
        void Handler(Guid _) => callCount++;
        _invalidator.Subscribe(Handler);
        _invalidator.Invalidate(printerId);
        callCount.Should().Be(1);

        _invalidator.Unsubscribe(Handler);
        _invalidator.Invalidate(printerId);

        callCount.Should().Be(1);
    }

    [Fact]
    public void Invalidate_ThrowingSubscriber_DoesNotPreventOtherSubscribersFromBeingNotified()
    {
        Guid printerId = Guid.NewGuid();
        Guid? receivedByGoodSubscriber = null;
        _invalidator.Subscribe(_ => throw new InvalidOperationException("simulated subscriber failure"));
        _invalidator.Subscribe(id => receivedByGoodSubscriber = id);

        Action act = () => _invalidator.Invalidate(printerId);

        act.Should().NotThrow();
        receivedByGoodSubscriber.Should().Be(printerId);
    }

    [Fact]
    public void Subscribe_NullHandler_Throws()
    {
        Action act = () => _invalidator.Subscribe(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Unsubscribe_NullHandler_Throws()
    {
        Action act = () => _invalidator.Unsubscribe(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
