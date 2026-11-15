using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Tags;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services; // needed for IGcodeUploadSettings
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Farm.Modules.Gcode.DTOs;

/// <summary>Request body for bulk deletion of virtual G-code files.</summary>
public sealed class DeleteFilesRequest
{
    [JsonPropertyName("fileIds")] public IList<Guid> FileIds { get; init; } = [];
}

/// <summary>Response body for bulk deletion of virtual G-code files.</summary>
/// <param name="Deleted">Number of distinct requested records that were removed, including those whose stored-file cleanup was incomplete.</param>
/// <param name="TotalRequested">Number of distinct requested file IDs.</param>
/// <param name="Results">One outcome per distinct requested ID, in request order.</param>
public sealed record DeleteFilesResponse(
    [property: JsonPropertyName("deleted")] int Deleted,
    [property: JsonPropertyName("totalRequested")] int TotalRequested,
    [property: JsonPropertyName("results")] IReadOnlyList<BulkDeleteItemResult> Results);
