using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;

namespace Farm.Infrastructure.Dtos.PartsInventory;

/// <summary>Response DTO for a printed-part SKU.</summary>
public record PartInventoryResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    string? ModelFileRef,
    Guid? DefaultBinId,
    string? DefaultBinCode,
    string? DefaultBinName,
    int OnHand,
    int ReorderPoint,
    bool NeedsReorder,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Response DTO for a printed-part storage bin.</summary>
public record BinResponse(
    Guid Id,
    string Code,
    string Name,
    string? Location,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Response DTO for a single ledger entry.</summary>
public record PartAdjustmentResponse(
    Guid Id,
    Guid PartInventoryId,
    string Sku,
    Guid? BinId,
    string? BinCode,
    int Delta,
    int ResultingBalance,
    [property: JsonConverter(typeof(PartAdjustmentReasonConverter))] PartAdjustmentReason Reason,
    Guid? PrintJobId,
    string? OperationKey,
    string? Notes,
    string? UserId,
    DateTime CreatedAt);

/// <summary>Response DTO for a job-output → SKU mapping.</summary>
public record PartOutputMappingResponse(
    Guid Id,
    Guid PartInventoryId,
    string Sku,
    Guid? GcodeFileId,
    Guid? PrintProjectFileId,
    int Quantity,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Response DTO for the harvest action on a completed print job.</summary>
public record HarvestJobResponse(
    Guid PrintJobId,
    DateTime HarvestedAt,
    Guid? BinId,
    string? BinCode,
    bool AlreadyHarvested,
    IReadOnlyList<PartAdjustmentResponse> Adjustments,
    IReadOnlyList<HarvestOutputResponse> Outputs);

/// <summary>Persisted final output returned by a successful or replayed harvest.</summary>
public record HarvestOutputResponse(
    int Sequence,
    Guid PartInventoryId,
    string PartSku,
    int Quantity,
    Guid? ExpectedBinId,
    string? ExpectedBinCode,
    Guid ActualBinId,
    string ActualBinCode,
    PartHarvestOutputOrigin Origin,
    Guid? SourceFileId,
    Guid? SourceMappingId,
    bool OverrideApplied,
    string? OverrideReason,
    DateTime CreatedAt);

/// <summary>Single row in the canonical wrong-bin ProblemDetails extension.</summary>
public record WrongBinMismatchResponse(
    string PartSku,
    string? ExpectedBinCode,
    string ScannedBinCode);

/// <summary>Typed wrong-bin details returned before stock or harvest mutation.</summary>
public record WrongBinResponse(IReadOnlyList<WrongBinMismatchResponse> Mismatches);

/// <summary>Typed missing-mapping details used to build canonical ProblemDetails.</summary>
public record PartMappingRequiredResponse(
    Guid JobId,
    Guid? ProjectFileId,
    Guid? GcodeFileId,
    string Guidance);

/// <summary>
/// Discriminated 409 conflict envelope for <c>POST /api/job-queue/{id}/harvest</c> (issue #2294).
/// Extends the base <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> members
/// (<c>type</c>/<c>title</c>/<c>status</c>/<c>detail</c>/<c>instance</c>) with the discriminator
/// and code-specific extension properties the server always emits for a harvest conflict --
/// previously only reachable via <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails.Extensions"/>
/// and therefore invisible to the OpenAPI schema and any generated client (including iOS).
/// <see cref="Code"/> selects which remaining properties are meaningful: <c>"wrongBin"</c>
/// populates <see cref="Mismatches"/>; <c>"partMappingRequired"</c> populates
/// <see cref="JobId"/>, <see cref="ProjectFileId"/>, <see cref="GcodeFileId"/>, and
/// <see cref="Guidance"/>. Properties that do not apply to the current <see cref="Code"/> are left
/// unset and, under the global <c>DefaultIgnoreCondition = WhenWritingNull</c> controller JSON
/// options, are omitted from the wire payload entirely -- matching the corpus fixtures, where e.g.
/// <c>harvest.wrong-bin.json</c> has no <c>jobId</c>/<c>projectFileId</c>/<c>gcodeFileId</c>/
/// <c>guidance</c> keys at all.
/// </summary>
public sealed class HarvestConflictResponse : Microsoft.AspNetCore.Mvc.ProblemDetails
{
    /// <summary>Discriminator: <c>"wrongBin"</c> or <c>"partMappingRequired"</c>.</summary>
    public required string Code { get; set; }

    /// <summary>Populated when <see cref="Code"/> is <c>"wrongBin"</c>; otherwise omitted.</summary>
    public IReadOnlyList<WrongBinMismatchResponse>? Mismatches { get; set; }

    /// <summary>Populated when <see cref="Code"/> is <c>"partMappingRequired"</c>; otherwise omitted.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Populated when <see cref="Code"/> is <c>"partMappingRequired"</c>; otherwise omitted.</summary>
    public Guid? ProjectFileId { get; set; }

    /// <summary>
    /// Populated when <see cref="Code"/> is <c>"partMappingRequired"</c>; the corpus fixture requires
    /// the key to be present with an explicit JSON <c>null</c> when no gcode file exists yet, and
    /// absent entirely for any other <see cref="Code"/>. A plain <c>Guid?</c> cannot express both
    /// "explicitly null" and "entirely absent" under a single <c>DefaultIgnoreCondition</c> policy, so
    /// this uses <see cref="OptionalGuid"/>: left at its default (<see cref="OptionalGuid.Absent"/>)
    /// for <c>"wrongBin"</c>, which <see cref="JsonIgnoreCondition.WhenWritingDefault"/> then omits;
    /// set to <see cref="OptionalGuid.Of"/> (even wrapping a <see langword="null"/> <see cref="Guid"/>)
    /// for <c>"partMappingRequired"</c>, which is never equal to the struct default and therefore always
    /// serializes, as an explicit <c>null</c> when the wrapped value is itself <see langword="null"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptionalGuid GcodeFileId { get; set; }

    /// <summary>Populated when <see cref="Code"/> is <c>"partMappingRequired"</c>; otherwise omitted.</summary>
    public string? Guidance { get; set; }
}

/// <summary>
/// Wraps a nullable <see cref="Guid"/> so JSON serialization can distinguish "property entirely
/// absent from the payload" (this struct's default value) from "property present with an explicit
/// JSON <c>null</c>" (any other value, including one wrapping a <see langword="null"/>
/// <see cref="Guid"/>). Needed for <see cref="HarvestConflictResponse.GcodeFileId"/>: see that
/// property's remarks for why a plain <c>Guid?</c> cannot express this distinction.
/// </summary>
[JsonConverter(typeof(OptionalGuidJsonConverter))]
public readonly struct OptionalGuid : IEquatable<OptionalGuid>
{
    /// <summary>The struct's default value: no value was ever supplied, so the property is omitted.</summary>
    public static readonly OptionalGuid Absent;

    private OptionalGuid(Guid? value)
    {
        HasValue = true;
        Value = value;
    }

    /// <summary><see langword="true"/> for any instance created via <see cref="Of"/>; <see langword="false"/> for <see cref="Absent"/>.</summary>
    public bool HasValue { get; }

    /// <summary>The wrapped, possibly-<see langword="null"/>, value.</summary>
    public Guid? Value { get; }

    /// <summary>Wraps <paramref name="value"/> (including <see langword="null"/>) as an explicitly-present value.</summary>
    public static OptionalGuid Of(Guid? value) => new(value);

    public bool Equals(OptionalGuid other) => HasValue == other.HasValue && Value == other.Value;

    public override bool Equals(object? obj) => obj is OptionalGuid other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(HasValue, Value);

    public static bool operator ==(OptionalGuid left, OptionalGuid right) => left.Equals(right);

    public static bool operator !=(OptionalGuid left, OptionalGuid right) => !left.Equals(right);
}

/// <summary>Serializes <see cref="OptionalGuid.Value"/> as either a GUID string or an explicit JSON <c>null</c>.</summary>
public sealed class OptionalGuidJsonConverter : JsonConverter<OptionalGuid>
{
    public override OptionalGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return OptionalGuid.Of(null);
        }

        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out Guid guid))
        {
            throw new JsonException("Expected a GUID string or null for OptionalGuid.");
        }

        return OptionalGuid.Of(guid);
    }

    public override void Write(Utf8JsonWriter writer, OptionalGuid value, JsonSerializerOptions options)
    {
        if (value.Value is { } guid)
        {
            writer.WriteStringValue(guid);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// 409 conflict envelope for <c>POST /api/parts-inventory/{sku}/adjust</c> (issue #2294). The
/// adjust endpoint's conflict path (e.g. adjusting a job that was already fully reconciled) never
/// raises the wrong-bin/mapping-required codes above -- it only ever carries a single human-readable
/// <see cref="Message"/> -- so it gets its own accurate, narrower DTO rather than being forced into
/// <see cref="HarvestConflictResponse"/>'s richer, code-discriminated shape.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is nullable (not <c>required</c>) even though the controller's own
/// in-action conflict path always supplies one: the shared <c>[Idempotent]</c> filter can also
/// short-circuit this same action with a <c>409</c> before the controller runs, and that
/// filter-level payload is a plain <c>ProblemDetails</c> with a <c>code</c> extension and no
/// <c>message</c> at all (see <c>IdempotencyProblemDetails.HashConflict</c>/<c>InProgress</c>).
/// A <c>required</c> <see cref="Message"/> would make the declared
/// <c>[ProducesResponseType(typeof(PartAdjustmentConflictResponse), 409)]</c> schema fail to
/// describe that filter-emitted response (Bishop review, issue #2294); making the property
/// optional keeps the schema accurate for every 409 this action can actually emit without
/// changing what the controller's own conflict path writes on the wire.
/// </remarks>
public sealed record PartAdjustmentConflictResponse(string? Message);

/// <summary>Item in a caller-supplied harvest override / mapping fallback.</summary>
public record HarvestOutputRequestItem(
    [Required, MinLength(1), MaxLength(64)] string Sku,
    [Range(1, 10000)] int Quantity);

/// <summary>Per-SKU destination bin assignment for a multi-output harvest.</summary>
public record HarvestOutputBinRequest(
    [Required, MinLength(1), MaxLength(64)] string PartSku,
    [Required, MinLength(1), MaxLength(128)] string BinCode);

/// <summary>Request body for harvesting a completed job into printed-part stock.</summary>
public record HarvestJobRequest(
    string? BinCode = null,
    int? QuantityOverride = null,
    IReadOnlyList<HarvestOutputRequestItem>? Outputs = null,

    // The harvest endpoint writes this client-supplied key verbatim to PrintJob.HarvestOperationKey —
    // the SAME unique filtered index the server's own "harvest:<jobId>" keys occupy — so it must
    // reject the reserved idem:/harvest: namespaces exactly like AdjustPartInventoryRequest, or a
    // client could pre-occupy another job's future server key and permanently break its harvest
    // (issue #715, Hicks r8 blocker B1/H3). ASP.NET Core validates record constructor-parameter
    // DataAnnotations (6.0+), so this rejects at the model-binding boundary with a 400; the
    // PartHarvestService guard enforces the same rule as defense-in-depth.
    [ReservedOperationKeyPrefix] string? OperationKey = null,
    IReadOnlyList<HarvestOutputBinRequest>? OutputBins = null,
    bool AllowWrongBin = false,
    string? OverrideReason = null);

/// <summary>Request body for adjusting a printed-part SKU stock level.</summary>
public record AdjustPartInventoryRequest(
    [Range(-10000, 10000)] int Delta,
    [property: JsonConverter(typeof(PartAdjustmentReasonConverter))] PartAdjustmentReason Reason,
    Guid? JobId = null,
    string? BinCode = null,
    string? Notes = null,
    [ReservedOperationKeyPrefix] string? OperationKey = null);

/// <summary>Request body for creating a printed-part SKU.</summary>
public record CreatePartInventoryRequest(
    [Required, MinLength(1), MaxLength(64)] string Sku,
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description = null,
    [MaxLength(500)] string? ModelFileRef = null,
    string? DefaultBinCode = null,
    [Range(0, int.MaxValue)] int InitialOnHand = 0,
    [Range(0, int.MaxValue)] int ReorderPoint = 0);

/// <summary>Request body for updating a printed-part SKU's mutable metadata.</summary>
public record UpdatePartInventoryRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description = null,
    [MaxLength(500)] string? ModelFileRef = null,
    string? DefaultBinCode = null,
    [Range(0, int.MaxValue)] int ReorderPoint = 0,
    bool IsActive = true);

/// <summary>Request body for creating a bin. <c>Code</c> doubles as the barcode.</summary>
public record CreateBinRequest(
    [Required, MinLength(1), MaxLength(128)] string Code,
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(200)] string? Location = null,
    [MaxLength(1000)] string? Notes = null);

