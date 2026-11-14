using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Identifies recoverable authentication-store failures without hiding DI or programming faults.
/// </summary>
public static class AuthenticationStoreFailure
{
    /// <summary>
    /// Recognizes database/update failures, timeouts and internal cancellation. Only the exact
    /// InvalidOperationException provider wrapper with a database/timeout cause is recoverable.
    /// Caller cancellation, disposed services and unwrapped invalid states propagate.
    /// </summary>
    public static bool IsRecoverable(Exception exception, CancellationToken callerCancellation = default)
    {
        if (callerCancellation.IsCancellationRequested)
        {
            return false;
        }

        return exception is DbException or DbUpdateException or TimeoutException or OperationCanceledException ||
            (exception.GetType() == typeof(InvalidOperationException) &&
             exception.InnerException is DbException or TimeoutException);
    }
}
