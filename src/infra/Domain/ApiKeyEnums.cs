using System;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Declares what an <see cref="ApiKey"/> is intended to authenticate. Determines which
/// <see cref="ApiKeyScope"/> values and expiry rules apply.
/// </summary>
/// <remarks>
/// Existing and unscoped keys always default to <see cref="OctoPrint"/> so that they never
/// implicitly gain access to the PrintFarmer Desktop app's scoped API-key exchange (see
/// issue #838). Only keys explicitly created with <see cref="Desktop"/> purpose can be
/// exchanged for a desktop session token.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiKeyPurpose
{
    /// <summary>
    /// Default/legacy purpose used for OctoPrint-compatible slicer uploads (PrusaSlicer,
    /// OrcaSlicer, SuperSlicer, etc.). Carries no scopes and cannot be exchanged for a
    /// desktop session token.
    /// </summary>
    OctoPrint = 0,

    /// <summary>
    /// Purpose-built for the PrintFarmer Desktop app's API-key-to-JWT exchange. Requires
    /// at least one explicit <see cref="ApiKeyScope"/> and an expiry date.
    /// </summary>
    Desktop = 1,
}

/// <summary>
/// Explicit, bitwise-combinable permissions that a Desktop-purpose <see cref="ApiKey"/> may
/// carry. OctoPrint-purpose keys must never carry any scope other than <see cref="None"/>.
/// </summary>
/// <remarks>
/// <para>
/// The underlying type is pinned to <see cref="int"/> because <c>ApiKeys.Scopes</c> is persisted
/// as an <c>int</c> column. The numeric values of <see cref="ModelRead"/> (1),
/// <see cref="ModelWrite"/> (2), <see cref="LibrarySync"/> (4) and <see cref="All"/> (7) are part
/// of that persisted contract and can never be reassigned.
/// </para>
/// <para>
/// <see cref="All"/> is deliberately <b>frozen at 7</b> and means "all three legacy model/library
/// scopes", not "every scope". Every other flag is privileged - each translates into exactly one
/// real <c>permission</c> claim - so they must always be selected one by one and must never be
/// swept in by a catch-all value. Every pre-existing key stored as 1/2/4/7 therefore continues to
/// mean exactly what it meant when it was issued and can never gain calibration, slicing, or queue
/// authority.
/// </para>
/// <para>
/// Because <see cref="All"/> is not a validation mask, use
/// <see cref="Farm.Infrastructure.Authorization.DesktopScopePermissionMap.KnownScopeMask"/> to
/// reject undefined bits. Note that <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// renders the exact value 7 as the single name <c>"All"</c> and any other combination as a
/// comma-separated list, and accepts raw numbers on input - which is why validation is mask-based
/// and why the API also exposes an explicit <c>scopeNames</c> string array.
/// </para>
/// </remarks>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
#pragma warning disable S1939 // Underlying type is pinned deliberately: ApiKeys.Scopes is persisted
// as an int column, and the stored values 1/2/4/7 are a compatibility contract. Stating `int`
// explicitly prevents a future edit from silently widening or narrowing the persisted type.
public enum ApiKeyScope : int
#pragma warning restore S1939
{
    /// <summary>No scopes granted. The only valid value for <see cref="ApiKeyPurpose.OctoPrint"/> keys.</summary>
    None = 0,

    /// <summary>Allows reading 3D model/library metadata and files. Gated by a scope policy, not a permission claim.</summary>
    ModelRead = 1 << 0,

    /// <summary>Allows creating, updating, or deleting 3D model/library entries. Gated by a scope policy, not a permission claim.</summary>
    ModelWrite = 1 << 1,

    /// <summary>Allows synchronizing the local desktop model library with the server. Gated by a scope policy, not a permission claim.</summary>
    LibrarySync = 1 << 2,

    /// <summary>
    /// Legacy aggregate covering only the model/library scopes above. <b>Frozen at 7</b> - it must
    /// never be widened, or every existing key stored as <c>7</c> would silently escalate. Prefer
    /// selecting individual flags; it is never emitted as a claim.
    /// </summary>
    All = ModelRead | ModelWrite | LibrarySync,

    // Bits 3-7 (8, 16, 32, 64, 128) are intentionally left unallocated. The gap keeps the
    // privileged scopes below well clear of the frozen All=7 aggregate, so no future
    // legacy-range addition can ever be absorbed into it.

    /// <summary>Grants <c>calibration:read</c>: read calibration projects, attempts, photos, and generated profiles.</summary>
    CalibrationRead = 1 << 8,

    /// <summary>Grants <c>calibration:create</c>: create calibration projects and attempts.</summary>
    CalibrationCreate = 1 << 9,

    /// <summary>Grants <c>calibration:update</c>: edit calibration projects, drafts, observations, and photos.</summary>
    CalibrationUpdate = 1 << 10,

    /// <summary>Grants <c>calibration:delete</c>: <b>destructively</b> delete calibration projects, drafts, and photos.</summary>
    CalibrationDelete = 1 << 11,

    /// <summary>Grants <c>calibration:generate</c>: produce and export generated calibration profiles.</summary>
    CalibrationGenerate = 1 << 12,

    /// <summary>Grants <c>calibration:publish</c>: publish a generated calibration profile revision.</summary>
    CalibrationPublish = 1 << 13,

    /// <summary>Grants <c>slicing:submit</c>: submit slicing jobs. Required alongside <see cref="CalibrationGenerate"/>.</summary>
    SlicingSubmit = 1 << 14,

    /// <summary>Grants <c>slicing:read-artifact</c>: download sliced G-code artifacts.</summary>
    SlicingReadArtifact = 1 << 15,

    /// <summary>Grants <c>queue:read</c>: read the print queue.</summary>
    QueueRead = 1 << 16,

    /// <summary>Grants <c>queue:write</c>: enqueue and edit print jobs.</summary>
    QueueWrite = 1 << 17,

    /// <summary>Grants <c>queue:start</c>: <b>start a physical print</b> on a printer.</summary>
    QueueStart = 1 << 18,

    /// <summary>Grants <c>queue:cancel</c>: <b>cancel a running physical print</b>.</summary>
    QueueCancel = 1 << 19,

    /// <summary>Grants <c>queue:acknowledge-bed-clear</c>: confirm the bed is clear so the next job may start. Requires <see cref="QueueStart"/>, which the bed-clear routes also check.</summary>
    QueueAcknowledgeBedClear = 1 << 20,
}
