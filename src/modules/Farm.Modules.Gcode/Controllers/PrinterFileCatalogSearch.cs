using Farm.Infrastructure.Domain;

namespace Farm.Modules.Gcode.Controllers;

/// <summary>Provider-independent path search for the printer-file catalog.</summary>
internal static class PrinterFileCatalogSearch
{
    /// <summary>
    /// Substring match on <see cref="PrinterFileLocation.RelativePath"/> that ignores case on SQLite,
    /// PostgreSQL and SQL Server.
    /// </summary>
    /// <remarks>
    /// Provider collations differ (PostgreSQL and SQLite substring search is case-sensitive), so
    /// the text is lowered explicitly on both sides. SQLite's built-in <c>lower()</c> folds ASCII
    /// only; the exact-text clause keeps non-ASCII exact-case matches working there. Folding
    /// non-ASCII letters identically on every provider would need a normalized column and a
    /// migration, which is out of scope.
    /// </remarks>
    public static IQueryable<PrinterFileLocation> WherePathContains(
        IQueryable<PrinterFileLocation> query,
        string search)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(search);

#pragma warning disable CA1862 // Database providers translate ToLower; StringComparison overloads do not translate consistently.
        string loweredSearch = search.ToLowerInvariant();
        return query.Where(x => x.RelativePath.Contains(search) ||
            x.RelativePath.ToLower().Contains(loweredSearch));
#pragma warning restore CA1862
    }
}
