using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.PasswordPolicy;
using Farm.Web.Shared;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests;

public class PasswordPolicyServiceTests
{
    [Fact]
    public async Task GetAsync_Returns_DefaultDto_WhenRepositoryEmpty()
    {
        var repo = new Mock<Farm.Infrastructure.Repositories.PasswordPolicy.IPasswordPolicyRepository>();
        repo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Farm.Infrastructure.Domain.PasswordPolicyEntity?)null);
        var svc = new PasswordPolicyService(repo.Object);

        var dto = await svc.GetAsync(CancellationToken.None);

        Assert.NotNull(dto);
        // Default MinLength set in shared DTO is 8.
        // Service should return that default DTO when repository has no entity.
        Assert.Equal(8, dto.MinLength);
    }

    [Fact]
    public async Task UpdateAsync_CreatesOrUpdates_AndReturnsDto()
    {
        var repo = new Mock<Farm.Infrastructure.Repositories.PasswordPolicy.IPasswordPolicyRepository>();
        // repository initially returns null -> service creates new entity and calls SaveAsync
        Farm.Infrastructure.Domain.PasswordPolicyEntity? savedEntity = null;
        repo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(savedEntity);
        repo.Setup(r => r.SaveAsync(It.IsAny<Farm.Infrastructure.Domain.PasswordPolicyEntity>(), It.IsAny<CancellationToken>()))
            .Callback<Farm.Infrastructure.Domain.PasswordPolicyEntity, CancellationToken>((p, ct) => savedEntity = p)
            .Returns(Task.CompletedTask)
            .Verifiable();

        var svc = new PasswordPolicyService(repo.Object);
        var request = new UpdatePasswordPolicyRequest { MinLength = 14, RequireDigit = true };

        var result = await svc.UpdateAsync(request, CancellationToken.None);

        // Ensure SaveAsync was called and our callback captured the saved entity
        Assert.NotNull(savedEntity);
        Assert.Equal(14, savedEntity!.MinLength);

        // Now ensure the service returned the updated values
        Assert.Equal(14, result.MinLength);
        Assert.True(result.RequireDigit);
        repo.Verify(r => r.SaveAsync(It.IsAny<Farm.Infrastructure.Domain.PasswordPolicyEntity>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
