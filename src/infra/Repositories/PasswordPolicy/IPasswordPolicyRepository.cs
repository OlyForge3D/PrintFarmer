using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PasswordPolicy;

/// <summary>
/// Repository for managing password policy configuration.
/// </summary>
public interface IPasswordPolicyRepository
{
    /// <summary>Gets the current password policy.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<PasswordPolicyEntity?> GetAsync(CancellationToken ct = default);

    /// <summary>Saves or updates the password policy.</summary>
    /// <param name="policy">The password policy to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(PasswordPolicyEntity policy, CancellationToken ct = default);
}
