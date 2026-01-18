using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings;

public class ConfigurationSystemSettingsProvider(IConfiguration config) : ISystemSettingsProvider
{
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

    public T Get<T>() where T : class, new()
    {
        // Try to find a public constant/field or static property named SectionName on the type T
        FieldInfo? field = typeof(T).GetField("SectionName", BindingFlags.Public | BindingFlags.Static);
        PropertyInfo? prop = typeof(T).GetProperty("SectionName", BindingFlags.Public | BindingFlags.Static);
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
                MethodInfo? getter = prop.GetMethod;
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

        T instance = new();
        IConfigurationSection section = _config.GetSection(sectionName);
        section.Bind(instance);
        return instance;
    }
}

internal static class ReflectionExtensions
{
    public static bool IsStatic(this MemberInfo mi)
    {
        return mi switch
        {
            FieldInfo fi => fi.IsStatic,
            PropertyInfo pi => (pi.GetMethod ?? pi.SetMethod)?.IsStatic ?? false,
            _ => false
        };
    }
}
