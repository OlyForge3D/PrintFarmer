using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Issue #3145: writer-to-parser contract. The host-update verifier must accept exactly what the
/// real <c>/health</c> writer (<see cref="ProgramHelpers.WriteHealthResponseAsync"/>) emits, so
/// the two sides cannot drift apart again behind hand-built fixtures.
/// </summary>
public sealed class HealthResponseHostUpdateContractTests
{
    private static readonly IReadOnlySet<string> Required =
        new HashSet<string>(StringComparer.Ordinal) { "comprehensive", "signalr", "spoolman" };

    [Fact]
    public async Task RealWriterOutput_AllRequiredEntriesHealthy_IsAccepted()
    {
        string body = await WriteAsync(HealthStatus.Healthy, HealthStatus.Healthy, HealthStatus.Healthy);

        HostUpdateAggregateHealthReport.IsHealthy(body, Required).Should().BeTrue(body);
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task RealWriterOutput_AnyRequiredEntryNotHealthy_IsRejected(HealthStatus entryStatus)
    {
        foreach (int position in new[] { 0, 1, 2 })
        {
            HealthStatus[] statuses = [HealthStatus.Healthy, HealthStatus.Healthy, HealthStatus.Healthy];
            statuses[position] = entryStatus;

            string body = await WriteAsync(statuses);

            HostUpdateAggregateHealthReport.IsHealthy(body, Required).Should().BeFalse(body);
        }
    }

    [Fact]
    public async Task RealWriterOutput_MissingRequiredEntry_IsRejected()
    {
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
        {
            ["comprehensive"] = Entry(HealthStatus.Healthy),
            ["signalr"] = Entry(HealthStatus.Healthy),
        };

        string body = await WriteAsync(new HealthReport(entries, TimeSpan.FromMilliseconds(5)));

        HostUpdateAggregateHealthReport.IsHealthy(body, Required).Should().BeFalse(body);
    }

    [Fact]
    public async Task RealWriterOutput_WireFormatIsUnchanged()
    {
        // Other consumers (the React frontend's DetailedHealthStatusEntry) depend on this shape:
        // a string top-level status and numeric per-entry HealthStatus values.
        string body = await WriteAsync(HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("Unhealthy");
        JsonElement results = root.GetProperty("results");
        results.GetProperty("comprehensive").GetProperty("status").GetInt32().Should().Be((int)HealthStatus.Healthy).And.Be(2);
        results.GetProperty("signalr").GetProperty("status").GetInt32().Should().Be((int)HealthStatus.Degraded).And.Be(1);
        results.GetProperty("spoolman").GetProperty("status").GetInt32().Should().Be((int)HealthStatus.Unhealthy).And.Be(0);
    }

    [Fact]
    public async Task RealWriterOutput_QueueConsumersEntry_UsesNumericStatusAndCamelCaseData()
    {
        // Issue #3157: the recovery matrix and the host-update parser both read this entry.
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
        {
            ["comprehensive"] = Entry(HealthStatus.Healthy),
            ["signalr"] = Entry(HealthStatus.Healthy),
            ["spoolman"] = Entry(HealthStatus.Healthy),
            [Farm.Web.Api.Health.QueueConsumersHealthCheck.Name] = new(
                HealthStatus.Healthy,
                "All 6 queue consumers running",
                TimeSpan.FromMilliseconds(1),
                exception: null,
                data: new Dictionary<string, object> { ["backendStartCommandConsumer"] = "running" }),
        };

        string body = await WriteAsync(new HealthReport(entries, TimeSpan.FromMilliseconds(5)));

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement entry = document.RootElement.GetProperty("results").GetProperty("queue-consumers");
        entry.GetProperty("status").GetInt32().Should().Be(2);
        entry.GetProperty("data").GetProperty("backendStartCommandConsumer").GetString().Should().Be("running");

        var withQueue = new HashSet<string>(Required, StringComparer.Ordinal) { "queue-consumers" };
        HostUpdateAggregateHealthReport.IsHealthy(body, withQueue).Should().BeTrue(body);
    }

    [Fact]
    public void DefaultRequiredAggregateHealthResultNames_DoNotRequireQueueConsumers()
    {
        // Rolling back to a release that predates #3157 must still verify, so the entry is
        // observable but not a default host-update requirement.
        new HostUpdateExecutionOptions().RequiredAggregateHealthResultNames
            .Should().NotContain("queue-consumers");
    }

    private static Task<string> WriteAsync(params HealthStatus[] statuses)
    {
        string[] names = ["comprehensive", "signalr", "spoolman"];
        var entries = new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            entries[names[i]] = Entry(statuses[i]);
        }

        return WriteAsync(new HealthReport(entries, TimeSpan.FromMilliseconds(5)));
    }

    private static HealthReportEntry Entry(HealthStatus status) =>
        new(status, "contract", TimeSpan.FromMilliseconds(1), exception: null, data: new Dictionary<string, object> { ["probe"] = "contract" });

    private static async Task<string> WriteAsync(HealthReport report)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

        await ProgramHelpers.WriteHealthResponseAsync(context, report, startup: null, environment.Object);

        return Encoding.UTF8.GetString(body.ToArray());
    }
}
