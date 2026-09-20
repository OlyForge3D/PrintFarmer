using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public class HostUpdateExecutionOptionsDefaultsTests
{
    [Fact]
    public void FenceProofDefaults_AreExpectedAndCoverRequiredWriterDuration()
    {
        var options = new HostUpdateExecutionOptions();

        options.FenceProofTimeoutSeconds.Should().Be(321);
        options.FencePollIntervalSeconds.Should().Be(2);
        BackendStartCommandConsumerService.RequiredFenceProofDuration
            .Should().Be(TimeSpan.FromSeconds(319));
        TimeSpan.FromSeconds(
                options.FenceProofTimeoutSeconds - options.FencePollIntervalSeconds)
            .Should().BeGreaterThanOrEqualTo(
                BackendStartCommandConsumerService.RequiredFenceProofDuration);
    }

    [Fact]
    public void BackendStartFenceDeadlineAddends_AreExpected()
    {
        BackendStartCommandConsumerService.IterationDeadline
            .Should().Be(TimeSpan.FromSeconds(310));
        BackendStartCommandConsumerService.CancellationCleanupDeadline
            .Should().Be(TimeSpan.FromSeconds(4));
        BackendStartCommandConsumerService.OutcomePersistenceDeadline
            .Should().Be(TimeSpan.FromSeconds(4));
        BackendStartCommandConsumerService.FenceAcknowledgementMargin
            .Should().Be(TimeSpan.FromSeconds(1));
        (BackendStartCommandConsumerService.IterationDeadline
         + BackendStartCommandConsumerService.CancellationCleanupDeadline
         + BackendStartCommandConsumerService.OutcomePersistenceDeadline
         + BackendStartCommandConsumerService.FenceAcknowledgementMargin)
            .Should().Be(BackendStartCommandConsumerService.RequiredFenceProofDuration);
    }
}
