using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Three-state outcome of validating a scanned spool against a printer toolhead's expected
/// material for the guided filament swap flow (GitHub issue OlyForge3D/PrintFarmer#710).
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>feature-local</b> enum with a dedicated converter so it serializes to the
/// exact lowercase wire tokens <c>ok</c> / <c>mismatch</c> / <c>unknown</c>. It deliberately
/// does NOT rely on the global <c>JsonStringEnumConverter</c> (which would emit PascalCase
/// names) and never changes global enum serialization.
/// </para>
/// <para>
/// Semantics:
/// <list type="bullet">
///   <item><description><see cref="Ok"/> — the scanned material matches the expected
///     requirement, or there is no requirement to satisfy (safe to bind).</description></item>
///   <item><description><see cref="Mismatch"/> — a concrete requirement exists and the
///     scanned material differs (bind blocked unless explicitly overridden).</description></item>
///   <item><description><see cref="Unknown"/> — validation could not be performed because the
///     scanned spool could not be resolved in Spoolman or carries no material metadata. This is
///     NOT a mismatch and MUST NOT be overridden.</description></item>
/// </list>
/// </para>
/// </remarks>
[JsonConverter(typeof(SwapValidationStatusJsonConverter))]
public enum SwapValidationStatus
{
    /// <summary>Scanned material matches, or no requirement exists. Safe to bind.</summary>
    Ok,

    /// <summary>A concrete requirement exists and the scanned material differs.</summary>
    Mismatch,

    /// <summary>Validation could not be performed (spool unresolved / no material metadata).</summary>
    Unknown,
}

/// <summary>
/// Serializes <see cref="SwapValidationStatus"/> to the exact lowercase wire tokens
/// <c>ok</c>, <c>mismatch</c>, and <c>unknown</c> (and parses them case-insensitively).
/// </summary>
public sealed class SwapValidationStatusJsonConverter : JsonConverter<SwapValidationStatus>
{
    /// <inheritdoc />
    public override SwapValidationStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return value?.Trim().ToLowerInvariant() switch
        {
            "ok" => SwapValidationStatus.Ok,
            "mismatch" => SwapValidationStatus.Mismatch,
            "unknown" => SwapValidationStatus.Unknown,
            _ => SwapValidationStatus.Unknown,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SwapValidationStatus value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string token = value switch
        {
            SwapValidationStatus.Ok => "ok",
            SwapValidationStatus.Mismatch => "mismatch",
            SwapValidationStatus.Unknown => "unknown",
            _ => "unknown",
        };

        writer.WriteStringValue(token);
    }
}
