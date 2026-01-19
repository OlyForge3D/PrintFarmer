using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Generic paged result wrapper
/// </summary>
/// <typeparam name="T">The type of items in the paged result.</typeparam>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);
