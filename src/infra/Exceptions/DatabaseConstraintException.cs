using System.Text.RegularExpressions;

namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when a database constraint violation occurs during entity creation or update.
/// Provides a user-friendly error message extracted from the EF constraint failure.
/// </summary>
#pragma warning disable CA1032 // Current constructor pattern is intentional for domain exception
public sealed class DatabaseConstraintException : Exception
{
    public string? ConstraintName { get; }

    public string EntityType { get; } = "Entity";

    public string PropertyName { get; } = "Property";

    /// <summary>
    /// Creates an exception with just a message.
    /// </summary>
    /// <param name="message">The error message describing the constraint violation.</param>
    public DatabaseConstraintException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates an exception with detailed constraint information.
    /// </summary>
    /// <param name="message">The error message describing the constraint violation.</param>
    /// <param name="constraintName">The name of the constraint that was violated.</param>
    /// <param name="entityType">The type of entity that caused the violation.</param>
    /// <param name="propertyName">The name of the property involved in the violation.</param>
    /// <param name="inner">The inner exception that caused this exception.</param>
#pragma warning disable S3427 // Multiple constructors with default parameters are intentional for flexible initialization
    public DatabaseConstraintException(
        string message,
        string? constraintName = null,
        string entityType = "Entity",
        string propertyName = "Property",
        Exception? inner = null)
#pragma warning restore S3427
        : base(message, inner)
    {
        ConstraintName = constraintName;
        EntityType = entityType;
        PropertyName = propertyName;
    }

    /// <summary>
    /// Creates a user-friendly error message from an EF DbUpdateException.
    /// Extracts constraint violation details and provides context.
    /// </summary>
    /// <param name="ex">The Entity Framework exception to parse.</param>
    /// <param name="entityType">The type of entity being saved when the exception occurred.</param>
    public static DatabaseConstraintException FromEfException(Exception ex, string entityType = "Printer")
    {
        ArgumentNullException.ThrowIfNull(ex);
#pragma warning restore CA1032

        // Try to extract constraint info from various exception types
        string? constraintName = null;
        string propertyName = "Unknown";
        string message = "Failed to save changes to the database";

        // Check for specific EF exception types
        string exceptionType = ex.GetType().Name;

        // Look for constraint violation in exception message or inner exceptions
        if (ex.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            // SQLite UNIQUE constraint format: "UNIQUE constraint failed: Printers.Name"
            Match match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"UNIQUE constraint failed: \w+\.(\w+)");
            if (match.Success)
            {
                propertyName = match.Groups[1].Value;
                message = $"{entityType} with this {propertyName} already exists";
                constraintName = "UNIQUE";
            }
        }
        else if (ex.Message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            message = $"Cannot save {entityType.ToLower()}: one or more referenced entities do not exist or have been deleted";
            constraintName = "FOREIGN KEY";
        }
        else if (ex.Message.Contains("NOT NULL constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            // "NOT NULL constraint failed: Printers.BackendPort"
            Match match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"NOT NULL constraint failed: \w+\.(\w+)");
            if (match.Success)
            {
                propertyName = match.Groups[1].Value;
                message = $"{entityType}.{propertyName} is required but was not provided";
            }

            constraintName = "NOT NULL";
        }
        else if (ex.Message.Contains("CHECK constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            message = $"One or more {entityType.ToLower()} properties have invalid values";
            constraintName = "CHECK";
        }
        else if (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            message = $"A {entityType.ToLower()} with this information already exists";
            constraintName = "UNIQUE (inferred)";
        }
        else
        {
            // Fallback: show what we can extract
            message = ex.InnerException?.Message ?? ex.Message;
            if (message.StartsWith("An error occurred while saving", StringComparison.OrdinalIgnoreCase))
            {
                // Replace the generic EF message
                message = $"Failed to save {entityType.ToLower()} due to database constraint violation";
            }
        }

        return new DatabaseConstraintException(message, constraintName, entityType, propertyName, ex);
    }
}
