using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Discovers, loads, and validates all settings classes marked with [AppSetting].
    /// </summary>
    using Farm.Infrastructure.Data;
    using Microsoft.EntityFrameworkCore;

    public class SettingsService : ISettingsService
    {
        /// <summary>
        /// Returns the current settings object for a given settings class name (e.g., "DatabaseSettings").
        /// </summary>
        public object? GetSettingsClassValues(string className)
        {
            var type = _settingTypes.Find(t => t.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
            if (type == null)
            {
                return null;
            }

            var appAttr = type.GetCustomAttribute<AppSettingAttribute>();
            var sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();
            string? key = appAttr?.Key ?? sysAttr?.Key;
            if (key == null)
            {
                return null;
            }

            if (_settings.TryGetValue(key, out var value))
            {
                return value;
            }

            return null;
        }
        private readonly IUnifiedLoggingService _logger;

        public void Save<T>(T settings) where T : class, IAppSetting
        {
            ArgumentNullException.ThrowIfNull(settings);
            var type = typeof(T);
            var appAttr = type.GetCustomAttribute<AppSettingAttribute>();
            if (appAttr == null)
            {
                throw new InvalidOperationException($"Type {type.FullName} is not marked with [AppSetting]. Only AppSettings can be persisted to DB.");
            }
            _settings[appAttr.Key] = settings;

            // Persist to DB (AppSettings only)
            var json = System.Text.Json.JsonSerializer.Serialize(settings);
            var entity = _dbContext.AppSettingsEntities.FirstOrDefault(e => e.Key == appAttr.Key);
            if (entity == null)
            {
                _logger.LogInformation("[SettingsService] Adding new entity", null, new { Key = appAttr.Key });
                entity = new AppSettingsEntity
                {
                    Key = appAttr.Key,
                    SettingsJson = json,
                    UpdatedAt = DateTime.UtcNow
                };
                _dbContext.AppSettingsEntities.Add(entity);
            }
            else
            {
                _logger.LogInformation("[SettingsService] Updating existing entity", null, new { Key = entity.Key, Id = entity.Id });
                _logger.LogDebug("[SettingsService] JSON length change", null, new { OldLen = entity.SettingsJson?.Length ?? 0, NewLen = json.Length });

                var entry = _dbContext.Entry(entity);
                _logger.LogDebug("[SettingsService] Entity state before changes", null, new { State = entry.State.ToString() });

                entity.SettingsJson = json;
                entity.UpdatedAt = DateTime.UtcNow;

                // Explicitly mark as modified to ensure EF tracks the changes
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Modified;

                _logger.LogDebug("[SettingsService] Entity state after marking Modified", null, new { State = entry.State.ToString() });
            }

            var rowsAffected = _dbContext.SaveChanges();
            _logger.LogInformation("[SettingsService] SaveChanges returned", null, new { RowsAffected = rowsAffected });

            // Clear change tracker to ensure fresh data is loaded on next query
            _dbContext.ChangeTracker.Clear();
        }
        private Dictionary<string, object> _settings = new();
        private readonly List<Type> _settingTypes;
        private readonly AppDbContext _dbContext;

        public SettingsService(IConfiguration config)
        {
            // For DI: IConfiguration and AppDbContext
            throw new InvalidOperationException("Use the constructor with IConfiguration and AppDbContext");

        }

        public SettingsService(IConfiguration config, AppDbContext dbContext, IUnifiedLoggingService logger)
        {
            _settingTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.GetCustomAttribute<AppSettingAttribute>() != null || t.GetCustomAttribute<SystemSettingAttribute>() != null)
                .ToList();
            _dbContext = dbContext;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LoadSettings(config);
        }

        private void LoadSettings(IConfiguration config)
        {
            var newSettings = new Dictionary<string, object>();
            foreach (Type type in _settingTypes)
            {
                var appAttr = type.GetCustomAttribute<AppSettingAttribute>();
                var sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();
                string? key = appAttr?.Key ?? sysAttr?.Key;
                if (key == null)
                {
                    continue;
                }

                object? instance = null;
                if (appAttr != null)
                {
                    // AppSettings: try DB first, fallback to config
                    var dbEntity = _dbContext.AppSettingsEntities.FirstOrDefault(e => e.Key == appAttr.Key);
                    if (dbEntity != null && !string.IsNullOrWhiteSpace(dbEntity.SettingsJson))
                    {
                        try
                        {
                            instance = System.Text.Json.JsonSerializer.Deserialize(dbEntity.SettingsJson, type);
                        }
                        catch
                        {
                            instance = config.GetSection(appAttr.Key).Get(type) ?? Activator.CreateInstance(type);
                        }
                    }
                    else
                    {
                        instance = config.GetSection(appAttr.Key).Get(type) ?? Activator.CreateInstance(type);
                    }
                }
                else
                {
                    // SystemSettings: config only
                    instance = config.GetSection(sysAttr!.Key).Get(type) ?? Activator.CreateInstance(type);
                }
                if (instance == null)
                {
                    throw new InvalidOperationException($"Could not create instance of settings type {type.FullName}");
                }
                if (instance is IValidatableSetting validatable)
                {
                    validatable.Validate();
                }
                newSettings[key] = instance;
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

        public T Get<T>() where T : class
        {
            var result = _settings.Values.OfType<T>().FirstOrDefault();
            if (result == null)
            {
                throw new InvalidOperationException($"No settings instance found for type {typeof(T).Name}");
            }
            return result;
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
        /// Only returns AppSettings (IAppSetting), not SystemSettings (ISystemSetting).
        /// SystemSettings are configuration-only and cannot be modified through the UI.
        /// </summary>
        public IEnumerable<SettingMetadata> GetAllMetadata()
        {
            foreach (Type type in _settingTypes)
            {
                var appAttr = type.GetCustomAttribute<AppSettingAttribute>();
                var sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();

                // Skip SystemSettings - they should not appear in the UI
                if (sysAttr != null && appAttr == null)
                {
                    continue;
                }

                string? key = appAttr?.Key;
                if (key == null)
                {
                    continue;
                }

                var classDisplayAttr = type.GetCustomAttribute<SettingDisplayAttribute>();
                string? displayName = classDisplayAttr?.Name;
                string? description = classDisplayAttr?.Description;
                string? icon = classDisplayAttr?.Icon;
                string? group = classDisplayAttr?.Group;
                int? order = classDisplayAttr?.Order;

                List<SettingPropertyMetadata> props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p =>
                    {
                        var jsonAttr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                        if (jsonAttr == null)
                        {
                            throw new InvalidOperationException($"Property '{p.Name}' in settings class '{type.Name}' is missing [JsonPropertyName] attribute.");
                        }
                        var meta = new SettingPropertyMetadata
                        {
                            Name = jsonAttr.Name,
                            Type = p.PropertyType.Name,
                            Attributes = new System.Collections.ObjectModel.ReadOnlyCollection<string>(p.GetCustomAttributes().Select(a => a.GetType().Name).ToList())
                        };
                        var displayAttr = p.GetCustomAttribute<SettingDisplayAttribute>();
                        if (displayAttr != null)
                        {
#pragma warning disable S1244 // Floating point numbers should not be tested for equality
                            meta.Display = new SettingPropertyDisplayMetadata
                            {
                                Name = displayAttr.Name,
                                Description = displayAttr.Description,
                                Icon = displayAttr.Icon,
                                Group = displayAttr.Group,
                                Order = displayAttr.Order,
                                InputType = displayAttr.InputType,
                                IsMulti = displayAttr.IsMulti,
                                AllowedValues = displayAttr.AllowedValues,
                                MinValue = (displayAttr.MinValue == -1) ? null : displayAttr.MinValue,
                                MaxValue = (displayAttr.MaxValue == -1) ? null : displayAttr.MaxValue
                            };
#pragma warning restore S1244 // Floating point numbers should not be tested for equality
                        }
                        return meta;
                    }).ToList();
                yield return new SettingMetadata
                {
                    Key = key,
                    ClassName = type.Name,
                    DisplayName = displayName,
                    Description = description,
                    Icon = icon,
                    Group = group,
                    Order = order,
                    Properties = new System.Collections.ObjectModel.ReadOnlyCollection<SettingPropertyMetadata>(props)
                };
            }
        }
    }
}
