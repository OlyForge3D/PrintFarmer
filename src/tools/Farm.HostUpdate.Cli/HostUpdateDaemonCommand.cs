using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// <c>daemon</c>: runs the enrolled host-update daemon service core (issue #3114) inside the
/// existing CLI packaging and service graph, so the daemon shares the CLI's executor, journal and
/// lock registrations instead of carrying its own. Execution is always disabled.
/// </summary>
internal static class HostUpdateDaemonCommand
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(
        IServiceProvider provider,
        IConfiguration configuration,
        HostUpdateExecutionOptions executionOptions,
        HostUpdateCliArguments args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = new HostUpdateDaemonOptions();
        try
        {
            configuration.GetSection(HostUpdateDaemonOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException exception)
        {
            return await HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new HostUpdateCli.CliFailure("configuration_invalid", [exception.GetType().Name])).ConfigureAwait(false);
        }

        IReadOnlyList<string> failures = HostUpdateDaemonOptions.Validate(configuration, options);
        if (failures.Count > 0)
        {
            return await HostUpdateCli.EmitAsync(output, args.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new HostUpdateCli.CliFailure("configuration_invalid", [.. failures])).ConfigureAwait(false);
        }

        var sink = new LineSink(output, args.Json);
        var daemon = new HostUpdateDaemon(
            options,
            executionOptions.StateDirectory,
            executionOptions.RootDirectory,
            new HostUpdateDaemonJournalReader(
                executionOptions.StateDirectory,
                provider.GetRequiredService<IHostUpdateExecutionLock>(),
                provider.GetRequiredService<IHostUpdateExecutionJournal>(),
                new FileHostUpdateDaemonVerificationJournal(
                    Path.Join(executionOptions.StateDirectory, FileHostUpdateDaemonVerificationJournal.FileName))),
            new DisabledHostUpdateDaemonExecutionGate(),
            sink,
            provider.GetRequiredService<ILogger<HostUpdateDaemon>>());
        string? failure = await daemon.RunAsync(args.Once, cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync().ConfigureAwait(false);
        return failure switch
        {
            HostUpdateDaemon.AlreadyRunningCode => HostUpdateCliExitCodes.LockHeld,
            not null => HostUpdateCliExitCodes.StateUnreadable,
            _ when args.Once && daemon.Status?.ConsecutiveFailures > 0 => HostUpdateCliExitCodes.StateUnreadable,
            _ => HostUpdateCliExitCodes.Success,
        };
    }

    private sealed class LineSink(TextWriter output, bool json) : IHostUpdateDaemonStatusSink
    {
        private readonly object gate = new();

        public void Publish(HostUpdateDaemonStatus status)
        {
            string line = json
                ? JsonSerializer.Serialize(status, LineOptions)
                : $"{status.Lifecycle.ToString().ToLowerInvariant()} {status.Code} gate={status.ExecutionGateCode} identity={status.IdentityStorageCode} journal={status.JournalCode} verification={status.VerificationCode} checkpoint={status.Checkpoint?.Code ?? "checkpoint_unavailable"} recovery={status.Checkpoint?.RecoveryHint ?? "none"}";
            lock (gate)
            {
                output.WriteLine(line);
                output.Flush();
            }
        }

        public Task FlushAsync() => output.FlushAsync();
    }
}
