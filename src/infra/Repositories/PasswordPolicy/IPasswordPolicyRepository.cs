using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PasswordPolicy;

public interface IPasswordPolicyRepository
{
    Task<PasswordPolicyEntity?> GetAsync(CancellationToken ct = default);

    Task SaveAsync(PasswordPolicyEntity policy, CancellationToken ct = default);
}
