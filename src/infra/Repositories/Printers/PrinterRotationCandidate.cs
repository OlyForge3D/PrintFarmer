namespace Farm.Infrastructure.Repositories.Printers;

/// <summary>
/// Minimal projection of a printer selected by a maintenance rotation query (issue #2061).
/// Deliberately excludes credentials and every other column: the maintenance alert engine only
/// needs the printer's identity, so the rotation query never pays the decryption cost that
/// <see cref="IPrintersRepository.GetAllAsync"/> incurs for every row.
/// </summary>
/// <param name="Id">The printer's unique identifier.</param>
/// <param name="Name">The printer's display name, used only for logging.</param>
public sealed record PrinterRotationCandidate(Guid Id, string Name);
