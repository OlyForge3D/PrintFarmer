namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for securely hashing and verifying user passwords using BCrypt.
/// </summary>
public interface IPasswordHashingService
{
    /// <summary>
    /// Hashes a plaintext password using BCrypt.
    /// </summary>
    /// <param name="password">The plaintext password to hash.</param>
    /// <returns>The BCrypt hashed password.</returns>
    string HashPassword(string password);

    /// <summary>
    /// Verifies a plaintext password against a BCrypt hash.
    /// </summary>
    /// <param name="password">The plaintext password to verify.</param>
    /// <param name="hashedPassword">The BCrypt hash to verify against.</param>
    /// <returns>True if the password matches the hash, false otherwise.</returns>
    bool VerifyPassword(string password, string hashedPassword);
}

public class PasswordHashingService : IPasswordHashingService
{
    public string HashPassword(string password)
    {
        return string.IsNullOrEmpty(password)
            ? throw new ArgumentException("Password cannot be null or empty", nameof(password))
            : BCrypt.Net.BCrypt.HashPassword(password);
    }

    public bool VerifyPassword(string password, string hashedPassword)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hashedPassword))
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
        }
        catch
        {
            // Invalid hash format or other BCrypt errors should return false
            return false;
        }
    }
}
