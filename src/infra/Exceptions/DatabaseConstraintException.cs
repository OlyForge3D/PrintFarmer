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

    public DatabaseConstraintException(string message) : base(message) { }

    public DatabaseConstraintException(
        string message,
        string? constraintName = null,
        string entityType = "Entity",
        string propertyName = "Property",
        Exception? inner = null)
#pragma warning disable S3427 // Multiple constructors intentionally support flexible initialization
        : base(message, inner)
#pragma warning restore S3427
    {
        ConstraintName = constraintName;
        EntityType = entityType;
        PropertyName = propertyName;
    }

    /// <summary>
    /// Creates a user-friendly error message from an EF DbUpdateException.
    /// Extracts constraint violation details and provides context.
    /// </summary>
    public static DatabaseConstraintException FromEfException(Exception ex, string entityType = "Printer")
    {
        ArgumentNullException.ThrowIfNull(ex);
#pragma warning restore CA1032

        // Try to extract constraint info from various exception types
        string? constraintName = null;
        string propertyName = "Unknown";
        string message = "Failed to save changes to the database";

        // Check for specific EF exception types
        var exceptionType = ex.GetType().Name;

        // Look for constraint violation in exception message or inner exceptions
        if (ex.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            // SQLite UNIQUE constraint format: "UNIQUE constraint failed: Printers.Name"
            var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"UNIQUE constraint failed: \w+\.(\w+)");
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
            var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"NOT NULL constraint failed: \w+\.(\w+)");
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