/// <summary>Request body for updating a bin.</summary>
public record UpdateBinRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(200)] string? Location = null,
    [MaxLength(1000)] string? Notes = null,
    bool IsActive = true);

/// <summary>
/// Request body for the bin registration endpoint that reuses the shared
/// barcode infrastructure. If a bin with the supplied code exists it is
/// returned; otherwise a new bin is created with the supplied name/location.
/// </summary>
public record RegisterBinBarcodeRequest(
    [Required, MinLength(1), MaxLength(128)] string Code,
    [MaxLength(200)] string? Name = null,
    [MaxLength(200)] string? Location = null);

/// <summary>Request body for creating a job-output → SKU mapping.</summary>
public record CreatePartOutputMappingRequest(
    [Required, MinLength(1), MaxLength(64)] string Sku,
    Guid? GcodeFileId = null,
    Guid? PrintProjectFileId = null,
    [Range(1, 10000)] int Quantity = 1);

/// <summary>Reorder-evaluation entry consumed by the F8 shift compiler.</summary>
public record ReorderCandidateResponse(
    Guid PartInventoryId,
    string Sku,
    string Name,
    int OnHand,
    int ReorderPoint,
    int Deficit,
    Guid? DefaultBinId = null,
    string? DefaultBinCode = null,
    string? DefaultBinName = null);
