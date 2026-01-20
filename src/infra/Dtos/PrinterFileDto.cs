using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// File information including G-code file name and thumbnail URL.
/// </summary>
public record PrinterFileDto(
    string FileName,
    string? ThumbnailUrl = null,
    long? Modified = null,
    long? SizeBytes = null);
