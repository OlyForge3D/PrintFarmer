using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Infrastructure.UnitOfWork;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.PasswordPolicy;

public class PasswordPolicyRepository : IPasswordPolicyRepository
{
    private readonly IUnitOfWork _uow;

    public PasswordPolicyRepository(IUnitOfWork uow)
    {
        _uow = uow;
    }

    private AppDbContext Db => _uow.Context;

    public async Task<Farm.Infrastructure.Domain.PasswordPolicyEntity?> GetAsync(CancellationToken ct = default)
    {
        return await Db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
    }

    public async Task SaveAsync(Farm.Infrastructure.Domain.PasswordPolicyEntity policy, CancellationToken ct = default)
    {
        if (policy.Id == default)
        {
            _ = Db.PasswordPolicies.Add(policy);
        }
        _ = await _uow.SaveChangesAsync(ct);
    }
}
