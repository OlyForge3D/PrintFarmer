using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Webhooks;
using Farm.Infrastructure.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Data.Interceptors;

/// <summary>
/// EF Core interceptor that encrypts sensitive data (ApiKey, Password, Webhook Secret) before saving to the database.
/// Works in conjunction with SensitiveDataDecryptionInterceptor for symmetric encryption/decryption.
/// </summary>
public class SensitiveDataEncryptionInterceptor : SaveChangesInterceptor
{
    private readonly ISensitiveDataProtector _protector;
    private readonly ILogger<SensitiveDataEncryptionInterceptor> _logger;

    public SensitiveDataEncryptionInterceptor(
        ISensitiveDataProtector protector,
        ILogger<SensitiveDataEncryptionInterceptor> logger)
    {
        _protector = protector;
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        EncryptSensitiveData(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EncryptSensitiveData(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void EncryptSensitiveData(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        EncryptPrinterCredentials(context);
        EncryptWebhookSecrets(context);
        EncryptHomeAssistantToken(context);
    }

    private void EncryptPrinterCredentials(DbContext context)
    {
        var entries = context.ChangeTracker.Entries<Printer>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            var printer = entry.Entity;

            // Encrypt ApiKey if it was modified or added
            if (entry.State == EntityState.Added || entry.Property(p => p.ApiKey).IsModified)
            {
                if (!string.IsNullOrEmpty(printer.ApiKey) && !IsAlreadyEncrypted(printer.ApiKey))
                {
                    printer.ApiKey = _protector.Protect(printer.ApiKey);
                    _logger.LogDebug("Encrypted ApiKey for printer {PrinterId}", printer.Id);
                }
            }

            // Encrypt Password if it was modified or added
            if (entry.State == EntityState.Added || entry.Property(p => p.Password).IsModified)
            {
                if (!string.IsNullOrEmpty(printer.Password) && !IsAlreadyEncrypted(printer.Password))
                {
                    printer.Password = _protector.Protect(printer.Password);
                    _logger.LogDebug("Encrypted Password for printer {PrinterId}", printer.Id);
                }
            }
        }
    }

    private void EncryptWebhookSecrets(DbContext context)
    {
        var entries = context.ChangeTracker.Entries<WebhookSubscription>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            var webhook = entry.Entity;

            if (entry.State == EntityState.Added || entry.Property(w => w.Secret).IsModified)
            {
                if (!string.IsNullOrEmpty(webhook.Secret) && !IsAlreadyEncrypted(webhook.Secret))
                {
                    webhook.Secret = _protector.Protect(webhook.Secret);
                    _logger.LogDebug("Encrypted Secret for webhook {WebhookId}", webhook.Id);
                }
            }
        }
    }

    private void EncryptHomeAssistantToken(DbContext context)
    {
        var entries = context.ChangeTracker.Entries<HomeAssistantSettings>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            var settings = entry.Entity;

            if (entry.State == EntityState.Added || entry.Property(h => h.LongLivedAccessToken).IsModified)
            {
                if (!string.IsNullOrEmpty(settings.LongLivedAccessToken) &&
                    !IsAlreadyEncrypted(settings.LongLivedAccessToken))
                {
                    settings.LongLivedAccessToken = _protector.Protect(settings.LongLivedAccessToken);
                    _logger.LogDebug("Encrypted LongLivedAccessToken for HomeAssistantSettings");
                }
            }
        }
    }

    /// <summary>
    /// Simple heuristic to check if data is already encrypted.
    /// Data Protection produces Base64-encoded strings that are typically longer than plain text credentials.
    /// </summary>
    internal static bool IsAlreadyEncrypted(string value)
    {
        // Data Protection output is Base64 and typically starts with "CfDJ8" for default configuration
        // It's also significantly longer than typical plaintext passwords/API keys
        return value.Length > 100 && value.StartsWith("CfDJ", StringComparison.Ordinal);
    }
}
