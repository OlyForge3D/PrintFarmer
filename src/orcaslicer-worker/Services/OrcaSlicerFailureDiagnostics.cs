using System.Globalization;
using System.Text;
using System.Text.Json;
using Farm.Slicer.Module.Models;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Turns a failed OrcaSlicer CLI run into a diagnostic an admin can act on and a redacted
/// <see cref="SliceFailureReason"/> every caller can see (issue #1811).
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the worker scraped the process streams for the first line containing "error"
/// or "fail". On the CLI's slicing-failure path that first line is the bare word <c>Errors</c>
/// (OrcaSlicer's own <c>ex.what()</c>), so every such job surfaced exactly
/// <c>OrcaSlicer failed with exit code 156: Errors</c> and the informative second line was dropped.
/// </para>
/// <para>
/// The authoritative diagnostic is not on the streams at all: OrcaSlicer's <c>record_exit_reson</c>
/// always writes <c>result.json</c> into <c>--outputdir</c> before exiting, carrying the exact
/// signed <c>return_code</c>, a human-readable <c>error_string</c> from its own <c>cli_errors</c>
/// table, and — on the paths that reach it — per-plate <c>warning_message</c> text. That file is
/// read first here, and the streams are used to enrich it rather than to replace it.
/// </para>
/// </remarks>
internal static class OrcaSlicerFailureDiagnostics
{
    /// <summary>Name of the result file OrcaSlicer writes into its output directory.</summary>
    internal const string ResultFileName = "result.json";

    /// <summary>Upper bound on the composed detail, matching the API's 1024-char fail contract.</summary>
    private const int MaxDetailLength = 900;

    /// <summary>Upper bound on how many stream lines are carried into the detail.</summary>
    private const int MaxStreamLines = 6;

    /// <summary>
    /// Upper bound on the <c>result.json</c> bytes read. The engine writes a small fixed-shape
    /// document, so anything larger is not a diagnostic worth parsing — and this path must never be
    /// able to exhaust memory, because doing so would destroy the very failure it exists to report.
    /// </summary>
    private const int MaxResultFileBytes = 256 * 1024;

