using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>Issue #3114: the enrolled host-update daemon service core is a thin, disabled host around the existing executor.</summary>
public sealed class HostUpdateDaemonTests : IDisposable
{
    private const string ReleaseId = "stable:1.2.3";

    private readonly DirectoryInfo root = HostStateTestPaths.CreateTempSubdirectory("pf-daemon-");

    private string StateDirectory => Path.Combine(root.FullName, "state");

    public HostUpdateDaemonTests() => Directory.CreateDirectory(StateDirectory);

    public void Dispose()
    {
        try
        {
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void ProductionGate_IsDisabled_AndHasNoInputThatCouldEnableIt()
    {
        var gate = new DisabledHostUpdateDaemonExecutionGate();

        gate.Evaluate().Should().Be(DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        typeof(DisabledHostUpdateDaemonExecutionGate).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Should().BeEmpty();
        typeof(DisabledHostUpdateDaemonExecutionGate).GetProperties().Should().BeEmpty();
    }

    [Fact]
    public void Options_HaveNoExecutionOrAutoUpdateSwitch()
    {
        HostUpdateDaemonOptions.AllowedKeys.Should().BeEquivalentTo(["PollIntervalSeconds", "MaxBackoffSeconds", "IdentityDirectory"]);
        typeof(HostUpdateDaemonOptions).GetProperties().Where(p => p.PropertyType == typeof(bool)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("AutoUpdate")]
    [InlineData("ExecutionEnabled")]
    [InlineData("ExecutionMode")]
    [InlineData("ApiKey")]
    public void Options_RejectUnknownKeys_SoNoSettingCanSilentlyEnableExecution(string key)
    {
        IConfiguration configuration = Configuration(new() { [$"HostUpdateDaemon:{key}"] = "true" });
        var options = new HostUpdateDaemonOptions();
        configuration.GetSection(HostUpdateDaemonOptions.SectionName).Bind(options);

        HostUpdateDaemonOptions.Validate(configuration, options).Should().Equal("daemon_setting_unknown:" + key);
    }

    [Theory]
    [InlineData(59, 900, "daemon_poll_interval_out_of_range")]
    [InlineData(300, 901, "daemon_max_backoff_out_of_range")]
    [InlineData(300, 120, "daemon_max_backoff_out_of_range")]
    public void Options_EnforcePollingBounds(int poll, int backoff, string expected)
    {
        var options = new HostUpdateDaemonOptions { PollIntervalSeconds = poll, MaxBackoffSeconds = backoff };

        HostUpdateDaemonOptions.Validate(Configuration(new()), options).Should().Contain(expected);
    }

    [Fact]
    public void Options_Defaults_AreValid()
    {
        var options = new HostUpdateDaemonOptions();

        HostUpdateDaemonOptions.Validate(Configuration(new()), options).Should().BeEmpty();
        options.IdentityDirectory.Should().BeEmpty();
    }

    [Fact]
    public void Options_RejectRelativeIdentityDirectory() =>
        HostUpdateDaemonOptions.Validate(Configuration(new()), new HostUpdateDaemonOptions { IdentityDirectory = "daemon" })
            .Should().Equal("daemon_identity_directory_not_absolute");

    [Fact]
    public void Options_RejectTraversalInIdentityDirectory() =>
        HostUpdateDaemonOptions.Validate(
                Configuration(new()),
                new HostUpdateDaemonOptions { IdentityDirectory = Path.Combine(Path.GetTempPath(), "a", "..", "daemon") })
            .Should().Equal("daemon_identity_directory_traversal_rejected");

    [Fact]
    public async Task Dispatcher_WithProductionGate_NeverCallsExecutor()
    {
        var executor = new Mock<IHostUpdateExecutor>(MockBehavior.Strict);
        var dispatcher = new HostUpdateDaemonExecutionDispatcher(new DisabledHostUpdateDaemonExecutionGate(), executor.Object);

        HostUpdateDaemonDispatchResult result = await dispatcher.DispatchAsync(Request(), CancellationToken.None);

        result.Dispatched.Should().BeFalse();
        result.RefusalCode.Should().Be(DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        executor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatcher_WhenGateOpens_ExecutesOnlyThroughExistingExecutor()
    {
        HostUpdateExecutionRequest request = Request();
        var expected = new HostUpdateExecutionResult(ReleaseId, HostUpdateExecutionState.Completed, null, []);
        var executor = new Mock<IHostUpdateExecutor>(MockBehavior.Strict);
        executor.Setup(e => e.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var dispatcher = new HostUpdateDaemonExecutionDispatcher(new OpenGate(), executor.Object);

        HostUpdateDaemonDispatchResult result = await dispatcher.DispatchAsync(request, CancellationToken.None);

        result.Dispatched.Should().BeTrue();
        result.Execution.Should().BeSameAs(expected);
        executor.Verify(e => e.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_ReportsHeldExecutionLock_InsteadOfRunningConcurrently()
    {
        var executor = new Mock<IHostUpdateExecutor>();
        executor.Setup(e => e.ExecuteAsync(It.IsAny<HostUpdateExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("host_update_lock_timeout"));
        var dispatcher = new HostUpdateDaemonExecutionDispatcher(new OpenGate(), executor.Object);

        HostUpdateDaemonDispatchResult result = await dispatcher.DispatchAsync(Request(), CancellationToken.None);

        result.Dispatched.Should().BeFalse();
        result.RefusalCode.Should().Be(HostUpdateDaemonExecutionDispatcher.ExecutionLockHeldCode);
    }

    [Fact]
    public async Task RunOnce_ReportsHealth_LoadsJournal_AndDoesNotEnableExecution()
    {
        SeedJournal(ReleaseId, HostUpdateExecutionState.Completed);
        SeedJournal("stable:1.2.4", HostUpdateExecutionState.RecoveryRequired);
        SeedJournal("stable:1.2.5", HostUpdateExecutionState.Applying);
        var sink = new ListSink();

        string? failure = await Daemon(sink).RunAsync(once: true, CancellationToken.None);

        failure.Should().BeNull();
        sink.Statuses.Select(s => s.Lifecycle).Should().Equal(
            HostUpdateDaemonLifecycle.Starting,
            HostUpdateDaemonLifecycle.Running,
            HostUpdateDaemonLifecycle.Stopping,
            HostUpdateDaemonLifecycle.Stopped);
        sink.Statuses.Should().OnlyContain(s => !s.ExecutionEnabled && !s.Enrolled &&
            s.ExecutionGateCode == DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        HostUpdateDaemonStatus running = sink.Statuses[1];
        running.JournalCode.Should().Be(HostUpdateDaemonJournalSnapshot.OkCode);
        running.ReleaseCount.Should().Be(3);
        running.RecoveryRequiredCount.Should().Be(1);
        running.InFlightCount.Should().Be(1);
        running.IdentityStorage.Should().Be(HostUpdateDaemonIdentityStorageState.NotConfigured);
    }

    [Fact]
    public async Task RunOnce_DoesNotResumeOrWriteTheJournal()
    {
        SeedJournal(ReleaseId, HostUpdateExecutionState.Applying);
        string journal = Path.Combine(StateDirectory, "journal.ndjson");
        byte[] before = await File.ReadAllBytesAsync(journal);

        await Daemon(new ListSink()).RunAsync(once: true, CancellationToken.None);

        (await File.ReadAllBytesAsync(journal)).Should().Equal(before);
    }

    [Fact]
    public async Task SecondInstance_IsRefused_WhileFirstHoldsTheInstanceLease()
    {
        using var cts = new CancellationTokenSource();
        var firstSink = new ListSink();
        HostUpdateDaemon first = Daemon(firstSink);
        Task<string?> running = first.RunAsync(once: false, cts.Token);
        await WaitUntilAsync(() => first.Status?.Lifecycle == HostUpdateDaemonLifecycle.Running);

        var secondSink = new ListSink();
        string? failure = await Daemon(secondSink).RunAsync(once: true, CancellationToken.None);

        failure.Should().Be(HostUpdateDaemon.AlreadyRunningCode);
        secondSink.Statuses.Should().ContainSingle().Which.Lifecycle.Should().Be(HostUpdateDaemonLifecycle.Failed);

        await cts.CancelAsync();
        (await running).Should().BeNull();
        first.Status!.Lifecycle.Should().Be(HostUpdateDaemonLifecycle.Stopped);

        // After a clean stop the lease is released.
        (await Daemon(new ListSink()).RunAsync(once: true, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HeldExecutionLock_IsReported_AndTheJournalIsNotRead()
    {
        var journal = new Mock<IHostUpdateExecutionJournal>(MockBehavior.Strict);
        var executionLock = new FileHostUpdateExecutionLock(Path.Combine(StateDirectory, FileHostUpdateExecutionLock.FileName));
        using IHostUpdateExecutionLease held = executionLock.Acquire(TimeSpan.FromSeconds(5), CancellationToken.None);
        var sink = new ListSink();

        await Daemon(sink, new HostUpdateDaemonJournalReader(StateDirectory, executionLock, journal.Object)).RunAsync(once: true, CancellationToken.None);

        HostUpdateDaemonStatus running = sink.Statuses.Single(s => s.Lifecycle == HostUpdateDaemonLifecycle.Running);
        running.JournalCode.Should().Be(HostUpdateDaemonExecutionDispatcher.ExecutionLockHeldCode);
        running.ConsecutiveFailures.Should().Be(0);
        journal.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CorruptJournal_FailsTheCycleWithAFixedCode_AndBacksOff()
    {
        await File.WriteAllTextAsync(Path.Combine(StateDirectory, "journal.ndjson"), "{not json at " + root.FullName + "}\n");
        var sink = new ListSink();

        await Daemon(sink).RunAsync(once: true, CancellationToken.None);

        HostUpdateDaemonStatus running = sink.Statuses.Single(s => s.Lifecycle == HostUpdateDaemonLifecycle.Running);
        running.JournalCode.Should().Be("journal_corrupt");
        running.Code.Should().Be("daemon_cycle_failed");
        running.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task InvalidIdentityStorage_FailsTheCycle_AndBacksOff()
    {
        var sink = new ListSink();
        var options = new HostUpdateDaemonOptions { IdentityDirectory = Path.Combine(root.FullName, "identity") };

        await Daemon(sink, options: options).RunAsync(once: true, CancellationToken.None);

        HostUpdateDaemonStatus running = sink.Statuses.Single(s => s.Lifecycle == HostUpdateDaemonLifecycle.Running);
        running.IdentityStorage.Should().Be(HostUpdateDaemonIdentityStorageState.Invalid);
        running.IdentityStorageCode.Should().Be("identity_inside_executor_root");
        running.Code.Should().Be("daemon_cycle_failed");
        running.ConsecutiveFailures.Should().Be(1);
        running.Enrolled.Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseWithEmptyHistory_FailsTheCycleWithAFixedCode()
    {
        var journal = new Mock<IHostUpdateExecutionJournal>();
        journal.Setup(j => j.ListReleaseIds()).Returns([ReleaseId]);
        journal.Setup(j => j.Read(ReleaseId)).Returns([]);
        var executionLock = new FileHostUpdateExecutionLock(Path.Combine(StateDirectory, FileHostUpdateExecutionLock.FileName));
        var sink = new ListSink();

        await Daemon(sink, new HostUpdateDaemonJournalReader(StateDirectory, executionLock, journal.Object)).RunAsync(once: true, CancellationToken.None);

        HostUpdateDaemonStatus running = sink.Statuses.Single(s => s.Lifecycle == HostUpdateDaemonLifecycle.Running);
        running.JournalCode.Should().Be("journal_release_history_empty");
        running.Code.Should().Be("daemon_cycle_failed");
    }

    [Fact]
    public async Task UnavailableSubsystem_ReportsTheSameStateCodeAsTheCli()
    {
        var journal = new Mock<IHostUpdateExecutionJournal>();
        journal.Setup(j => j.ListReleaseIds()).Throws(new HostUpdateSubsystemUnavailableException("host_update_unavailable", null));
        var executionLock = new FileHostUpdateExecutionLock(Path.Combine(StateDirectory, FileHostUpdateExecutionLock.FileName));
        var sink = new ListSink();

        await Daemon(sink, new HostUpdateDaemonJournalReader(StateDirectory, executionLock, journal.Object)).RunAsync(once: true, CancellationToken.None);

        sink.Statuses.Single(s => s.Lifecycle == HostUpdateDaemonLifecycle.Running).JournalCode.Should().Be("state_unavailable");
    }

    [Fact]
    public void NextDelay_BacksOffExponentially_WithinTheCeiling()
    {
        var options = new HostUpdateDaemonOptions { PollIntervalSeconds = 60, MaxBackoffSeconds = 900 };
        HostUpdateDaemon daemon = Daemon(new ListSink(), options: options, jitter: () => 1.0);

        daemon.NextDelay().Should().Be(TimeSpan.FromSeconds(66));
        SetFailures(daemon, 1);
        daemon.NextDelay().Should().Be(TimeSpan.FromSeconds(132));
        SetFailures(daemon, 30);
        daemon.NextDelay().Should().Be(TimeSpan.FromSeconds(900));
    }

    [Fact]
    public async Task Cancellation_StopsTheLoopCleanly()
    {
        using var cts = new CancellationTokenSource();
        var sink = new ListSink();
        HostUpdateDaemon daemon = Daemon(sink);
        Task<string?> running = daemon.RunAsync(once: false, cts.Token);
        await WaitUntilAsync(() => daemon.Status?.Lifecycle == HostUpdateDaemonLifecycle.Running);

        await cts.CancelAsync();

        (await running).Should().BeNull();
        sink.Statuses[^1].Lifecycle.Should().Be(HostUpdateDaemonLifecycle.Stopped);
        sink.Statuses.Should().ContainSingle(s => s.Lifecycle == HostUpdateDaemonLifecycle.Running)
            .Which.NextCycleSeconds.Should().BeInRange(300, 330);
    }

    [Fact]
    public async Task StatusAndLogs_RedactPathsAndIdentityMaterial()
    {
        const string KeyMarker = "PRIVATE-KEY-MARKER-3114";
        string identity = Path.Combine(HostStateTestPaths.TempRoot, "pf-daemon-identity-SECRETPATH-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(identity);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(identity, HostUpdateDaemonIdentityStorage.KeyFileName), KeyMarker);
            await File.WriteAllTextAsync(Path.Combine(StateDirectory, "journal.ndjson"), "garbage " + identity + "\n");
            var sink = new ListSink();
            var logger = new ListLogger();

            await Daemon(sink, options: new HostUpdateDaemonOptions { IdentityDirectory = identity }, logger: logger).RunAsync(once: true, CancellationToken.None);

            var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            string everything = string.Join('\n', sink.Statuses.Select(s => JsonSerializer.Serialize(s, json)).Concat(logger.Messages));
            everything.Should().NotContain("SECRETPATH").And.NotContain(KeyMarker).And.NotContain(root.FullName).And.NotContain(HostStateTestPaths.TempRoot);
            sink.Statuses.Should().OnlyContain(s => !s.Enrolled);
        }
        finally
        {
            Directory.Delete(identity, recursive: true);
        }
    }

    private static void SetFailures(HostUpdateDaemon daemon, int failures) =>
        typeof(HostUpdateDaemon).GetField("consecutiveFailures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(daemon, failures);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the daemon should reach the expected state");
            await Task.Delay(10);
        }
    }

    private HostUpdateDaemon Daemon(
        ListSink sink,
        IHostUpdateDaemonJournalReader? reader = null,
        HostUpdateDaemonOptions? options = null,
        Func<double>? jitter = null,
        ListLogger? logger = null)
    {
        reader ??= new HostUpdateDaemonJournalReader(
            StateDirectory,
            new FileHostUpdateExecutionLock(Path.Combine(StateDirectory, FileHostUpdateExecutionLock.FileName)),
            new FileHostUpdateExecutionJournal(Path.Combine(StateDirectory, "journal.ndjson")));
        return new HostUpdateDaemon(
            options ?? new HostUpdateDaemonOptions(),
            StateDirectory,
            root.FullName,
            reader,
            new DisabledHostUpdateDaemonExecutionGate(),
            sink,
            logger ?? new ListLogger(),
            jitterSource: jitter);
    }

    private void SeedJournal(string releaseId, HostUpdateExecutionState state) =>
        new FileHostUpdateExecutionJournal(Path.Combine(StateDirectory, "journal.ndjson"))
            .Append(new HostUpdateExecutionActivity(Guid.NewGuid().ToString("N"), releaseId, state, "seeded", DateTimeOffset.UtcNow));

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static HostUpdateExecutionRequest Request() =>
        new(ReleaseId, 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, []);

    private sealed class OpenGate : IHostUpdateDaemonExecutionGate
    {
        public string? Evaluate() => null;
    }

    private sealed class ListSink : IHostUpdateDaemonStatusSink
    {
        private readonly List<HostUpdateDaemonStatus> statuses = [];

        public IReadOnlyList<HostUpdateDaemonStatus> Statuses
        {
            get
            {
                lock (statuses)
                {
                    return [.. statuses];
                }
            }
        }

        public void Publish(HostUpdateDaemonStatus status)
        {
            lock (statuses)
            {
                statuses.Add(status);
            }
        }
    }

    private sealed class ListLogger : ILogger<HostUpdateDaemon>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (messages)
                {
                    return [.. messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            }
        }
    }
}
