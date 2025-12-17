namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown to signal a duplicate entity (HTTP 409) with an existing DTO and optional normalized name.
/// </summary>
public sealed class DuplicateEntityException : Exception
{
    public string EntityType { get; } = string.Empty;
    public object ExistingDto { get; } = new object();
    public string? NormalizedName { get; }

    public DuplicateEntityException() { }
    public DuplicateEntityException(string message) : base(message) { }
    public DuplicateEntityException(string message, Exception inner) : base(message, inner) { }

    public DuplicateEntityException(string entityType, object existingDto, string? normalizedName, string? message = null, Exception? inner = null)
        : base(message ?? $"{entityType} already exists", inner)
    {
        EntityType = entityType;
        ExistingDto = existingDto;
        NormalizedName = normalizedName;
    }
}
