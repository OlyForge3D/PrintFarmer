using System;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Security;

/// <summary>
/// Implementation of ISensitiveDataProtector using ASP.NET Core Data Protection API.
/// Provides encryption for sensitive data like API keys and passwords at rest.
/// </summary>
public class SensitiveDataProtector : ISensitiveDataProtector
{
    private readonly IDataProtector _protector;
    private readonly ILogger<SensitiveDataProtector> _logger;

    /// <summary>
    /// Purpose string for the data protector - used to isolate encryption keys.
    /// </summary>
    private const string Purpose = "PrintFarmer.SensitiveData.v1";

    public SensitiveDataProtector(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<SensitiveDataProtector> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    /// <inheritdoc />
    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return plainText;
        }

        try
        {
            return _protector.Protect(plainText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt sensitive data");
            throw new InvalidOperationException("Failed to encrypt sensitive data", ex);
        }
    }

    /// <inheritdoc />
    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return protectedText;
        }

        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (Exception ex)
        {
            // Log but don't throw - the data might be in plaintext (migration scenario)
            // or corrupted. Return null to indicate decryption failed.
            _logger.LogWarning(ex, "Failed to decrypt sensitive data - data may be plaintext or corrupted");
            return null;
        }
    }
}
