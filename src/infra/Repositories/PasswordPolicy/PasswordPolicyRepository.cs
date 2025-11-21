using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Repositories.PasswordPolicy;

public class PasswordPolicyRepository : IPasswordPolicyRepository
{
    private readonly AppDbContext _db;

    public PasswordPolicyRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PasswordPolicyEntity?> GetAsync(CancellationToken ct = default)
    {
        return await _db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
    }

    public async Task SaveAsync(PasswordPolicyEntity policy, CancellationToken ct = default)
    {
        if (policy.Id == default)
        {
            _ = _db.PasswordPolicies.Add(policy);
        }
        _ = await _db.SaveChangesAsync(ct);
    }
}
