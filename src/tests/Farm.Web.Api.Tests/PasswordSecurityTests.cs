using Farm.Infrastructure.Services.Authentication;

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
        string hash = _passwordHashingService.HashPassword(password);

        // Assert
        _ = hash.Should().NotBeNullOrEmpty();
        _ = hash.Should().NotBe(password); // Hash should not be the same as the original password
    }

    [Fact]
    public void HashPassword_SamePAssword_ShouldReturnDifferentHashes()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        string hash1 = _passwordHashingService.HashPassword(password);
        string hash2 = _passwordHashingService.HashPassword(password);

        // Assert
        _ = hash1.Should().NotBeNullOrEmpty();
        _ = hash2.Should().NotBeNullOrEmpty();
        _ = hash1.Should().NotBe(hash2); // Each hash should be unique due to salt
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ShouldReturnTrue()
    {
        // Arrange
        const string password = "TestPassword123!";
        string hash = _passwordHashingService.HashPassword(password);

        // Act
        bool isValid = _passwordHashingService.VerifyPassword(password, hash);

        // Assert
        _ = isValid.Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WithIncorrectPassword_ShouldReturnFalse()
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        const string wrongPassword = "WrongPassword123!";
        string hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        bool isValid = _passwordHashingService.VerifyPassword(wrongPassword, hash);

        // Assert
        _ = isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithEmptyPassword_ShouldReturnFalse()
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        string hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        bool isValid = _passwordHashingService.VerifyPassword("", hash);

        // Assert
        _ = isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithNullPassword_ShouldReturnFalse()
    {
        // Arrange
        const string originalPassword = "TestPassword123!";
        string hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        bool isValid = _passwordHashingService.VerifyPassword(null!, hash);

        // Assert
        _ = isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithInvalidHash_ShouldReturnFalse()
    {
        // Arrange
        const string password = "TestPassword123!";
        const string invalidHash = "invalid-hash";

        // Act
        bool isValid = _passwordHashingService.VerifyPassword(password, invalidHash);

        // Assert
        _ = isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WithEmptyHash_ShouldReturnFalse()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        bool isValid = _passwordHashingService.VerifyPassword(password, "");

        // Assert
        _ = isValid.Should().BeFalse();
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
        string hash = _passwordHashingService.HashPassword(originalPassword);

        // Act
        bool isValid = _passwordHashingService.VerifyPassword(testPassword, hash);

        // Assert
        if (testPassword == originalPassword)
        {
            _ = isValid.Should().BeTrue();
        }
        else
        {
            _ = isValid.Should().BeFalse();
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
        string hash = _passwordHashingService.HashPassword(password);
        bool isValid = _passwordHashingService.VerifyPassword(password, hash);

        // Assert
        _ = hash.Should().NotBeNullOrEmpty();
        _ = isValid.Should().BeTrue();
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
        string hash = _passwordHashingService.HashPassword(password);
        bool isValid = _passwordHashingService.VerifyPassword(password, hash);

        // Assert
        _ = hash.Should().NotBeNullOrEmpty();
        _ = isValid.Should().BeTrue();
    }

    [Fact]
    public void HashPassword_NullPassword_ShouldThrowException()
    {
        // Act & Assert
        Func<string> action = () => _passwordHashingService.HashPassword(null!);
        _ = action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HashPassword_EmptyPassword_ShouldThrowException()
    {
        // Act & Assert
        Func<string> action = () => _passwordHashingService.HashPassword("");
        _ = action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PasswordHashFormat_ShouldBeConsistent()
    {
        // Arrange
        const string password = "TestPassword123!";

        // Act
        string hash1 = _passwordHashingService.HashPassword(password);
        string hash2 = _passwordHashingService.HashPassword(password);

        // Assert - Both hashes should be valid format (though different due to salt)
        _ = hash1.Should().NotBeNullOrEmpty();
        _ = hash2.Should().NotBeNullOrEmpty();
        _ = hash1.Should().NotBe(hash2);

        // Both should verify correctly
        _ = _passwordHashingService.VerifyPassword(password, hash1).Should().BeTrue();
        _ = _passwordHashingService.VerifyPassword(password, hash2).Should().BeTrue();

        // Cross-verification should fail (hash1 with password shouldn't verify with hash2)
        _ = _passwordHashingService.VerifyPassword(password + "wrong", hash1).Should().BeFalse();
        _ = _passwordHashingService.VerifyPassword(password + "wrong", hash2).Should().BeFalse();
    }

    [Fact]
    public void PasswordHashing_SaltUniqueness_ShouldEnsureHashesAreDifferent()
    {
        // Arrange
        const string password = "TestPassword123!";
        const int numberOfHashes = 5;

        // Act
        List<string> hashes = new List<string>();
        for (int i = 0; i < numberOfHashes; i++)
        {
            hashes.Add(_passwordHashingService.HashPassword(password));
        }

        // Assert
        _ = hashes.Should().HaveCount(numberOfHashes);
        _ = hashes.Should().OnlyHaveUniqueItems(); // All hashes should be unique

        // All hashes should verify the original password
        foreach (string hash in hashes)
        {
            _ = _passwordHashingService.VerifyPassword(password, hash).Should().BeTrue();
        }
    }
}
