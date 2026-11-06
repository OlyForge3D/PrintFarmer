namespace Farm.Web.Api.Services.Gcode.Safety;

/// <summary>The lifecycle point recorded for a G-code input check.</summary>
public enum GcodeSafetyCheckpoint
{
    /// <summary>Unknown. Never a valid value.</summary>
    Unspecified = 0,

    /// <summary>Before a worker artifact is accepted as complete.</summary>
    BeforeArtifactCompletion = 1,

    /// <summary>Before an artifact is promoted into the G-code library.</summary>
    BeforePromotion = 2,

    /// <summary>Before a print job is queued.</summary>
    BeforeQueueing = 3,

    /// <summary>Before a print job is started.</summary>
    BeforeStart = 4,

    /// <summary>Before an artifact is streamed to a physical printer.</summary>
    BeforeSendToPrinter = 5,
}

/// <summary>One rejection reason from a G-code input check.</summary>
/// <param name="Code">A stable, machine-readable problem code.</param>
/// <param name="Field">The field or location the problem was found at.</param>
/// <param name="Message">A human-readable description of the problem.</param>
public sealed record GcodeSafetyProblem(string Code, string Field, string Message);

/// <summary>A non-empty input record, not a certification of command safety.</summary>
/// <param name="Checkpoint">The lifecycle point that was checked.</param>
/// <param name="GcodeSha256">The digest of the supplied G-code text.</param>
/// <param name="CommandCount">The number of non-empty lines after stripping semicolon comments.</param>
/// <param name="ValidatedAtUtc">When the check ran.</param>
public sealed record GcodeSafetyReport(
    GcodeSafetyCheckpoint Checkpoint,
    string GcodeSha256,
    int CommandCount,
    DateTime ValidatedAtUtc);

/// <summary>The outcome of a G-code input check: either a report or problems.</summary>
/// <typeparam name="T">The successful payload type.</typeparam>
public sealed class GcodeSafetyResult<T>
{
    internal GcodeSafetyResult(bool isValid, T? value, IReadOnlyList<GcodeSafetyProblem> problems)
    {
        if (isValid && problems.Count > 0)
        {
            throw new ArgumentException("A valid result cannot carry problems.", nameof(problems));
        }

        if (!isValid && problems.Count == 0)
        {
            throw new ArgumentException("An invalid result must carry at least one problem.", nameof(problems));
        }

        IsValid = isValid;
        Value = value;
        Problems = problems;
    }

    /// <summary>Whether the input check succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>The successful payload, when <see cref="IsValid"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>The rejection reasons, empty when <see cref="IsValid"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<GcodeSafetyProblem> Problems { get; }
}

/// <summary>Non-generic factory methods for <see cref="GcodeSafetyResult{T}"/>.</summary>
public static class GcodeSafetyResult
{
    /// <summary>Creates a successful result.</summary>
    /// <typeparam name="T">The successful payload type.</typeparam>
    /// <param name="value">The successful payload.</param>
    public static GcodeSafetyResult<T> Success<T>(T value) => new(true, value, []);

    /// <summary>Creates a failed result from one or more problems.</summary>
    /// <typeparam name="T">The successful payload type.</typeparam>
    /// <param name="problems">The rejection reasons.</param>
    public static GcodeSafetyResult<T> Failure<T>(IReadOnlyList<GcodeSafetyProblem> problems) =>
        new(false, default, problems);

    /// <summary>Creates a failed result from a single problem.</summary>
    /// <typeparam name="T">The successful payload type.</typeparam>
    /// <param name="code">A stable, machine-readable problem code.</param>
    /// <param name="field">The field or location the problem was found at.</param>
    /// <param name="message">A human-readable description of the problem.</param>
    public static GcodeSafetyResult<T> Failure<T>(string code, string field, string message) =>
        new(false, default, [new GcodeSafetyProblem(code, field, message)]);
}

/// <summary>The stable, machine-readable rejection codes for G-code input checks.</summary>
public static class GcodeSafetyProblemCodes
{
    /// <summary>The supplied G-code is null or empty.</summary>
    public const string Malformed = "gcode_malformed";
}

/// <summary>Input text and report metadata, with no machine-specific command limits.</summary>
/// <param name="Gcode">The G-code program text to check.</param>
/// <param name="Checkpoint">The lifecycle point being checked.</param>
public sealed record GcodeSafetyRequest(
    string? Gcode,
    GcodeSafetyCheckpoint Checkpoint);

/// <summary>Rejects only null or empty G-code and records non-empty input without rewriting it.</summary>
/// <remarks>
/// Command content, temperatures, motion, geometry, speed, acceleration, extrusion and host
/// commands are not inspected. Firmware configuration, trusted programs and operator procedures
/// own command safety. A successful report is not a safety certification.
/// </remarks>
public interface IGcodeSafetyValidator
{
    /// <summary>Checks for empty input and records a report for non-empty text.</summary>
    /// <param name="request">The input and report metadata.</param>
    /// <returns>The report, or the empty-input rejection.</returns>
    GcodeSafetyResult<GcodeSafetyReport> Validate(GcodeSafetyRequest request);
}
