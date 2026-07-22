namespace Farm.Infrastructure.Domain;

/// <summary>
/// Visibility of a <see cref="ModelCollection"/>. Controls whether users other than the
/// owner (and administrators) may read the collection and its membership.
/// </summary>
/// <remarks>
/// Serialized as a string via the global <c>JsonStringEnumConverter</c> and persisted as a
/// string column. New values may be appended in the future (e.g. for granular sharing in the
/// library-sync epic) without breaking existing clients, so consumers must tolerate unknown
/// members gracefully.
/// </remarks>
public enum ModelCollectionVisibility
{
    /// <summary>Only the owner and administrators can read or mutate the collection.</summary>
    Private = 0,

    /// <summary>
    /// Any authenticated user may read the collection and its members. Mutation remains
    /// restricted to the owner and administrators.
    /// </summary>
    Shared = 1
}
