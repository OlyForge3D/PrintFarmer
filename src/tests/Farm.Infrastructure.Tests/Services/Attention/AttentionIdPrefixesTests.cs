using Farm.Infrastructure.Services.Attention;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Attention;

/// <summary>
/// Guards the wire format of computed attention item ids. Ids are persisted in
/// <c>AttentionSnoozes.AttentionItemId</c> and travel across web + iOS clients, so any
/// change to the format breaks previously persisted snoozes and both clients.
/// </summary>
public class AttentionIdPrefixesTests
{
    [Fact]
    public void Prefixes_AreStableLowercaseTokens()
    {
        // Renaming a prefix invalidates previously persisted snoozes on production DBs.
        AttentionIdPrefixes.Failure.Should().Be("failure");
        AttentionIdPrefixes.Runout.Should().Be("runout");
        AttentionIdPrefixes.Harvest.Should().Be("harvest");
        AttentionIdPrefixes.Maintenance.Should().Be("maintenance");
        AttentionIdPrefixes.Offline.Should().Be("offline");
    }

    [Fact]
    public void Build_ProducesPrefixColonGuidWithDefaultFormat()
    {
        Guid id = new("11111111-2222-3333-4444-555555555555");

        string built = AttentionIdPrefixes.Build(AttentionIdPrefixes.Failure, id);

        built.Should().Be("failure:11111111-2222-3333-4444-555555555555");
    }
}
