using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Discovers, loads, and validates all settings classes marked with [AppSetting].
/// </summary>
public class SettingsService : ISettingsService
{
    /// <summary>
    /// Returns the current settings object for a given settings class name (e.g., "DatabaseSettings").
    /// </summary>
    /// <param name="className">The name of the settings class to retrieve.</param>
    /// <returns>The settings object if found; otherwise, null.</returns>
    public object? GetSettingsClassValues(string className)
    {
        Type? type = _settingTypes.Find(t => t.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (type == null)
        {
            return null;
        }

        AppSettingAttribute? appAttr = type.GetCustomAttribute<AppSettingAttribute>();
        SystemSettingAttribute? sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();
        string? key = appAttr?.Key ?? sysAttr?.Key;
        return key == null ? null : _settings.TryGetValue(key, out object? value) ? value : null;
    }

    private readonly ILogger<SettingsService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public void Save<T>(T settings)
        where T : class, IAppSetting
    {
        ArgumentNullException.ThrowIfNull(settings);
        Type type = typeof(T);
        AppSettingAttribute? appAttr = type.GetCustomAttribute<AppSettingAttribute>() ?? throw new InvalidOperationException($"Type {type.FullName} is not marked with [AppSetting]. Only AppSettings can be persisted to DB.");
        _settings[appAttr.Key] = settings;

        // Persist to DB (AppSettings only)
        string json = JsonSerializer.Serialize(settings);

        // Use repository to persist settings
        // Note: This is called from non-async context, so we use sync over async as a workaround
        // Ideally this method should be async
#pragma warning disable VSTHRD002
        Task setTask = _settingsRepo.SetAsync(appAttr.Key, json);
        setTask.Wait();
        Task saveTask = _settingsRepo.SaveChangesAsync();
        saveTask.Wait();
#pragma warning restore VSTHRD002
    }

    private Dictionary<string, object> _settings = new();
    private readonly List<Type> _settingTypes;

    public SettingsService(IConfiguration config)
    {
        // For DI: IConfiguration and IDbContextFactory
        throw new InvalidOperationException("Use the constructor with IConfiguration and IDbContextFactory<AppDbContext>");
    }

    private readonly Farm.Infrastructure.Repositories.Settings.IAppSettingsRepository _settingsRepo;

