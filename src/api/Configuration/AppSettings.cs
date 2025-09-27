

namespace Farm.Web.Api.Configuration
{
    // ConfigurationValidator stub: all legacy property validation removed
    public class ConfigurationValidator
    {
        public ConfigurationValidator(Microsoft.Extensions.Options.IOptions<Farm.Infrastructure.Settings.AppSettings> appSettings)
        {
            // Only unified AppSettings is available; legacy property validation removed
        }

        public void ValidateConfiguration()
        {
            // No-op: All legacy property validation removed
        }
    }
}
