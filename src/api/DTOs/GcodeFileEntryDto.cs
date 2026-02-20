using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services; // needed for IGcodeUploadSettings
using Farm.Web.Api.Services.Tags;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.DTOs;

/// <summary>DTO describing a single file or directory entry in the virtual G-code library listing.</summary>
public record GcodeFileEntryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("fileSize")] long FileSize,
    [property: JsonPropertyName("uploadedAt")] DateTime UploadedAt,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory,
    [property: JsonPropertyName("name")] string? Name = null,  // Original filename for display
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl = null,
    [property: JsonPropertyName("id")] string? Id = null,  // Include file ID for efficient lookups (GUID as string)
    [property: JsonPropertyName("fileType")] string? FileType = null,  // File extension: gcode, bgcode
    [property: JsonPropertyName("directoryId")] string? DirectoryId = null,   // Include directory ID for efficient directory lookups (virtual path)
    [property: JsonPropertyName("targetModelName")] string? TargetModelName = null,  // Printer model this gcode was sliced for
    [property: JsonPropertyName("requiredMaterial")] string? RequiredMaterial = null,  // Required filament type (e.g., "PLA", "PETG")
    [property: JsonPropertyName("tags")] IReadOnlyList<TagDto>? Tags = null,  // Tags assigned to this gcode file
    [property: JsonPropertyName("extractedSlicerName")] string? ExtractedSlicerName = null,  // Slicer used (PrusaSlicer, OrcaSlicer, etc.)
    [property: JsonPropertyName("extractedSlicerVersion")] string? ExtractedSlicerVersion = null,
    [property: JsonPropertyName("extractedPrintTime")] double? ExtractedPrintTime = null,  // Minutes
    [property: JsonPropertyName("extractedFilamentLength")] double? ExtractedFilamentLength = null,  // Millimeters
    [property: JsonPropertyName("extractedNozzleDiameter")] double? ExtractedNozzleDiameter = null,  // Millimeters
    [property: JsonPropertyName("extractedMaterial")] string? ExtractedMaterial = null,
    [property: JsonPropertyName("extractedPrinterModel")] string? ExtractedPrinterModel = null,
    [property: JsonPropertyName("extractedPrinterModelName")] string? ExtractedPrinterModelName = null,  // Raw extracted name (fallback if resolution failed)
    [property: JsonPropertyName("extractedLayerHeight")] double? ExtractedLayerHeight = null,  // Millimeters
    [property: JsonPropertyName("extractedInfill")] double? ExtractedInfill = null,  // Percentage
    [property: JsonPropertyName("extractedPerimeters")] int? ExtractedPerimeters = null,  // Number of perimeter loops
    [property: JsonPropertyName("extractedHotendTemp")] double? ExtractedHotendTemp = null,  // Celsius
    [property: JsonPropertyName("extractedBedTemp")] double? ExtractedBedTemp = null);  // Celsius
