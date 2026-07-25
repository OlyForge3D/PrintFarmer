using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Api.Authorization;

/// <summary>
/// Verifies that routes targeting a printer use an enabled authoritative printer.
/// </summary>
public interface IPrinterAccessValidator
{
    Task<bool> IsEnabledAsync(Guid? printerId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class PrinterAccessValidator(AppDbContext dbContext) : IPrinterAccessValidator
{
    public Task<bool> IsEnabledAsync(
        Guid? printerId,
        CancellationToken cancellationToken = default)
    {
        if (printerId is null || printerId == Guid.Empty)
        {
            return Task.FromResult(true);
        }

        return dbContext.Printers
            .AsNoTracking()
            .AnyAsync(
                printer => printer.Id == printerId && printer.IsEnabled,
                cancellationToken);
    }
}
