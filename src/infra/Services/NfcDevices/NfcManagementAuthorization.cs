using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.NfcDevices;

/// <summary>
/// Applies NFC management permission and the existing printer-group administration boundary.
/// Resolved per operation by the singleton tag service; never captures a request principal.
/// </summary>
public sealed class NfcManagementAuthorization(
    IHttpContextAccessor httpContextAccessor,
    IQueueResourceAuthorizationService resources,
    AppDbContext db)
{
    public void EnsureAdmin()
    {
        _ = GetAdmin();
    }

    public async Task<bool> CanAccessPrinterAsync(Guid? printerId, CancellationToken ct)
    {
        ClaimsPrincipal principal = GetAdmin();
        return !printerId.HasValue ||
            (await db.Printers.AnyAsync(p => p.Id == printerId.Value, ct) &&
             await resources.CanAccessPrinterAsync(principal, printerId.Value, PrinterGroupAccessLevel.Manage, ct));
    }

    public async Task EnsurePrinterAsync(Guid? printerId, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(printerId, ct))
        {
            throw new NfcManagementAccessDeniedException();
        }
    }

    public Task<IReadOnlySet<Guid>> FilterPrinterIdsAsync(IReadOnlyCollection<Guid> printerIds, CancellationToken ct) =>
        resources.FilterAccessiblePrinterIdsAsync(GetAdmin(), printerIds, PrinterGroupAccessLevel.Manage, ct);

    private ClaimsPrincipal GetAdmin()
    {
        ClaimsPrincipal? principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true ||
            !PrintFarmerPermissions.HasPermission(principal, "nfc_devices:admin"))
        {
            throw new NfcManagementAccessDeniedException();
        }

        return principal;
    }
}

/// <summary>Uniform denial without exposing NFC device, tag, or printer identifiers.</summary>
public sealed class NfcManagementAccessDeniedException : Exception
{
    public NfcManagementAccessDeniedException() : base("NFC management access denied.")
    {
    }

    public NfcManagementAccessDeniedException(string message) : base(message)
    {
    }

    public NfcManagementAccessDeniedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
