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
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services; // needed for IGcodeUploadSettings
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Tags;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.DTOs;

public sealed record ChunkInitResponse(
    [property: JsonPropertyName("uploadId")] string UploadId,
    [property: JsonPropertyName("finalFileName")] string FinalFileName,
    [property: JsonPropertyName("virtualPath")] string VirtualPath,
    [property: JsonPropertyName("uploadedBytes")] long UploadedBytes,
    [property: JsonPropertyName("totalSize")] long TotalSize,
    [property: JsonPropertyName("recommendedChunkSize")] int RecommendedChunkSize,
    [property: JsonPropertyName("hashAlgorithm")] string? HashAlgorithm);
