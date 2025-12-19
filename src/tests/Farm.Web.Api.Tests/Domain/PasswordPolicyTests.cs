using Farm.Web.Api.Domain;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Domain;

public class PasswordPolicyTests
{
    [Fact]
    public void Constructor_WithDefaults_Succeeds()
    {
        var policy = new PasswordPolicy();

        policy.Should().NotBeNull();
        policy.MinLength.Should().Be(8);
        policy.RequireUppercase.Should().BeFalse();
        policy.RequireLowercase.Should().BeFalse();
        policy.RequireDigit.Should().BeFalse();
        policy.RequireSymbol.Should().BeFalse();
    }

    [Fact]
    public void MinLength_DefaultsToEight()
    {
        var policy = new PasswordPolicy();

        policy.MinLength.Should().Be(8);
    }

    [Fact]
    public void MinLength_CanBeSet()
    {
        var policy = new PasswordPolicy { MinLength = 12 };

        policy.MinLength.Should().Be(12);
    }

    [Fact]
    public void RequireUppercase_CanBeSet()
    {
        var policy = new PasswordPolicy { RequireUppercase = true };

        policy.RequireUppercase.Should().BeTrue();
    }

    [Fact]
    public void RequireLowercase_CanBeSet()
    {
        var policy = new PasswordPolicy { RequireLowercase = true };

        policy.RequireLowercase.Should().BeTrue();
    }

    [Fact]
    public void RequireDigit_CanBeSet()
    {
        var policy = new PasswordPolicy { RequireDigit = true };

        policy.RequireDigit.Should().BeTrue();
    }

    [Fact]
    public void RequireSymbol_CanBeSet()
    {
        var policy = new PasswordPolicy { RequireSymbol = true };

        policy.RequireSymbol.Should().BeTrue();
    }

    [Fact]
    public void UpdatedAt_DefaultsToNow()
    {
        var before = DateTime.UtcNow;
        var policy = new PasswordPolicy();
        var after = DateTime.UtcNow;

        policy.UpdatedAt.Should().BeOnOrAfter(before);
        policy.UpdatedAt.Should().BeOnOrBefore(after.AddSeconds(1));
    }

    [Fact]
    public void UpdatedAt_CanBeSet()
    {
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new PasswordPolicy { UpdatedAt = timestamp };

        policy.UpdatedAt.Should().Be(timestamp);
    }

    [Fact]
    public void Id_CanBeSet()
    {
        var policy = new PasswordPolicy { Id = 42 };

        policy.Id.Should().Be(42);
    }

    [Fact]
    public void PasswordPolicy_CanHaveAllOptionsEnabled()
    {
        var policy = new PasswordPolicy
        {
            MinLength = 16,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireDigit = true,
            RequireSymbol = true
        };

        policy.MinLength.Should().Be(16);
        policy.RequireUppercase.Should().BeTrue();
        policy.RequireLowercase.Should().BeTrue();
        policy.RequireDigit.Should().BeTrue();
        policy.RequireSymbol.Should().BeTrue();
    }

    [Fact]
    public void PasswordPolicy_CanHaveSelectiveRequirements()
    {
        var policy = new PasswordPolicy
        {
            MinLength = 10,
            RequireUppercase = true,
            RequireDigit = true
        };

        policy.MinLength.Should().Be(10);
        policy.RequireUppercase.Should().BeTrue();
        policy.RequireDigit.Should().BeTrue();
        policy.RequireLowercase.Should().BeFalse();
        policy.RequireSymbol.Should().BeFalse();
    }

    [Fact]
    public void MinLength_CanBeZero()
    {
        var policy = new PasswordPolicy { MinLength = 0 };

        policy.MinLength.Should().Be(0);
    }

    [Fact]
    public void MinLength_CanBeVeryLarge()
    {
        var policy = new PasswordPolicy { MinLength = 1000 };

        policy.MinLength.Should().Be(1000);
    }

    [Fact]
    public void MultipleInstances_AreIndependent()
    {
        var policy1 = new PasswordPolicy { MinLength = 8, RequireUppercase = true };
        var policy2 = new PasswordPolicy { MinLength = 16, RequireDigit = true };

        policy1.MinLength.Should().Be(8);
        policy1.RequireUppercase.Should().BeTrue();
        policy1.RequireDigit.Should().BeFalse();

        policy2.MinLength.Should().Be(16);
        policy2.RequireDigit.Should().BeTrue();
        policy2.RequireUppercase.Should().BeFalse();
    }

    [Fact]
    public void PasswordPolicy_DefaultId_IsZero()
    {
        var policy = new PasswordPolicy();

        policy.Id.Should().Be(0);
    }

    [Fact]
    public void PasswordPolicy_CanBeModifiedAfterCreation()
    {
        var policy = new PasswordPolicy();

        policy.MinLength = 12;
        policy.RequireUppercase = true;
        policy.UpdatedAt = DateTime.UtcNow.AddHours(-1);

        policy.MinLength.Should().Be(12);
        policy.RequireUppercase.Should().BeTrue();
    }
}
