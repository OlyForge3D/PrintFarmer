using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Discovers, loads, and validates all settings classes marked with [AppSetting].
    /// </summary>
    public class SettingsService
    {
        private Dictionary<string, object> _settings = new();
        private readonly List<Type> _settingTypes;

        public SettingsService(IConfiguration config)
        {
            _settingTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.GetCustomAttribute<AppSettingAttribute>() != null)
                .ToList();
            LoadSettings(config);
        }

        private void LoadSettings(IConfiguration config)
        {
            var newSettings = new Dictionary<string, object>();
            foreach (var type in _settingTypes)
            {
                var attr = type.GetCustomAttribute<AppSettingAttribute>();
                if (attr == null)
                {
                    continue;
                }
                var instance = config.GetSection(attr.Key).Get(type) ?? Activator.CreateInstance(type);
                if (instance == null)
                {
                    throw new InvalidOperationException($"Could not create instance of settings type {type.FullName}");
                }
                if (instance is IValidatableSetting validatable)
                {
                    validatable.Validate();
                }
                newSettings[attr.Key] = instance;
            }
            _settings = newSettings;
        }

        /// <summary>
        /// Reloads all settings from the provided configuration at runtime.
        /// </summary>
        public void Reload(IConfiguration config)
        {
            LoadSettings(config);
        }

        public T Get<T>() where T : class, IAppSetting
        {
            return _settings.Values.OfType<T>().First();
        }

        public object GetByKey(string key)
        {
            return _settings[key];
        }

        public IEnumerable<object> All
        {
            get { return _settings.Values; }
        }
        /// <summary>
        /// Returns metadata for all discovered settings classes for dynamic UI generation.
        /// </summary>
        public IEnumerable<SettingMetadata> GetAllMetadata()
        {
            foreach (var type in _settingTypes)
            {
                var attr = type.GetCustomAttribute<AppSettingAttribute>();
                if (attr == null) continue;
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => new SettingPropertyMetadata
                    {
                        Name = p.Name,
                        Type = p.PropertyType.Name,
                        // Optionally: extract validation attributes, description, etc.
                        Attributes = p.GetCustomAttributes().Select(a => a.GetType().Name).ToList()
                    }).ToList();
                yield return new SettingMetadata
                {
                    Key = attr.Key,
                    ClassName = type.Name,
                    Properties = props
                };
            }
        }

        public class SettingMetadata
        {
            public string Key { get; set; } = string.Empty;
            public string ClassName { get; set; } = string.Empty;
            public List<SettingPropertyMetadata> Properties { get; set; } = new();
        }

        public class SettingPropertyMetadata
        {
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public List<string> Attributes { get; set; } = new();
        }
    }
}
