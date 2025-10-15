using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Repositories.PasswordPolicy;

public interface IPasswordPolicyRepository
{
    Task<Farm.Infrastructure.Domain.PasswordPolicy?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(Farm.Infrastructure.Domain.PasswordPolicy policy, CancellationToken ct = default);
}