    /// <summary>
    /// OrcaSlicer's CLI exit codes, transcribed from <c>CLI_*</c> in <c>src/libslic3r/Utils.hpp</c>
    /// at tag <c>v2.4.2</c> (the release this worker pins). The symbolic name is reported so an
    /// admin can search OrcaSlicer's own source for the exact exit site.
    /// </summary>
    private static readonly Dictionary<int, (string Symbol, SliceFailureReason Reason)> ExitCodes = new()
    {
        [-1] = ("CLI_ENVIRONMENT_ERROR", SliceFailureReason.SlicerFailed),
        [-2] = ("CLI_INVALID_PARAMS", SliceFailureReason.SlicerFailed),
        [-3] = ("CLI_FILE_NOTFOUND", SliceFailureReason.ModelFileUnreadable),
        [-4] = ("CLI_FILELIST_INVALID_ORDER", SliceFailureReason.SlicerFailed),
        [-5] = ("CLI_CONFIG_FILE_ERROR", SliceFailureReason.ProfileInvalid),
        [-6] = ("CLI_DATA_FILE_ERROR", SliceFailureReason.ModelFileUnreadable),
        [-7] = ("CLI_INVALID_PRINTER_TECH", SliceFailureReason.ProfileNotCompatible),
        [-8] = ("CLI_UNSUPPORTED_OPERATION", SliceFailureReason.SlicerFailed),
        [-9] = ("CLI_COPY_OBJECTS_ERROR", SliceFailureReason.SlicerFailed),
        [-10] = ("CLI_SCALE_TO_FIT_ERROR", SliceFailureReason.ModelOutsideBuildVolume),
        [-11] = ("CLI_EXPORT_STL_ERROR", SliceFailureReason.SlicerFailed),
        [-12] = ("CLI_EXPORT_OBJ_ERROR", SliceFailureReason.SlicerFailed),
        [-13] = ("CLI_EXPORT_3MF_ERROR", SliceFailureReason.SlicerFailed),
        [-14] = ("CLI_OUT_OF_MEMORY", SliceFailureReason.ModelTooComplex),
        [-15] = ("CLI_3MF_NOT_SUPPORT_MACHINE_CHANGE", SliceFailureReason.ProfileNotCompatible),
        [-16] = ("CLI_3MF_NEW_MACHINE_NOT_SUPPORTED", SliceFailureReason.ProfileNotCompatible),
        [-17] = ("CLI_PROCESS_NOT_COMPATIBLE", SliceFailureReason.ProfileNotCompatible),
        [-18] = ("CLI_INVALID_VALUES_IN_3MF", SliceFailureReason.ProfileInvalid),
        [-19] = ("CLI_POSTPROCESS_NOT_SUPPORTED", SliceFailureReason.ProfileInvalid),
        [-20] = ("CLI_PRINTABLE_SIZE_REDUCED", SliceFailureReason.ModelOutsideBuildVolume),
        [-21] = ("CLI_OBJECT_ARRANGE_FAILED", SliceFailureReason.ModelOutsideBuildVolume),
        [-22] = ("CLI_OBJECT_ORIENT_FAILED", SliceFailureReason.SlicingEngineRejectedModel),
        [-23] = ("CLI_MODIFIED_PARAMS_TO_PRINTER", SliceFailureReason.ProfileInvalid),
        [-24] = ("CLI_FILE_VERSION_NOT_SUPPORTED", SliceFailureReason.ModelFileUnreadable),
        [-50] = ("CLI_NO_SUITABLE_OBJECTS", SliceFailureReason.NoPrintableObjects),
        [-51] = ("CLI_VALIDATE_ERROR", SliceFailureReason.ProfileInvalid),
        [-52] = ("CLI_OBJECTS_PARTLY_INSIDE", SliceFailureReason.ModelOutsideBuildVolume),
        [-53] = ("CLI_EXPORT_CACHE_DIRECTORY_CREATE_FAILED", SliceFailureReason.SlicerFailed),
        [-54] = ("CLI_EXPORT_CACHE_WRITE_FAILED", SliceFailureReason.SlicerFailed),
        [-55] = ("CLI_IMPORT_CACHE_NOT_FOUND", SliceFailureReason.SlicerFailed),
        [-56] = ("CLI_IMPORT_CACHE_DATA_CAN_NOT_USE", SliceFailureReason.SlicerFailed),
        [-57] = ("CLI_IMPORT_CACHE_LOAD_FAILED", SliceFailureReason.SlicerFailed),
        [-58] = ("CLI_SLICING_TIME_EXCEEDS_LIMIT", SliceFailureReason.SlicingTimedOut),
        [-59] = ("CLI_TRIANGLE_COUNT_EXCEEDS_LIMIT", SliceFailureReason.ModelTooComplex),
        [-60] = ("CLI_NO_SUITABLE_OBJECTS_AFTER_SKIP", SliceFailureReason.NoPrintableObjects),
        [-61] = ("CLI_FILAMENT_NOT_MATCH_BED_TYPE", SliceFailureReason.ProfileNotCompatible),
        [-62] = ("CLI_FILAMENTS_DIFFERENT_TEMP", SliceFailureReason.ProfileNotCompatible),
        [-63] = ("CLI_OBJECT_COLLISION_IN_SEQ_PRINT", SliceFailureReason.ToolpathConflict),
        [-64] = ("CLI_OBJECT_COLLISION_IN_LAYER_PRINT", SliceFailureReason.ToolpathConflict),
        [-65] = ("CLI_SPIRAL_MODE_INVALID_PARAMS", SliceFailureReason.ProfileInvalid),
        [-66] = ("CLI_FILAMENT_CAN_NOT_MAP", SliceFailureReason.ProfileNotCompatible),
        [-67] = ("CLI_ONLY_ONE_TPU_SUPPORTED", SliceFailureReason.ProfileNotCompatible),
        [-68] = ("CLI_FILAMENTS_NOT_SUPPORTED_BY_EXTRUDER", SliceFailureReason.ProfileNotCompatible),
        [-100] = ("CLI_SLICING_ERROR", SliceFailureReason.SlicingEngineRejectedModel),
        [-101] = ("CLI_GCODE_PATH_CONFLICTS", SliceFailureReason.ToolpathConflict),
        [-102] = ("CLI_GCODE_PATH_IN_UNPRINTABLE_AREA", SliceFailureReason.ModelOutsideBuildVolume),
    };

    /// <summary>
    /// What OrcaSlicer recorded in <c>result.json</c> before exiting.
    /// </summary>
    /// <param name="ReturnCode">The exact signed CLI return code, or <see langword="null"/>.</param>
    /// <param name="ErrorString">OrcaSlicer's own description of the failure, or <see langword="null"/>.</param>
    /// <param name="PlateWarnings">Per-plate warning text, when the exit path recorded any.</param>
    internal sealed record OrcaResult(int? ReturnCode, string? ErrorString, IReadOnlyList<string> PlateWarnings);

