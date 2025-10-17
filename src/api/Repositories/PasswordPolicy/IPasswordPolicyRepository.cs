using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Repositories.PasswordPolicy;

public interface IPasswordPolicyRepository
{
    Task<Farm.Infrastructure.Domain.PasswordPolicyEntity?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(Farm.Infrastructure.Domain.PasswordPolicyEntity policy, CancellationToken ct = default);
}
