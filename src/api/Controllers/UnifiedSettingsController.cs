using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.SlicerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class UnifiedSettingsController : ControllerBase
{
    private readonly SettingsService _modularSettingsService;
    private readonly IConfiguration _config;
    private readonly Dictionary<string, string> _keyNameToClassNameMap;
    private readonly ILogger<UnifiedSettingsController> _logger;

    public UnifiedSettingsController(
        SettingsService modularSettingsService,
        IConfiguration config,
        ILogger<UnifiedSettingsController> logger)
    {
        _modularSettingsService = modularSettingsService;
        _config = config;
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
        var allMetadata = _modularSettingsService.GetAllMetadata();
        var result = new Dictionary<string, object>();
        foreach (var meta in allMetadata)
        {
            var settings = _modularSettingsService.GetByKey(meta.Key);
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
            var keyToType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Farm.Infrastructure.Settings.AppSettingAttribute>(t) != null)
                .ToDictionary(
                    t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Farm.Infrastructure.Settings.AppSettingAttribute>(t)!.Key,
                    t => t
                );

            foreach (var kvp in settingsSections)
            {
                var key = kvp.Key;
                var value = kvp.Value;
                _logger.LogDebug("Settings POST: Processing section key '{Key}'", key);
                if (!keyToType.TryGetValue(key, out var settingsType))
                {
                    _logger.LogWarning("Settings POST: Unknown section key '{Key}'", key);
                    continue;
                }

                if (value is System.Text.Json.JsonElement jsonElement)
                {
                    _logger.LogDebug("Settings POST: Deserializing section '{Key}' with type {Type}", key, settingsType);
                    try
                    {
                        var typedSettings = System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), settingsType);
                        _logger.LogDebug("Settings POST: Deserialized object for '{Key}': {@TypedSettings}", key, typedSettings);
                        if (typedSettings != null)
                        {
                            // Verify the type implements IAppSetting (required for Save<T>)
                            if (!typeof(Farm.Infrastructure.Settings.IAppSetting).IsAssignableFrom(settingsType))
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
                                    var errors = new Dictionary<string, string>();
                                    if (vex.ValidationResult != null && vex.ValidationResult.MemberNames != null && vex.ValidationResult.MemberNames.Any())
                                    {
                                        foreach (var member in vex.ValidationResult.MemberNames)
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
                            var saveMethod = typeof(SettingsService).GetMethod("Save");
                            if (saveMethod != null)
                            {
                                try
                                {
                                    var genericSaveMethod = saveMethod.MakeGenericMethod(settingsType);
                                    _logger.LogDebug("Settings POST: Invoking Save for section '{Key}'", key);
                                    genericSaveMethod.Invoke(_modularSettingsService, new object[] { typedSettings });
                                    _logger.LogDebug("Settings POST: Save completed for section '{Key}'", key);
                                }
                                catch (System.Reflection.TargetInvocationException tie)
                                {
                                    // Unwrap the actual exception from reflection invoke
                                    var actualException = tie.InnerException ?? tie;
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
            var actualException = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;

            _logger.LogError(actualException, "Settings POST: Exception during settings update");
            // If it's a ValidationException thrown from Save via reflection, return structured response
            if (actualException is ValidationException vex)
            {
                var errors = new Dictionary<string, string>();
                if (vex.ValidationResult != null && vex.ValidationResult.MemberNames != null && vex.ValidationResult.MemberNames.Any())
                {
                    foreach (var member in vex.ValidationResult.MemberNames)
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
            var metadata = _modularSettingsService.GetAllMetadata();
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
            var className = MapKeyNameToClassName(keyName);
            if (className == null)
            {
                return NotFound($"Settings key '{keyName}' not found");
            }

            var settings = _modularSettingsService.GetByKey(keyName);
            return Ok(settings);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to get settings for key '{keyName}': {ex.Message}");
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
            var errors = new Dictionary<string, string>();
            if (vex.ValidationResult != null && vex.ValidationResult.MemberNames != null && vex.ValidationResult.MemberNames.Any())
            {
                foreach (var member in vex.ValidationResult.MemberNames)
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
        var map = new Dictionary<string, string>();

        // Get all settings metadata from the service
        var allMetadata = _modularSettingsService.GetAllMetadata();

        foreach (var metadata in allMetadata)
        {
            // Map key to class name
            map[metadata.Key] = metadata.ClassName;
        }

        return map;
    }

    private string? MapKeyNameToClassName(string keyName)
    {
        return _keyNameToClassNameMap.TryGetValue(keyName, out var className) ? className : null;
    }

    private async Task UpdateAppSettingsPropertyAsync(string keyName, object settingsValues)
    {
        // For now, we'll use the modular settings service to save individual settings
        // and then reload the unified AppSettings. This approach allows us to support
        // any settings class without hardcoding specific mappings.

        var className = MapKeyNameToClassName(keyName);
        if (className == null)
        {
            throw new ArgumentException($"Unknown settings key: {keyName}");
        }

        // Save to modular settings service (this updates the underlying configuration)
        if (settingsValues is System.Text.Json.JsonElement jsonElement)
        {
            // Get the settings type from the modular service using the key
            var currentSettings = _modularSettingsService.GetByKey(keyName);
            var settingsType = currentSettings.GetType();

            // Deserialize the JSON to the correct type
            var typedSettings = System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), settingsType);
            if (typedSettings != null)
            {
                // Save using the modular service
                await Task.Run(() =>
                {
                    var saveMethod = typeof(SettingsService).GetMethod("Save");
                    if (saveMethod != null)
                    {
                        var genericSaveMethod = saveMethod.MakeGenericMethod(settingsType);
                        genericSaveMethod.Invoke(_modularSettingsService, new[] { typedSettings });
                    }
                });

                // No need to reload - Save() already updated the in-memory _settings dictionary
                // and cleared the change tracker to ensure fresh data on next query
            }
        }
    }
}