    public SettingsService(IConfiguration config, IDbContextFactory<AppDbContext> dbContextFactory, ILogger<SettingsService> logger, Farm.Infrastructure.Repositories.Settings.IAppSettingsRepository settingsRepo)
    {
        _settingTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(t => t != null).Cast<Type>();
                }
            })
            .Where(t => t.GetCustomAttribute<AppSettingAttribute>() != null || t.GetCustomAttribute<SystemSettingAttribute>() != null)
            .ToList();
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsRepo = settingsRepo ?? throw new ArgumentNullException(nameof(settingsRepo));
        LoadSettings(config);
    }

    private void LoadSettings(IConfiguration config)
    {
        Dictionary<string, object> newSettings = new Dictionary<string, object>();
        using AppDbContext dbContext = _dbContextFactory.CreateDbContext();

        foreach (Type type in _settingTypes)
        {
            AppSettingAttribute? appAttr = type.GetCustomAttribute<AppSettingAttribute>();
            SystemSettingAttribute? sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();
            string? key = appAttr?.Key ?? sysAttr?.Key;
            if (key == null)
            {
                continue;
            }

            object? instance = null;
            if (appAttr != null)
            {
                // AppSettings: try DB first, fallback to config
                AppSettingsEntity? dbEntity = dbContext.AppSettingsEntities.FirstOrDefault(e => e.Key == appAttr.Key);
                if (dbEntity != null && !string.IsNullOrWhiteSpace(dbEntity.SettingsJson))
                {
                    try
                    {
                        instance = JsonSerializer.Deserialize(dbEntity.SettingsJson, type);
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
    /// <param name="config">The configuration source to reload settings from.</param>
    public void Reload(IConfiguration config)
    {
        LoadSettings(config);
    }

    public T Get<T>()
        where T : class
    {
        T? result = _settings.Values.OfType<T>().FirstOrDefault();
        return result == null ? throw new InvalidOperationException($"No settings instance found for type {typeof(T).Name}") : result;
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
    /// Attempts to acquire a distributed lock for a given key.
    /// Returns true if the lock was acquired (key did not exist or was in a completion state).
    /// </summary>
    /// <param name="lockKey">The unique key identifying the lock.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>True if the lock was acquired; otherwise, false.</returns>
    public async Task<bool> TryAcquireLockAsync(string lockKey, CancellationToken ct = default)
    {
        AppSettingsEntity? existingLock = await _settingsRepo.GetAsync(lockKey, ct);
        if (existingLock?.SettingsJson is "completed" or "in-progress")
        {
            return false; // Lock already held
        }

        // Acquire lock
        await _settingsRepo.SetAsync(lockKey, "in-progress", ct);
        await _settingsRepo.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Marks a distributed lock as completed.
    /// </summary>
    /// <param name="lockKey">The unique key identifying the lock to complete.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CompleteLockAsync(string lockKey, CancellationToken ct = default)
    {
        await _settingsRepo.SetAsync(lockKey, "completed", ct);
        await _settingsRepo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Clears a distributed lock to allow retry.
    /// </summary>
    /// <param name="lockKey">The unique key identifying the lock to clear.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ClearLockAsync(string lockKey, CancellationToken ct = default)
    {
        await _settingsRepo.DeleteAsync(lockKey, ct);
        await _settingsRepo.SaveChangesAsync(ct);
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
            AppSettingAttribute? appAttr = type.GetCustomAttribute<AppSettingAttribute>();
            SystemSettingAttribute? sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();

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

            SettingDisplayAttribute? classDisplayAttr = type.GetCustomAttribute<SettingDisplayAttribute>();
            string? displayName = classDisplayAttr?.Name;
            string? description = classDisplayAttr?.Description;
            string? icon = classDisplayAttr?.Icon;
            string? group = classDisplayAttr?.Group;

            // Order now represents order within the group, not absolute order
            int? order = classDisplayAttr?.Order;

            List<SettingPropertyMetadata> props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .Select(p =>
                {
                    JsonPropertyNameAttribute? jsonAttr = p.GetCustomAttribute<JsonPropertyNameAttribute>() ?? throw new InvalidOperationException($"Property '{p.Name}' in settings class '{type.Name}' is missing [JsonPropertyName] attribute.");
                    SettingPropertyMetadata meta = new()
                    {
                        Name = jsonAttr.Name,
                        Type = p.PropertyType.Name,
                        Attributes = new System.Collections.ObjectModel.ReadOnlyCollection<string>(p.GetCustomAttributes().Select(a => a.GetType().Name).ToList())
                    };
                    SettingDisplayAttribute? displayAttr = p.GetCustomAttribute<SettingDisplayAttribute>();
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

    /// <summary>
    /// Returns metadata for all discovered settings groups for UI organization.
    /// Groups are derived from [SettingGroup] attributes on settings classes.
    /// If a group has no explicit attribute, it uses default ordering (100).
    /// </summary>
    public IEnumerable<SettingGroupMetadata> GetAllGroupMetadata()
    {
        // Collect all group definitions from SettingGroupAttribute
        Dictionary<string, SettingGroupMetadata> groups = new(StringComparer.OrdinalIgnoreCase);

        foreach (Type type in _settingTypes)
        {
            // Skip SystemSettings
            AppSettingAttribute? appAttr = type.GetCustomAttribute<AppSettingAttribute>();
            SystemSettingAttribute? sysAttr = type.GetCustomAttribute<SystemSettingAttribute>();
            if (sysAttr != null && appAttr == null)
            {
                continue;
            }

            // Check for SettingGroupAttribute
            SettingGroupAttribute? groupAttr = type.GetCustomAttribute<SettingGroupAttribute>();
            if (groupAttr != null)
            {
                string groupKey = groupAttr.GroupKey;

                // If we already have this group, keep the one with lower order
                if (groups.TryGetValue(groupKey, out SettingGroupMetadata? existing))
                {
                    if (groupAttr.Order < existing.Order)
                    {
                        groups[groupKey] = new SettingGroupMetadata
                        {
                            Key = groupKey,
                            DisplayName = groupAttr.DisplayName ?? groupKey,
                            Description = groupAttr.Description,
                            Icon = groupAttr.Icon,
                            Order = groupAttr.Order
                        };
                    }
                }
                else
                {
                    groups[groupKey] = new SettingGroupMetadata
                    {
                        Key = groupKey,
                        DisplayName = groupAttr.DisplayName ?? groupKey,
                        Description = groupAttr.Description,
                        Icon = groupAttr.Icon,
                        Order = groupAttr.Order
                    };
                }
            }

            // Also collect groups from SettingDisplay.Group that don't have explicit SettingGroupAttribute
            SettingDisplayAttribute? displayAttr = type.GetCustomAttribute<SettingDisplayAttribute>();
            if (displayAttr?.Group != null && !groups.ContainsKey(displayAttr.Group))
            {
                groups[displayAttr.Group] = new SettingGroupMetadata
                {
                    Key = displayAttr.Group,
                    DisplayName = displayAttr.Group,
                    Description = null,
                    Icon = null,
                    Order = 100 // Default order for undefined groups
                };
            }
        }

        return groups.Values.OrderBy(g => g.Order).ThenBy(g => g.DisplayName);
    }
}
