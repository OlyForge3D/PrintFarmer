using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Farm.Web.Api.Services.Gcode.Safety;

/// <summary>Default <see cref="IGcodeSafetyValidator"/>.</summary>
/// <remarks>
/// This is the general, calibration-independent g-code safety interpreter extracted from the
/// calibration-only validator originally added by PR #947. It performs no provenance/manifest/digest
/// matching — only physical safety checks against an authoritative <see cref="GcodeSafetyLimits"/>
/// envelope, plus redaction and structural scanning that is harmless to run against any g-code.
/// </remarks>
public sealed class GcodeSafetyValidator : IGcodeSafetyValidator
{
    /// <summary>Absolute pressure advance ceiling accepted in any validated program.</summary>
    public const decimal AbsolutePressureAdvanceCeiling = 2.0m;

    /// <summary>Absolute retraction ceiling, in millimetres, accepted in any validated program.</summary>
    public const decimal AbsoluteRetractionCeiling = 10.0m;

    /// <summary>Volumetric flow tolerance, in mm³/s, applied before a move is rejected.</summary>
    public const decimal VolumetricFlowTolerance = 0.05m;

    /// <summary>Coordinate tolerance, in millimetres, applied before a move is rejected.</summary>
    public const decimal CoordinateTolerance = 0.01m;

    /// <summary>The forbidden firmware tuning tower macro.</summary>
    public const string TuningTower = "TUNING_TOWER";

    /// <summary>Marks a safe transition between calibration segments.</summary>
    private const string SegmentTransitionMarker = ";PF_SEG_TRANSITION";

    /// <summary>Marks the start of a calibration segment.</summary>
    private const string SegmentBeginMarker = ";PF_SEG_BEGIN";

    /// <summary>Marks the end of a calibration segment.</summary>
    private const string SegmentEndMarker = ";PF_SEG_END";

    private static readonly string[] HostCommandMarkers =
    [
        "RUN_SHELL_COMMAND",
        "/bin/sh",
        "cmd.exe",
        "powershell ",
        "curl ",
        "wget ",
        "nc ",
        "ssh ",
    ];

    private static readonly string[] CredentialMarkers =
    [
        "api_key",
        "apikey",
        "password",
        "authorization:",
        "bearer ",
        "client_secret",
        "x-worker-key",
    ];

