using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

// This file contains DTOs intended for JSON serialization across client/server.
// URL-like values are represented as strings by design for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings

// Enum Serialization Policy:
//  - Global Program.cs registers JsonStringEnumConverter (string names in API payloads).
//  - Per-enum [JsonConverter] attributes are ONLY used when:
//      * A custom tolerant converter is required (numeric + string input) OR
//      * The enum is exchanged with external worker processes that may not share the global options.
//  - Simple API-only enums rely on global options (no attribute clutter).
//
// Custom tolerant converter (numeric OR string) for backward compatibility in tests and workers.
[JsonConverter(typeof(Json.PrinterBackendJsonConverter))]
public enum PrinterBackend
{
    Unknown = 0,
    Moonraker = 1,
    PrusaLink = 2,
    SDCP = 3,
    OctoPrint = 4
}
