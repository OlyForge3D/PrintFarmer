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

// ---------------- Chunk Upload DTOs ----------------
public sealed record ChunkInitRequest(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size"), JsonRequired] long Size,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hashAlgorithm")] string? HashAlgorithm = null,
    [property: JsonPropertyName("expectedHash")] string? ExpectedHash = null);
