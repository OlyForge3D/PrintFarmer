using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.PasswordPolicy;

public interface IPasswordPolicyService
{
    Task<PasswordPolicyDto> GetAsync(CancellationToken ct = default);
    Task<PasswordPolicyDto> UpdateAsync(Shared.UpdatePasswordPolicyRequest request, CancellationToken ct = default);
}
