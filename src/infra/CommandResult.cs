using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Local spools removed; Spoolman is the source of truth

/// <summary>
/// Standard command result indicating success or failure with optional message.
/// </summary>
public record CommandResult(bool Success, string? Message = null);
