using System;

namespace Farm.Infrastructure.Services.Security;

/// <summary>
/// Service for encrypting and decrypting sensitive data such as API keys and passwords.
/// </summary>
public interface ISensitiveDataProtector
{
    /// <summary>
    /// Encrypts sensitive data for secure storage.
    /// </summary>
    /// <param name="plainText">The plain text to encrypt.</param>
    /// <returns>The encrypted string, or null if input is null.</returns>
    string? Protect(string? plainText);

    /// <summary>
    /// Decrypts previously encrypted data.
    /// </summary>
    /// <param name="protectedText">The encrypted string to decrypt.</param>
    /// <returns>The decrypted plain text, or null if input is null or decryption fails.</returns>
    string? Unprotect(string? protectedText);
}
