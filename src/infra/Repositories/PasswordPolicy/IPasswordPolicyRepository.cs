using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PasswordPolicy;

public interface IPasswordPolicyRepository
{
    Task<Farm.Infrastructure.Domain.PasswordPolicyEntity?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(Farm.Infrastructure.Domain.PasswordPolicyEntity policy, CancellationToken ct = default);
}
