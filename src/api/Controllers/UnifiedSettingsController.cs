using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.SlicerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class UnifiedSettingsController : ControllerBase
{
    private readonly ISettingsService _modularSettingsService;
    private readonly Dictionary<string, string> _keyNameToClassNameMap;
    private readonly ILogger<UnifiedSettingsController> _logger;

    public UnifiedSettingsController(
        ISettingsService modularSettingsService,
        ILogger<UnifiedSettingsController> logger)
    {
        _modularSettingsService = modularSettingsService;
        _logger = logger;
        _keyNameToClassNameMap = BuildKeyNameToClassNameMap();
    }

    /// <summary>
    /// Gets all application settings sections and their current values.
    /// </summary>
    /// <remarks>
    /// Returns a dictionary where each key is a settings section name (keyName) and the value is the current settings object for that section.
    /// </remarks>
    /// <returns>Dictionary of all settings sections keyed by section name.</returns>
    [HttpGet]
    public ActionResult<IDictionary<string, object>> Get()
    {
        // Return all settings as a dictionary with SectionName (Key) as top-level keys
        IEnumerable<SettingMetadata> allMetadata = _modularSettingsService.GetAllMetadata();
        Dictionary<string, object> result = new();
        foreach (SettingMetadata meta in allMetadata)
        {
            object settings = _modularSettingsService.GetByKey(meta.Key);
            result[meta.Key] = settings ?? new { };
        }
        return Ok(result);
    }

    /// <summary>
    /// Saves updated settings for one or more sections.
    /// </summary>
    /// <remarks>
    /// The payload must be a dictionary where each key is a settings section name (keyName) and the value is the updated settings object for that section.
    /// </remarks>
    /// <param name="settingsSections">Dictionary of keyName to settings object.</param>
    /// <returns>Result of save operation, including validation errors if any.</returns>
    [HttpPost]
    public ActionResult Update([FromBody] Dictionary<string, object> settingsSections)
    {
        try
        {
            _logger.LogDebug("Settings POST: Raw payload object: {@SettingsSections}", settingsSections);
            Dictionary<string, Type> keyToType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<AppSettingAttribute>(t) != null)
                .ToDictionary(
                    t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<AppSettingAttribute>(t)!.Key,
                    t => t
                );

            foreach (KeyValuePair<string, object> kvp in settingsSections)
            {
                string key = kvp.Key;
                object value = kvp.Value;
                _logger.LogDebug("Settings POST: Processing section key '{Key}'", key);
                if (!keyToType.TryGetValue(key, out Type? settingsType))
                {
                    _logger.LogWarning("Settings POST: Unknown section key '{Key}'", key);
                    continue;
                }

                if (value is System.Text.Json.JsonElement jsonElement)
                {
                    _logger.LogDebug("Settings POST: Deserializing section '{Key}' with type {Type}", key, settingsType);
                    try
                    {
                        object? typedSettings = JsonSerializer.Deserialize(jsonElement.GetRawText(), settingsType);
                        _logger.LogDebug("Settings POST: Deserialized object for '{Key}': {@TypedSettings}", key, typedSettings);
                        if (typedSettings != null)
                        {
                            // Verify the type implements IAppSetting (required for Save<T>)
                            if (!typeof(IAppSetting).IsAssignableFrom(settingsType))
                            {
                                _logger.LogError("Settings POST: Type '{Type}' does not implement IAppSetting and cannot be saved to database", settingsType.Name);
                                throw new InvalidOperationException($"Settings type '{settingsType.Name}' does not implement IAppSetting");
                            }

                            // If the settings class implements IValidatableSetting, run validation and log errors
                            if (typedSettings is IValidatableSetting validatable)
                            {
                                _logger.LogDebug("Settings POST: Validating section '{Key}'", key);
                                try
                                {
                                    validatable.Validate();
                                    _logger.LogDebug("Settings POST: Validation succeeded for section '{Key}'", key);
                                }
                                catch (ValidationException vex)
                                {
                                    _logger.LogError(vex, "Settings POST: Validation failed for section '{Key}': {Error}", key, vex.Message);
                                    // Return structured validation error for this section
                                    Dictionary<string, string> errors = new();
                                    if (vex.ValidationResult != null && vex.ValidationResult.MemberNames != null && vex.ValidationResult.MemberNames.Any())
                                    {
                                        foreach (string member in vex.ValidationResult.MemberNames)
                                        {
                                            errors[member] = vex.ValidationResult.ErrorMessage ?? vex.Message;
                                        }
                                    }
                                    else
                                    {
                                        errors[key] = vex.Message;
                                    }
                                    return BadRequest(new { message = $"Validation failed for section '{key}'", errors });
                                }
                            }
                            System.Reflection.MethodInfo? saveMethod = typeof(ISettingsService).GetMethod("Save");
                            if (saveMethod != null)
                            {
                                try
                                {
                                    System.Reflection.MethodInfo genericSaveMethod = saveMethod.MakeGenericMethod(settingsType);
                                    _logger.LogDebug("Settings POST: Invoking Save for section '{Key}'", key);
                                    _ = genericSaveMethod.Invoke(_modularSettingsService, new object[] { typedSettings });
                                    _logger.LogDebug("Settings POST: Save completed for section '{Key}'", key);
                                }
                                catch (System.Reflection.TargetInvocationException tie)
                                {
                                    // Unwrap the actual exception from reflection invoke
                                    Exception actualException = tie.InnerException ?? tie;
                                    _logger.LogError(actualException, "Settings POST: Save failed for section '{Key}'", key);
                                    throw actualException;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Settings POST: Deserialization returned null for section '{Key}'", key);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Settings POST: Deserialization/validation failed for section '{Key}'", key);
                        throw;
                    }
                }
                else
                {
                    _logger.LogWarning("Settings POST: Value for section '{Key}' is not a JsonElement", key);
                }
            }

            // No need to reload - Save() already updated the in-memory _settings dictionary
            // and cleared the change tracker to ensure fresh data on next query
            return Ok();
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException if present
            Exception actualException = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;

            _logger.LogError(actualException, "Settings POST: Exception during settings update");
            // If it's a ValidationException thrown from Save via reflection, return structured response
            if (actualException is ValidationException vex)
            {
                Dictionary<string, string> errors = new();
                if (vex.ValidationResult != null && vex.ValidationResult.MemberNames != null && vex.ValidationResult.MemberNames.Any())
                {
                    foreach (string member in vex.ValidationResult.MemberNames)
                    {
                        errors[member] = vex.ValidationResult.ErrorMessage ?? vex.Message;
                    }
                }
                else
                {
                    errors[""] = vex.Message;
                }
                return BadRequest(new { message = "Validation failed", errors });
            }

            return BadRequest(new { message = $"Failed to save settings: {actualException.Message}" });
        }
    }

    /// <summary>
    /// Gets metadata for all settings sections, including property names, types, and descriptions.
    /// </summary>
    /// <remarks>
    /// Used for dynamic UI generation and frontend validation.
    /// </remarks>
    /// <returns>Metadata for all settings sections.</returns>
    [HttpGet("metadata")]
    public ActionResult<IEnumerable<SettingMetadata>> GetMetadata()
    {
        try
        {
            IEnumerable<SettingMetadata> metadata = _modularSettingsService.GetAllMetadata();
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to get settings metadata: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the settings for a specific section by keyName.
    /// </summary>
    /// <param name="keyName">The key name of the settings section.</param>
    /// <returns>The settings object for the specified section.</returns>
    [HttpGet("{keyName}")]
    public ActionResult<object> GetSettingsByKeyName(string keyName)
    {
        try
        {
            string? className = MapKeyNameToClassName(keyName);
            if (className == null)
            {
                return NotFound($"Settings key '{keyName}' not found");
            }

            object settings = _modularSettingsService.GetByKey(keyName);
            return Ok(settings);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to get settings for key '{keyName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Heartbeat endpoint for discovery service.
    /// Updates the LastHeartbeat timestamp in NetworkDiscoverySettings to confirm service is alive.
    /// </summary>
    /// <param name="keyName">The key name - should be "NetworkDiscovery".</param>
    /// <returns>NoContent on success.</returns>
    [HttpPost("{keyName}/heartbeat")]
    public ActionResult SendHeartbeat(string keyName)
    {
        try
        {
            if (keyName != "NetworkDiscovery")
            {
                return BadRequest(new { message = "Heartbeat endpoint only supports NetworkDiscovery settings" });
            }

            // Get current discovery settings
            NetworkDiscoverySettings? currentSettings = _modularSettingsService.GetByKey(keyName) as NetworkDiscoverySettings;
            if (currentSettings == null)
            {
                _logger.LogWarning("Failed to get NetworkDiscoverySettings for heartbeat");
                return BadRequest(new { message = "Failed to get NetworkDiscoverySettings" });
            }

            // Update the heartbeat timestamp
            currentSettings.LastHeartbeat = DateTime.UtcNow;

            // Save the updated settings
            _modularSettingsService.Save(currentSettings);

            _logger.LogDebug("Heartbeat received and recorded for NetworkDiscoverySettings at {Timestamp}", currentSettings.LastHeartbeat);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process heartbeat for key '{KeyName}'", keyName);
            return StatusCode(500, new { message = $"Failed to process heartbeat: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates the settings for a specific section by keyName.
    /// </summary>
    /// <param name="keyName">The key name of the settings section.</param>
    /// <param name="settingsValues">The updated settings object for the section.</param>
    /// <returns>Result of save operation for the specified section.</returns>
    [HttpPost("{keyName}")]
    public async Task<ActionResult> UpdateSettingsByKeyNameAsync(string keyName, [FromBody] object settingsValues)
    {
        try
        {
            // Use the modular settings service to save the individual settings
            await UpdateAppSettingsPropertyAsync(keyName, settingsValues);
            return Ok();
        }
        catch (ValidationException vex)
        {
            Dictionary<string, string> errors = new();
            if (vex.ValidationResult != null && vex.ValidationResult.MemberNames != null && vex.ValidationResult.MemberNames.Any())
            {
                foreach (string member in vex.ValidationResult.MemberNames)
                {
                    errors[member] = vex.ValidationResult.ErrorMessage ?? vex.Message;
                }
            }
            else
            {
                errors[""] = vex.Message;
            }
            return BadRequest(new { message = $"Validation failed for class '{keyName}'", errors });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to save settings for class '{keyName}': {ex.Message}" });
        }
    }

    private Dictionary<string, string> BuildKeyNameToClassNameMap()
    {
        Dictionary<string, string> map = new();

        // Get all settings metadata from the service
        IEnumerable<SettingMetadata> allMetadata = _modularSettingsService.GetAllMetadata();

        foreach (SettingMetadata metadata in allMetadata)
        {
            // Map key to class name
            map[metadata.Key] = metadata.ClassName;
        }

        return map;
    }

    private string? MapKeyNameToClassName(string keyName)
    {
        return _keyNameToClassNameMap.TryGetValue(keyName, out string? className) ? className : null;
    }

    private async Task UpdateAppSettingsPropertyAsync(string keyName, object settingsValues)
    {
        // For now, we'll use the modular settings service to save individual settings
        // and then reload the unified AppSettings. This approach allows us to support
        // any settings class without hardcoding specific mappings.

        string className = MapKeyNameToClassName(keyName) ?? throw new ArgumentException($"Unknown settings key: {keyName}");

        // Save to modular settings service (this updates the underlying configuration)
        if (settingsValues is System.Text.Json.JsonElement jsonElement)
        {
            // Get the settings type from the modular service using the key
            object currentSettings = _modularSettingsService.GetByKey(keyName);
            Type settingsType = currentSettings.GetType();

            // Deserialize the JSON to the correct type
            object? typedSettings = JsonSerializer.Deserialize(jsonElement.GetRawText(), settingsType);
            if (typedSettings != null)
            {
                // Save using the modular service
                await Task.Run(() =>
                {
                    System.Reflection.MethodInfo? saveMethod = typeof(ISettingsService).GetMethod("Save");
                    if (saveMethod != null)
                    {
                        System.Reflection.MethodInfo genericSaveMethod = saveMethod.MakeGenericMethod(settingsType);
                        _ = genericSaveMethod.Invoke(_modularSettingsService, new[] { typedSettings });
                    }
                });

                // No need to reload - Save() already updated the in-memory _settings dictionary
                // and cleared the change tracker to ensure fresh data on next query
            }
        }
    }
}
