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
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiKeyScope
{
    /// <summary>No scopes granted. The only valid value for <see cref="ApiKeyPurpose.OctoPrint"/> keys.</summary>
    None = 0,

    /// <summary>Allows reading 3D model/library metadata and files.</summary>
    ModelRead = 1 << 0,

    /// <summary>Allows creating, updating, or deleting 3D model/library entries.</summary>
    ModelWrite = 1 << 1,

    /// <summary>Allows synchronizing the local desktop model library with the server.</summary>
    LibrarySync = 1 << 2,

    /// <summary>All scopes currently defined for Desktop-purpose keys.</summary>
    All = ModelRead | ModelWrite | LibrarySync,
}
