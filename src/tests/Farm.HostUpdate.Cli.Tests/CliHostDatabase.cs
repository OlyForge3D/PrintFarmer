using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// A real application-schema SQLite database for the fixture, so the CLI's read-only printer
/// command inventory (issue #2999) runs against the same tables production uses. The template
/// is built once per test run in rollback-journal mode, so a read-only open leaves no
/// <c>-wal</c>/<c>-shm</c> side files and <see cref="CliHostFixture.Snapshot"/> stays exact.
/// </summary>
internal static class CliHostDatabase
{
    public static readonly Guid PrinterA = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    public static readonly Guid PrinterB = Guid.Parse("0b000000-0000-0000-0000-000000000002");
    public static readonly Guid IdlePrinter = Guid.Parse("0c000000-0000-0000-0000-000000000003");
    public static readonly Guid PrintingJob = Guid.Parse("1a000000-0000-0000-0000-000000000001");
    public static readonly Guid LeasedStartCommand = Guid.Parse("2a000000-0000-0000-0000-000000000001");
    public static readonly Guid UnknownAttempt = Guid.Parse("3b000000-0000-0000-0000-000000000001");
    public static readonly Guid DeadLetteredMoveCommand = Guid.Parse("4c000000-0000-0000-0000-000000000001");

    private static readonly Lazy<byte[]> Template = new(BuildTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static byte[] TemplateBytes => Template.Value;

    /// <summary>
    /// Seeds three printers: A is mid-print with a leased (Processing) start command whose
    /// delivery is unsettled, B has a dispatch attempt with an unknown outcome, and one is idle.
    /// </summary>
    public static void SeedUncertainCommands(string databasePath)
    {
        using AppDbContext db = Open(databasePath);
        db.Printers.AddRange(
            new Printer { Id = PrinterA, Name = "Printer A", ServerUrl = "http://printer-a" },
            new Printer { Id = PrinterB, Name = "Printer B", ServerUrl = "http://printer-b" },
            new Printer { Id = IdlePrinter, Name = "Idle printer", ServerUrl = "http://printer-c" });
        db.PrintJobs.Add(new PrintJob { Id = PrintingJob, Name = "benchy", AssignedPrinterId = PrinterA, Status = PrintJobStatus.Printing });
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = LeasedStartCommand,
            Sequence = 1,
            AggregateType = "PrintJob",
            AggregateId = PrintingJob,
            PrinterId = PrinterA,
            EventType = "PrintFarmer.Queue.BackendStartCommand.v1",
            Status = QueueOutboxEventStatus.Processing,
        });
        db.QueueDispatchAttempts.Add(new QueueDispatchAttempt
        {
            Id = UnknownAttempt,
            PrinterId = PrinterB,
            AttemptNumber = 1,
            ActorSubject = "system",
            StartPathKind = "Auto",
            ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Outcome = DispatchAttemptOutcome.Unknown,
            RequiresReconciliation = true,
        });
        db.SaveChanges();
    }

    /// <summary>Changes the inventory after a preview: the attempt on printer B is now settled.</summary>
    public static void SettleUnknownAttempt(string databasePath)
    {
        using AppDbContext db = Open(databasePath);
        QueueDispatchAttempt attempt = db.QueueDispatchAttempts.Single(a => a.Id == UnknownAttempt);
        attempt.Outcome = DispatchAttemptOutcome.Rejected;
        attempt.RequiresReconciliation = false;
        db.SaveChanges();
    }

    /// <summary>
    /// Changes the inventory after a preview: a move on the idle printer lost its response, so its
    /// command row was dead-lettered for manual review while the physical barrier stays held.
    /// </summary>
    public static void HoldDeadLetteredMoveBarrier(string databasePath)
    {
        using AppDbContext db = Open(databasePath);
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = DeadLetteredMoveCommand,
            Sequence = 2,
            AggregateType = "Printer",
            AggregateId = IdlePrinter,
            PrinterId = IdlePrinter,
            EventType = "PrintFarmer.Queue.BackendControlCommand.v1",
            Status = QueueOutboxEventStatus.DeadLettered,
            FailureCode = "manual_control_reconciliation_required",
        });
        db.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = IdlePrinter,
            PhysicalControlCommandId = DeadLetteredMoveCommand,
            PhysicalControlOperation = "move",
            PhysicalControlRequiresReconciliation = true,
        });
        db.SaveChanges();
    }

    public static (Guid? CommandId, bool RequiresReconciliation) ReadBarrier(string databasePath)
    {
        using AppDbContext db = Open(databasePath);
        PrinterDispatchState state = db.PrinterDispatchStates.AsNoTracking().Single(s => s.PrinterId == IdlePrinter);
        return (state.PhysicalControlCommandId, state.PhysicalControlRequiresReconciliation);
    }

    public static (QueueOutboxEventStatus Command, DispatchAttemptOutcome Attempt, bool RequiresReconciliation, PrintJobStatus Job) ReadCommandState(string databasePath)
    {
        using AppDbContext db = Open(databasePath);
        QueueDispatchOutbox command = db.QueueDispatchOutbox.AsNoTracking().Single(o => o.Id == LeasedStartCommand);
        QueueDispatchAttempt attempt = db.QueueDispatchAttempts.AsNoTracking().Single(a => a.Id == UnknownAttempt);
        PrintJob job = db.PrintJobs.AsNoTracking().Single(j => j.Id == PrintingJob);
        return (command.Status, attempt.Outcome, attempt.RequiresReconciliation, job.Status);
    }

    private static AppDbContext Open(string databasePath)
    {
        // Foreign keys are off so the seed needs no manufacturer/model/user graph.
        var connection = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = false, Pooling = false };
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection.ToString()).Options);
    }

    private static byte[] BuildTemplate()
    {
        string path = Path.Combine(Path.GetTempPath(), "pf-hostupdate-cli-template-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (AppDbContext db = Open(path))
            {
                db.Database.EnsureCreated();
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=DELETE;");
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
