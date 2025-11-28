using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PasswordPolicy;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.PasswordPolicy;

public class PasswordPolicyService : IPasswordPolicyService
{
    private readonly IPasswordPolicyRepository _repo;

    public PasswordPolicyService(IPasswordPolicyRepository repo)
    {
        _repo = repo;
    }

    public async Task<PasswordPolicyDto> GetAsync(CancellationToken ct = default)
    {
        PasswordPolicyEntity? entity = await _repo.GetAsync(ct);
        if (entity == null)
        {
            return new PasswordPolicyDto();
        }
        return new PasswordPolicyDto
        {
            MinLength = entity.MinLength,
            RequireUppercase = entity.RequireUppercase,
            RequireLowercase = entity.RequireLowercase,
            RequireDigit = entity.RequireDigit,
            RequireSymbol = entity.RequireSymbol
        };
    }

    public async Task<PasswordPolicyDto> UpdateAsync(UpdatePasswordPolicyRequest request, CancellationToken ct = default)
    {
        PasswordPolicyEntity entity = await _repo.GetAsync(ct) ?? new PasswordPolicyEntity();
        if (request.MinLength.HasValue)
        {
            entity.MinLength = request.MinLength.Value;
        }
        if (request.RequireUppercase.HasValue)
        {
            entity.RequireUppercase = request.RequireUppercase.Value;
        }
        if (request.RequireLowercase.HasValue)
        {
            entity.RequireLowercase = request.RequireLowercase.Value;
        }
        if (request.RequireDigit.HasValue)
        {
            entity.RequireDigit = request.RequireDigit.Value;
        }
        if (request.RequireSymbol.HasValue)
        {
            entity.RequireSymbol = request.RequireSymbol.Value;
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await _repo.SaveAsync(entity, ct);

        // Return the DTO directly from the saved entity to avoid an extra repository read
        return new PasswordPolicyDto
        {
            MinLength = entity.MinLength,
            RequireUppercase = entity.RequireUppercase,
            RequireLowercase = entity.RequireLowercase,
            RequireDigit = entity.RequireDigit,
            RequireSymbol = entity.RequireSymbol
        };
    }
}
