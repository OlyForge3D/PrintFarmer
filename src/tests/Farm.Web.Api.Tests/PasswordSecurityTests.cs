using Farm.Web.Api.Services.Authentication;

namespace Farm.Web.Api.Tests;

public class PasswordSecurityTests
{
    private readonly IPasswordHashingService _passwordHashingService = new PasswordHashingService();

    [Fact]
    public void HashPassword_ShouldReturnNonEmptyHash()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        var hash = _passwordHashingService.HashPassword(password);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        hash.Should().NotBe(password); // Hash should not be the same as the original password
    }

    [Fact]
    public void HashPassword_SamePAssword_ShouldReturnDifferentHashes()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        var hash1 = _passwordHashingService.HashPassword(password);
        var hash2 = _passwordHashingService.HashPassword(password);

        // Assert
        hash1.Should().NotBeNullOrEmpty();
        hash2.Should().NotBeNullOrEmpty();
        hash1.Should().NotBe(hash2); // Each hash should be unique due to salt
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ShouldReturnTrue()
    {
        // Arrange
        const string password = "TestPassword123!";
        var hash = _passwordHashingService.HashPassword(password);

        // Act
        var isValid = _passwordHashingService.VerifyPassword(password, hash);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WithIncorrectPassword_ShouldReturnFalse()
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        const string wrongPassword = "WrongPassword123!";
        var hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        var isValid = _passwordHashingService.VerifyPassword(wrongPassword, hash);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithEmptyPassword_ShouldReturnFalse()
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        var hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        var isValid = _passwordHashingService.VerifyPassword("", hash);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithNullPassword_ShouldReturnFalse()
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        var hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        var isValid = _passwordHashingService.VerifyPassword(null!, hash);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithInvalidHash_ShouldReturnFalse()
    {
        // Arrange
        const string password = "TestPassword123!";
        const string invalidHash = "invalid-hash";

        // Act
        var isValid = _passwordHashingService.VerifyPassword(password, invalidHash);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithEmptyHash_ShouldReturnFalse()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        var isValid = _passwordHashingService.VerifyPassword(password, "");

        // Assert
        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("Password123")]
    [InlineData("password123!")]
    [InlineData("PASSWORD123!")]
    public void PasswordHashing_CaseSensitive_ShouldOnlyMatchExactCase(string testPassword)
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        var hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        var isValid = _passwordHashingService.VerifyPassword(testPassword, hash);

        // Assert
        if (testPassword == originalPassword)
        {
            isValid.Should().BeTrue();
        }
        else
        {
            isValid.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("a")]                          // Too short
    [InlineData("ab")]                         // Too short
    [InlineData("abc")]                        // Too short
    [InlineData("abcd")]                       // Too short
    [InlineData("abcde")]                      // Too short
    [InlineData("abcdef")]                     // Minimum length
    [InlineData("abcdefg")]                    // Above minimum
    [InlineData("VeryLongPasswordThatShouldStillWork123!")] // Very long
    public void PasswordHashing_VariousLengths_ShouldWorkCorrectly(string password)
    {
        // Act
        var hash = _passwordHashingService.HashPassword(password);
        var isValid = _passwordHashingService.VerifyPassword(password, hash);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        isValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("SimplePassword")]
    [InlineData("password with spaces")]
    [InlineData("пароль123")] // Cyrillic characters
    [InlineData("密码123")]    // Chinese characters
    [InlineData("🔒🔑💻")]    // Emojis
    [InlineData("SpecialChars!@#$%^&*()")]
    [InlineData("Mixed123!@#AaBbCc")]
    public void PasswordHashing_SpecialCharacters_ShouldWorkCorrectly(string password)
    {
        // Act
        var hash = _passwordHashingService.HashPassword(password);
        var isValid = _passwordHashingService.VerifyPassword(password, hash);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        isValid.Should().BeTrue();
    }

    [Fact]
    public void PasswordHashing_Performance_ShouldBeReasonablyFast()
    {
        // Arrange
        const string password = "TestPassword123!";
        const int iterations = 10;

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            var hash = _passwordHashingService.HashPassword(password);
            var isValid = _passwordHashingService.VerifyPassword(password, hash);
            isValid.Should().BeTrue();
        }

        stopwatch.Stop();

        // Assert - Should complete 10 hash+verify cycles in reasonable time
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(10000); // Less than 10 seconds for 10 iterations
    }

    [Fact]
    public void HashPassword_NullPassword_ShouldThrowException()
    {
        // Act & Assert
        var action = () => _passwordHashingService.HashPassword(null!);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HashPassword_EmptyPassword_ShouldThrowException()
    {
        // Act & Assert
        var action = () => _passwordHashingService.HashPassword("");
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PasswordHashFormat_ShouldBeConsistent()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        var hash1 = _passwordHashingService.HashPassword(password);
        var hash2 = _passwordHashingService.HashPassword(password);

        // Assert - Both hashes should be valid format (though different due to salt)
        hash1.Should().NotBeNullOrEmpty();
        hash2.Should().NotBeNullOrEmpty();
        hash1.Should().NotBe(hash2);

        // Both should verify correctly
        _passwordHashingService.VerifyPassword(password, hash1).Should().BeTrue();
        _passwordHashingService.VerifyPassword(password, hash2).Should().BeTrue();

        // Cross-verification should fail (hash1 with password shouldn't verify with hash2)
        _passwordHashingService.VerifyPassword(password + "wrong", hash1).Should().BeFalse();
        _passwordHashingService.VerifyPassword(password + "wrong", hash2).Should().BeFalse();
    }

    [Fact]
    public void PasswordHashing_SaltUniqueness_ShouldEnsureHashesAreDifferent()
    {
        // Arrange
        const string password = "TestPassword123!";
        const int numberOfHashes = 5;

        // Act
        var hashes = new List<string>();
        for (int i = 0; i < numberOfHashes; i++)
        {
            hashes.Add(_passwordHashingService.HashPassword(password));
        }

        // Assert
        hashes.Should().HaveCount(numberOfHashes);
        hashes.Should().OnlyHaveUniqueItems(); // All hashes should be unique

        // All hashes should verify the original password
        foreach (var hash in hashes)
        {
            _passwordHashingService.VerifyPassword(password, hash).Should().BeTrue();
        }
    }
}
