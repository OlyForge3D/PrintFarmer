using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Auth;

namespace Farm.Infrastructure.Services.PasswordPolicy;

/// <summary>
/// Service for managing password policy configuration.
/// </summary>
public interface IPasswordPolicyService
{
    /// <summary>Gets the current password policy settings.</summary>
    Task<PasswordPolicyDto> GetAsync(CancellationToken ct = default);

    /// <summary>Updates the password policy settings.</summary>
    Task<PasswordPolicyDto> UpdateAsync(UpdatePasswordPolicyRequest request, CancellationToken ct = default);
}
