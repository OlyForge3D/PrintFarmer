using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.PasswordPolicy;

public class PasswordPolicyRepository(AppDbContext db) : IPasswordPolicyRepository
{
    private readonly AppDbContext _db = db;

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
