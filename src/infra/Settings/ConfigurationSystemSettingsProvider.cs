using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings
{
    public class ConfigurationSystemSettingsProvider : ISystemSettingsProvider
    {
        private readonly IConfiguration _config;

        public ConfigurationSystemSettingsProvider(IConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public T Get<T>() where T : class, new()
        {
            // Try to find a public constant/field or static property named SectionName on the type T
            var field = typeof(T).GetField("SectionName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var prop = typeof(T).GetProperty("SectionName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            string? section = null;
            try
            {
                if (field != null)
                {
                    object? val = field.GetValue(null);
                    section = val?.ToString();
                }
                else if (prop != null)
                {
                    // Only accept static property values
                    var getter = prop.GetMethod;
                    if (getter != null && getter.IsStatic)
                    {
                        object? val = prop.GetValue(null);
                        section = val?.ToString();
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(section))
            {
                // Fallback to type name
                section = typeof(T).Name;
            }

            return Get<T>(section);
        }

        public T Get<T>(string sectionName) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(sectionName))
            {
                throw new ArgumentNullException(nameof(sectionName));
            }

            T instance = new T();
            IConfigurationSection section = _config.GetSection(sectionName);
            section.Bind(instance);
            return instance;
        }
    }

    internal static class ReflectionExtensions
    {
        public static bool IsStatic(this System.Reflection.MemberInfo mi)
        {
            return mi switch
            {
                System.Reflection.FieldInfo fi => fi.IsStatic,
                System.Reflection.PropertyInfo pi => (pi.GetMethod ?? pi.SetMethod)?.IsStatic ?? false,
                _ => false
            };
        }
    }
}
