namespace Farm.Infrastructure.Domain;

/// <summary>
/// Identifies an entity protected by an application-managed optimistic-concurrency revision.
/// </summary>
public interface IRevisionedEntity
{
    /// <summary>Gets or sets the monotonically increasing persisted revision.</summary>
    long Revision { get; set; }
}
