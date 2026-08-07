using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Farm.Infrastructure.Data;

/// <summary>
/// Configures and advances portable optimistic-concurrency revisions.
/// </summary>
public static class RevisionConcurrency
{
    private const long InitialRevision = 1;

    /// <summary>
    /// Configures every revisioned root entity to use its revision as an
    /// application-managed concurrency token.
    /// </summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        IEnumerable<IMutableEntityType> revisionedTypes = modelBuilder.Model
            .GetEntityTypes()
            .Where(entityType =>
                typeof(IRevisionedEntity).IsAssignableFrom(entityType.ClrType) &&
                (entityType.BaseType is null ||
                    !typeof(IRevisionedEntity).IsAssignableFrom(entityType.BaseType.ClrType)));

        foreach (IMutableEntityType entityType in revisionedTypes)
        {
            _ = modelBuilder.Entity(entityType.ClrType)
                .Property<long>(nameof(IRevisionedEntity.Revision))
                .HasDefaultValue(InitialRevision)
                .ValueGeneratedNever()
                .IsConcurrencyToken();
        }
    }

    /// <summary>Initializes new revisions and increments modified revisions before a save.</summary>
    public static void Advance(ChangeTracker changeTracker)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        changeTracker.DetectChanges();

        foreach (EntityEntry entry in changeTracker.Entries())
        {
            if (entry.Entity is not IRevisionedEntity revisionedEntity)
            {
                continue;
            }

            PropertyEntry revision = entry.Property(nameof(IRevisionedEntity.Revision));
            if (entry.State == EntityState.Added)
            {
                revision.CurrentValue = InitialRevision;
                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            long originalRevision = (long)revision.OriginalValue!;
            if (originalRevision < InitialRevision)
            {
                throw new DbUpdateConcurrencyException(
                    $"Tracked {entry.Metadata.ClrType.Name} has invalid revision {originalRevision}.");
            }

            revision.CurrentValue = checked(originalRevision + 1);
            revision.IsModified = true;
        }
    }
}
