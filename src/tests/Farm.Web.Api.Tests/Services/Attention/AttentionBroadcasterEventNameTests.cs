using Farm.Infrastructure.Services.Attention;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

/// <summary>
/// Guards the SignalR event contract for the unified attention feed. Names MUST be
/// lowercase per the SignalR conventions (SKILL: signalr-event-alerts, PrintFarmer copilot
/// instructions), so clients on iOS and web can subscribe consistently.
/// </summary>
public class AttentionBroadcasterEventNameTests
{
    [Fact]
    public void EventName_IsLowercaseAttentionChanged()
    {
        // Regression guard: mobile + web clients wire "attentionchanged" as an invalidation
        // hint on /hubs/printers. Renaming or capitalizing this constant breaks both clients.
        IAttentionBroadcaster.EventName.Should().Be("attentionchanged");
    }
}