    /// <inheritdoc/>
    public GcodeSafetyResult<GcodeSafetyReport> Validate(GcodeSafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Gcode))
        {
            return GcodeSafetyResult.Failure<GcodeSafetyReport>(
                GcodeSafetyProblemCodes.Malformed,
                "gcode",
                "The g-code program is empty.");
        }

        List<GcodeSafetyProblem> problems = [];
        InterpreterState state = Interpret(request, problems);
        ValidateEnvelope(state, problems);

        if (problems.Count > 0)
        {
            return GcodeSafetyResult.Failure<GcodeSafetyReport>(problems);
        }

        string sha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(request.Gcode)))
            .ToLowerInvariant();
        return GcodeSafetyResult.Success(new GcodeSafetyReport(
            request.Checkpoint,
            sha256,
            state.CommandCount,
            DateTime.UtcNow));
    }

    private static InterpreterState Interpret(
        GcodeSafetyRequest request,
        List<GcodeSafetyProblem> problems)
    {
        InterpreterState state = new(request.Limits);
        int lineNumber = 0;

        foreach (string rawLine in request.Gcode.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            ScanRedaction(line, lineNumber, problems);
            if (line[0] == ';')
            {
                TrackMarker(state, line, lineNumber, problems);
                continue;
            }

            string command = ReadCommand(line);
            if (line.Contains(TuningTower, StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.TuningTowerForbidden,
                    Field(lineNumber),
                    "G-code must never drive a firmware tuning tower.");
                continue;
            }

            if (request.AllowedCommands is not null && !request.AllowedCommands.Contains(command))
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.CommandNotAllowlisted,
                    Field(lineNumber),
                    "G-code contains a command outside the trusted allowlist.");
                continue;
            }

            state.CommandCount++;
            Execute(state, command, line, lineNumber, problems);
        }

        return state;
    }

    private static void Execute(
        InterpreterState state,
        string command,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        switch (command)
        {
            case "G28":
                state.Homed = true;
                state.X = 0m;
                state.Y = 0m;
                state.Z = 0m;
                break;
            case "G90":
                state.AbsolutePositioning = true;
                break;
            case "G91":
                state.AbsolutePositioning = false;
                break;
            case "M82":
                state.AbsoluteExtrusion = true;
                break;
            case "M83":
                state.AbsoluteExtrusion = false;
                break;
            case "G92":
                if (TryReadParameter(line, 'E', out decimal reset))
                {
                    state.E = reset;
                }

                break;
            case "M104":
            case "M109":
                ApplyNozzleTemperature(state, line, lineNumber, problems);
                break;
            case "M140":
            case "M190":
                ApplyBedTemperature(state, line, lineNumber, problems);
                break;
            case "M141":
            case "M191":
                ApplyChamberTemperature(state, line, lineNumber, problems);
                break;
            case "M204":
                ApplyAcceleration(state, line, lineNumber, problems);
                break;
            case "SET_VELOCITY_LIMIT":
                ApplyVelocityLimit(state, line, lineNumber, problems);
                break;
            case "SET_PRESSURE_ADVANCE":
                ApplyPressureAdvance(state, line, lineNumber, problems);
                break;
            case "TURN_OFF_HEATERS":
                state.HeatersOff = true;
                state.NozzleTemperature = 0;
                state.BedTemperature = 0;
                break;
            case "M84":
                state.MotorsOff = true;
                break;
            case "G0":
            case "G1":
                ApplyMove(state, line, lineNumber, problems);
                break;
            default:
                break;
        }
    }

    private static void ApplyNozzleTemperature(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        state.NozzleTemperature = value;
        if (state.Limits.Toolhead.NozzleMaxTemperatureCelsius is null &&
            state.Limits.Toolhead.HotendMaxTemperatureCelsius is null)
        {
            return;
        }

        int ceiling = state.Limits.Toolhead.NozzleMaxTemperatureCelsius ??
            state.Limits.Toolhead.HotendMaxTemperatureCelsius ??
            0;
        if (value > ceiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.TemperatureAboveLimit,
                Field(lineNumber),
                "A commanded nozzle temperature exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyBedTemperature(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        state.BedTemperature = value;
        if (state.Limits.Machine.MaxBedTemperatureCelsius is not { } ceiling)
        {
            return;
        }

        if (value > ceiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.TemperatureAboveLimit,
                Field(lineNumber),
                "A commanded bed temperature exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyChamberTemperature(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        if (state.Limits.Machine.HasHeatedChamber != true ||
            value > (state.Limits.Machine.MaxChamberTemperatureCelsius ?? 0))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.TemperatureAboveLimit,
                Field(lineNumber),
                "A commanded chamber temperature is unsupported or above the authoritative ceiling.");
        }
    }

    private static void ApplyAcceleration(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (!TryReadParameter(line, 'S', out decimal value))
        {
            return;
        }

        if (state.Limits.Machine.MaxAcceleration is not { } ceiling)
        {
            return;
        }

        if (value > ceiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.AccelerationAboveLimit,
                Field(lineNumber),
                "A commanded acceleration exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyVelocityLimit(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (TryReadNamedParameter(line, "VELOCITY=", out decimal velocity) &&
            state.Limits.Machine.MaxTravelSpeedMillimetersPerSecond is { } speedCeiling &&
            velocity > speedCeiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.SpeedAboveLimit,
                Field(lineNumber),
                "A commanded velocity limit exceeds the authoritative ceiling.");
        }

        if (TryReadNamedParameter(line, "ACCEL=", out decimal acceleration) &&
            state.Limits.Machine.MaxAcceleration is { } accelCeiling &&
            acceleration > accelCeiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.AccelerationAboveLimit,
                Field(lineNumber),
                "A commanded acceleration limit exceeds the authoritative ceiling.");
        }
    }

    private static void ApplyPressureAdvance(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (!TryReadNamedParameter(line, "ADVANCE=", out decimal value))
        {
            return;
        }

        state.PressureAdvance = value;
        decimal ceiling = Math.Min(
            AbsolutePressureAdvanceCeiling,
            state.Limits.Toolhead.IsDirectDrive == true ? 0.5m : 2.0m);
        if (value < 0m || value > ceiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.PressureAdvanceOutOfRange,
                Field(lineNumber),
                "A commanded pressure advance value is outside the safe range.");
        }
    }

    private static void ApplyMove(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        decimal previousX = state.X;
        decimal previousY = state.Y;
        bool hasX = TryReadParameter(line, 'X', out decimal x);
        bool hasY = TryReadParameter(line, 'Y', out decimal y);
        bool hasZ = TryReadParameter(line, 'Z', out decimal z);
        bool hasE = TryReadParameter(line, 'E', out decimal e);
        bool hasF = TryReadParameter(line, 'F', out decimal feedRate);

        if (state.AbsolutePositioning)
        {
            if (hasX)
            {
                state.X = x;
            }

            if (hasY)
            {
                state.Y = y;
            }

            if (hasZ)
            {
                state.Z = z;
            }
        }
        else
        {
            if (hasX)
            {
                state.X += x;
            }

            if (hasY)
            {
                state.Y += y;
            }

            if (hasZ)
            {
                state.Z += z;
            }
        }

        decimal extrusion = 0m;
        if (hasE)
        {
            extrusion = state.AbsoluteExtrusion ? e - state.E : e;
            state.E = state.AbsoluteExtrusion ? e : state.E + e;
        }

        if (hasF)
        {
            decimal speed = feedRate / 60m;
            int? ceiling = extrusion > 0m
                ? state.Limits.Machine.MaxPrintSpeedMillimetersPerSecond
                : state.Limits.Machine.MaxTravelSpeedMillimetersPerSecond;
            if (ceiling is { } value && speed > value)
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.SpeedAboveLimit,
                    Field(lineNumber),
                    "A commanded feed rate exceeds the authoritative speed ceiling.");
            }

            state.FeedRate = feedRate;
        }

        if (extrusion < 0m)
        {
            decimal retraction = -extrusion;
            state.TransitionRetracted = true;
            decimal ceiling = Math.Min(
                AbsoluteRetractionCeiling,
                state.Limits.Toolhead.IsDirectDrive == true ? 3.0m : 10.0m);
            if (retraction > ceiling)
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.RetractionAboveLimit,
                    Field(lineNumber),
                    "A commanded retraction exceeds the safe range.");
            }
        }

        if (extrusion > 0m)
        {
            state.HasExtruded = true;
            if (!state.Homed)
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.UnsafeInitialization,
                    Field(lineNumber),
                    "G-code extrudes before the printer is homed.");
            }

            if (state.NozzleTemperature <= 0m)
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.UnsafeInitialization,
                    Field(lineNumber),
                    "G-code extrudes before a nozzle temperature is commanded.");
            }

            ValidateVolumetricFlow(state, previousX, previousY, extrusion, lineNumber, problems);
        }

        if (state.AbsolutePositioning && (hasX || hasY))
        {
            ValidatePosition(state, lineNumber, problems);
        }

        if (hasZ && state.Z > (state.Limits.Bed.SizeZMillimeters ?? decimal.MaxValue))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.MotionOutsideBuildVolume,
                Field(lineNumber),
                "A commanded Z height exceeds the authoritative build volume.");
        }
    }

    private static void ValidateVolumetricFlow(
        InterpreterState state,
        decimal previousX,
        decimal previousY,
        decimal extrusion,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (state.Limits.Print.FilamentDiameterMillimeters is not { } filamentDiameter ||
            state.Limits.Print.MaxVolumetricFlow is not { } maxVolumetricFlow)
        {
            return;
        }

        decimal deltaX = state.X - previousX;
        decimal deltaY = state.Y - previousY;
        decimal distance = Math.Abs(deltaX) + Math.Abs(deltaY);
        if (distance <= 0m || state.FeedRate <= 0m)
        {
            return;
        }

        decimal radius = filamentDiameter / 2m;
        decimal filamentArea = 3.1415926535897932384626433833m * radius * radius;
        decimal volume = extrusion * filamentArea;
        decimal speed = state.FeedRate / 60m;
        decimal flow = volume / distance * speed;
        decimal ceiling = maxVolumetricFlow + VolumetricFlowTolerance;
        if (flow > ceiling)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.VolumetricFlowAboveLimit,
                Field(lineNumber),
                "A commanded move exceeds the authoritative volumetric flow ceiling.");
        }
    }

    private static void ValidatePosition(
        InterpreterState state,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        GcodeSafetyBedLimits bed = state.Limits.Bed;
        decimal originX = bed.OriginXMillimeters ?? 0m;
        decimal originY = bed.OriginYMillimeters ?? 0m;
        if (bed.SizeXMillimeters is { } sizeX && bed.SizeYMillimeters is { } sizeY &&
            (state.X < originX - CoordinateTolerance ||
                state.X > originX + sizeX + CoordinateTolerance ||
                state.Y < originY - CoordinateTolerance ||
                state.Y > originY + sizeY + CoordinateTolerance))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.MotionOutsideBuildVolume,
                Field(lineNumber),
                "A commanded move falls outside the authoritative build volume.");
            return;
        }

        if (bed.PrintablePolygon.Count >= 3 &&
            !GcodeSafetyGeometry.ContainsPoint(bed.PrintablePolygon, state.X, state.Y))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.MotionOutsidePrintablePolygon,
                Field(lineNumber),
                "A commanded move falls outside the authoritative printable polygon.");
            return;
        }

        if (bed.ExcludedRegions.Any(region =>
                region.Polygon.Count >= 3 &&
                GcodeSafetyGeometry.ContainsPoint(region.Polygon, state.X, state.Y)))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.MotionInsideExcludedRegion,
                Field(lineNumber),
                "A commanded move enters an authoritative excluded region.");
        }
    }

    private static void TrackMarker(
        InterpreterState state,
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (line.StartsWith(SegmentTransitionMarker, StringComparison.Ordinal))
        {
            state.SawTransition = true;
            state.TransitionRetracted = false;
            return;
        }

        if (line.StartsWith(SegmentBeginMarker, StringComparison.Ordinal))
        {
            state.SegmentDepth++;
            if (state.SegmentsSeen > 0 && (!state.SawTransition || !state.TransitionRetracted))
            {
                Add(
                    problems,
                    GcodeSafetyProblemCodes.UnsafeSegmentTransition,
                    Field(lineNumber),
                    "A segment starts without a retracting safe transition.");
            }

            state.SegmentsSeen++;
            state.SawTransition = false;
            return;
        }

        if (line.StartsWith(SegmentEndMarker, StringComparison.Ordinal))
        {
            state.SegmentDepth--;
        }
    }

    private static void ValidateEnvelope(InterpreterState state, List<GcodeSafetyProblem> problems)
    {
        if (!state.Homed)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.UnsafeInitialization,
                "gcode.initialization",
                "G-code never homes the printer.");
        }

        if (!state.HeatersOff || !state.MotorsOff)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.MissingFinalReset,
                "gcode.finalization",
                "G-code does not end with a safe heater and motor reset.");
        }

        if (state.SegmentDepth != 0)
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.UnsafeSegmentTransition,
                "gcode.segments",
                "G-code contains an unbalanced segment marker.");
        }
    }

    private static void ScanRedaction(
        string line,
        int lineNumber,
        List<GcodeSafetyProblem> problems)
    {
        if (HostCommandMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.ContainsHostCommand,
                Field(lineNumber),
                "G-code contains a shell, host or network command.");
            return;
        }

        if (CredentialMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.ContainsCredential,
                Field(lineNumber),
                "G-code contains a credential-bearing token.");
            return;
        }

        if (line.Contains("://", StringComparison.Ordinal))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.ContainsPrivateUrl,
                Field(lineNumber),
                "G-code contains a URL.");
            return;
        }

        if (ContainsAbsolutePath(line))
        {
            Add(
                problems,
                GcodeSafetyProblemCodes.ContainsFilesystemPath,
                Field(lineNumber),
                "G-code contains an absolute filesystem path.");
        }
    }

    private static bool ContainsAbsolutePath(string line)
    {
        foreach (string token in line.Split(
            [' ', '\t', '=', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = token.TrimStart(';');
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                (candidate.Length >= 3 &&
                    char.IsAsciiLetter(candidate[0]) &&
                    candidate[1] == ':' &&
                    (candidate[2] == '\\' || candidate[2] == '/')) ||
                (candidate.Length > 1 && candidate[0] == '/' && char.IsAsciiLetter(candidate[1])))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadCommand(string line)
    {
        int end = line.IndexOfAny([' ', '\t', ';']);
        string token = end < 0 ? line : line[..end];
        return token.ToUpperInvariant();
    }

    private static bool TryReadParameter(string line, char parameter, out decimal value)
    {
        value = 0m;
        for (int index = 1; index < line.Length; index++)
        {
            if (char.ToUpperInvariant(line[index]) != parameter ||
                (line[index - 1] != ' ' && line[index - 1] != '\t'))
            {
                continue;
            }

            int start = index + 1;
            int end = start;
            while (end < line.Length &&
                (char.IsAsciiDigit(line[end]) || line[end] == '.' || line[end] == '-' ||
                    line[end] == '+'))
            {
                end++;
            }

            return end > start &&
                decimal.TryParse(
                    line.AsSpan(start, end - start),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        return false;
    }

    private static bool TryReadNamedParameter(string line, string key, out decimal value)
    {
        value = 0m;
        int start = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += key.Length;
        int end = start;
        while (end < line.Length &&
            (char.IsAsciiDigit(line[end]) || line[end] == '.' || line[end] == '-' ||
                line[end] == '+'))
        {
            end++;
        }

        return end > start &&
            decimal.TryParse(
                line.AsSpan(start, end - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static string Field(int lineNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"gcode.line[{lineNumber}]");

    private static void Add(
        List<GcodeSafetyProblem> problems,
        string code,
        string field,
        string message)
    {
        // One reason per code keeps a rejected large program from producing thousands of rows.
        if (!problems.Any(problem => string.Equals(problem.Code, code, StringComparison.Ordinal)))
        {
            problems.Add(new(code, field, message));
        }
    }

    private sealed class InterpreterState(GcodeSafetyLimits limits)
    {
        public GcodeSafetyLimits Limits { get; } = limits;

        public bool AbsolutePositioning { get; set; } = true;

        public bool AbsoluteExtrusion { get; set; } = true;

        public bool Homed { get; set; }

        public bool HeatersOff { get; set; }

        public bool MotorsOff { get; set; }

        public bool HasExtruded { get; set; }

        public bool SawTransition { get; set; }

        public bool TransitionRetracted { get; set; }

        public int SegmentsSeen { get; set; }

        public decimal X { get; set; }

        public decimal Y { get; set; }

        public decimal Z { get; set; }

        public decimal E { get; set; }

        public decimal FeedRate { get; set; }

        public decimal NozzleTemperature { get; set; }

        public decimal BedTemperature { get; set; }

        public decimal PressureAdvance { get; set; }

        public int SegmentDepth { get; set; }

        public int CommandCount { get; set; }
    }
}