    /// <summary>The composed diagnostic plus its redacted classification.</summary>
    /// <param name="Reason">Client-safe classification.</param>
    /// <param name="Detail">Verbatim, admin-only diagnostic.</param>
    internal sealed record Diagnosis(SliceFailureReason Reason, string Detail);

    /// <summary>
    /// Reads and parses OrcaSlicer's <c>result.json</c> from an output directory.
    /// </summary>
    /// <param name="outputDirectory">The directory passed to OrcaSlicer as <c>--outputdir</c>.</param>
    /// <returns>The parsed result, or <see langword="null"/> when absent or unreadable.</returns>
    internal static OrcaResult? TryReadResult(string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        string path = Path.Join(outputDirectory, ResultFileName);
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxResultFileBytes)
            {
                // Bounded rather than caught: refusing to read an implausibly large document is what
                // keeps this path incapable of masking the failure being reported.
                return null;
            }

            return ParseResult(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable result file must never mask the failure being reported.
            return null;
        }
    }

    /// <summary>
    /// Parses a <c>result.json</c> document. Extracted from <see cref="TryReadResult"/> so the
    /// shape OrcaSlicer writes can be exercised without touching the filesystem.
    /// </summary>
    /// <param name="json">The raw document.</param>
    /// <returns>The parsed result, or <see langword="null"/> when it cannot be parsed.</returns>
    internal static OrcaResult? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? returnCode = root.TryGetProperty("return_code", out JsonElement code) &&
                              code.ValueKind == JsonValueKind.Number &&
                              code.TryGetInt32(out int parsedCode)
                ? parsedCode
                : null;

            string? errorString = root.TryGetProperty("error_string", out JsonElement error) &&
                                  error.ValueKind == JsonValueKind.String
                ? Normalize(error.GetString())
                : null;

            List<string> warnings = [];
            if (root.TryGetProperty("sliced_plates", out JsonElement plates) &&
                plates.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement plate in plates.EnumerateArray())
                {
                    if (plate.ValueKind == JsonValueKind.Object &&
                        plate.TryGetProperty("warning_message", out JsonElement warning) &&
                        warning.ValueKind == JsonValueKind.String)
                    {
                        string? text = Normalize(warning.GetString());
                        if (!string.IsNullOrEmpty(text) && !warnings.Contains(text, StringComparer.Ordinal))
                        {
                            warnings.Add(text);
                        }
                    }
                }
            }

            return new OrcaResult(returnCode, errorString, warnings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the signed CLI return code for a run.
    /// </summary>
    /// <remarks>
    /// <c>result.json</c> is authoritative and is preferred whenever present, because a POSIX exit
    /// status is only the low 8 bits: OrcaSlicer's -100 is observed as 156. Reconstructing the sign
    /// from the exit status alone is therefore ambiguous in principle with signal terminations
    /// (128 + signal occupies 129-159), so it is only used as a fallback and only for values that
    /// correspond to a code this worker actually knows.
    /// <para>
    /// The residual overlap is 154-156 (= -102..-100) against signals 26-28. It is accepted rather
    /// than excluded, because excluding it would discard the classification for the exact failure
    /// this was built for (exit 156) on any run whose <c>result.json</c> is missing, and the signals
    /// involved cannot realistically terminate this process: SIGWINCH (28 → 156) is ignored by
    /// default and so cannot kill anything, while SIGVTALRM (26) and SIGPROF (27) only fire when an
    /// interval timer has been armed, which neither the worker nor a headless slice does. The
    /// signals that genuinely do kill a container process — SIGKILL (9 → 137, e.g. an OOM kill),
    /// SIGSEGV (11 → 139), SIGTERM (15 → 143) — decode to values absent from the table and so stay
    /// unclassified, which is the outcome that matters.
    /// </para>
    /// </remarks>
    /// <param name="result">Parsed <c>result.json</c>, when available.</param>
    /// <param name="exitCode">The observed process exit status.</param>
    /// <returns>The signed CLI return code, or <see langword="null"/> when it cannot be determined.</returns>
    internal static int? ResolveReturnCode(OrcaResult? result, int exitCode)
    {
        if (result?.ReturnCode is int recorded)
        {
            return recorded;
        }

        if (ExitCodes.ContainsKey(exitCode))
        {
            return exitCode;
        }

        int signed = exitCode - 256;
        return exitCode is > 128 and < 256 && ExitCodes.ContainsKey(signed) ? signed : null;
    }

    /// <summary>
    /// Classifies a signed CLI return code into a redacted, client-safe reason.
    /// </summary>
    /// <param name="returnCode">The signed CLI return code, or <see langword="null"/>.</param>
    /// <returns>The classification, defaulting to <see cref="SliceFailureReason.SlicerFailed"/>.</returns>
    internal static SliceFailureReason Classify(int? returnCode) =>
        returnCode is int code && ExitCodes.TryGetValue(code, out (string Symbol, SliceFailureReason Reason) entry)
            ? entry.Reason
            : SliceFailureReason.SlicerFailed;

    /// <summary>
    /// Collects every informative line from the slicer's console output.
    /// </summary>
    /// <remarks>
    /// This replaces a <c>FirstOrDefault</c> scan that kept a single line and discarded the rest.
    /// <c>[error]</c>-prefixed lines are preferred when the engine emits them, but the CLI's
    /// slicing-failure path emits none, so plain lines mentioning an error/failure are collected too
    /// — all of them, in order, deduplicated, rather than only the first.
    /// <para>
    /// Read through a <see cref="StringReader"/> rather than <c>Split('\n')</c>: a failing run can
    /// emit a very large log, and materializing an array of every line would allocate a second copy
    /// of it. This path runs only while reporting a failure, so it must not be able to turn a
    /// diagnosable failure into an out-of-memory crash that loses it.
    /// </para>
    /// </remarks>
    /// <param name="output">Combined console output from the slicer process.</param>
    /// <returns>The informative lines, in the order the slicer emitted them.</returns>
    internal static IReadOnlyList<string> CollectInformativeLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        List<string> tagged = [];
        List<string> plain = [];

        using StringReader reader = new(output);
        while (reader.ReadLine() is string raw)
        {
            // Both buckets are full, so nothing later can change the result.
            if (tagged.Count >= MaxStreamLines && plain.Count >= MaxStreamLines)
            {
                break;
            }

            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int marker = line.IndexOf("[error]", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                string stripped = line[(marker + "[error]".Length)..].TrimStart(':', ' ');
                if (stripped.Length > 0 &&
                    tagged.Count < MaxStreamLines &&
                    !tagged.Contains(stripped, StringComparer.Ordinal))
                {
                    tagged.Add(stripped);
                }

                continue;
            }

            if (plain.Count < MaxStreamLines &&
                (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("fail", StringComparison.OrdinalIgnoreCase)) &&
                !plain.Contains(line, StringComparer.Ordinal))
            {
                plain.Add(line);
            }
        }

        return tagged.Count > 0 ? tagged : plain;
    }

    /// <summary>
    /// Composes the admin-facing diagnostic and the redacted classification for a failed run.
    /// </summary>
    /// <param name="exitCode">The observed process exit status.</param>
    /// <param name="output">Combined console output from the slicer process.</param>
    /// <param name="result">Parsed <c>result.json</c>, when available.</param>
    /// <returns>The composed diagnosis.</returns>
    internal static Diagnosis Describe(int exitCode, string? output, OrcaResult? result)
    {
        int? returnCode = ResolveReturnCode(result, exitCode);
        SliceFailureReason reason = Classify(returnCode);

        StringBuilder detail = new();
        _ = detail.Append(CultureInfo.InvariantCulture, $"OrcaSlicer failed with exit code {exitCode}");

        if (returnCode is int code)
        {
            string symbol = ExitCodes.TryGetValue(code, out (string Symbol, SliceFailureReason Reason) entry)
                ? entry.Symbol
                : "unknown";
            _ = detail.Append(CultureInfo.InvariantCulture, $" ({symbol}, {code})");
        }

        // OrcaSlicer's own description leads, because it is the only authoritative statement of what
        // the engine decided; the console text is supporting evidence appended after it.
        if (!string.IsNullOrEmpty(result?.ErrorString))
        {
            _ = detail.Append(CultureInfo.InvariantCulture, $": {result.ErrorString}");
        }

        if (result is { PlateWarnings.Count: > 0 })
        {
            _ = detail.Append(CultureInfo.InvariantCulture, $" | plate warnings: {string.Join("; ", result.PlateWarnings)}");
        }

        IReadOnlyList<string> lines = CollectInformativeLines(output);
        if (lines.Count > 0)
        {
            _ = detail.Append(CultureInfo.InvariantCulture, $" | slicer output: {string.Join("; ", lines)}");
        }
        else if (string.IsNullOrEmpty(result?.ErrorString))
        {
            _ = detail.Append(" (the slicer produced no diagnostic output)");
        }

        string composed = detail.ToString();
        if (composed.Length > MaxDetailLength)
        {
            composed = composed[..MaxDetailLength] + "…";
        }

        return new Diagnosis(reason, composed);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
