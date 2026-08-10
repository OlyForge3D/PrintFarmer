using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class UnifiedSettingsController(
    ISettingsService modularSettingsService,
    DiscoveryHeartbeatMonitorService discoveryMonitor,
    ILogger<UnifiedSettingsController> logger,
    Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster? coverageBroadcaster = null) : ControllerBase
{
    private readonly ISettingsService _modularSettingsService = modularSettingsService;
    private readonly DiscoveryHeartbeatMonitorService _discoveryMonitor = discoveryMonitor;
    private readonly ILogger<UnifiedSettingsController> _logger = logger;
    private readonly Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster? _coverageBroadcaster = coverageBroadcaster;

    // Keys for settings types that own their own secret fields (encrypted tokens, etc.) and must
    // not be exposed or mutated through the generic settings surface.  Each such type has a
    // dedicated admin controller that handles masking / encryption correctly.
    //
    // This filter applies to the metadata endpoint as well as the value endpoints, and that is
    // deliberate: the encrypted field is a serialized property with a [JsonPropertyName], so it
    // appears in GetAllMetadata() output and would be returned by GET /api/settings/{key}. See
    // SettingsMetadataCoverageTests, which fails if a settings class grows a secret-bearing
    // property without being listed here.
    private static readonly HashSet<string> _settingsBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        HomeAssistantSettings.SectionName,
        TelegramSettings.SectionName
    };

    /// <summary>
    /// Section keys hidden from the generic settings surface because the type owns encrypted
    /// fields. Exposed so tests can assert the list stays in sync with the settings classes
    /// rather than re-declaring it and drifting.
    /// </summary>
    public static IReadOnlyCollection<string> SecretBearingSectionKeys => _settingsBlocklist;

    // Section keys that may be read WITHOUT authentication. This is an allowlist, not a blocklist:
    // it fails CLOSED. Only sections that a trusted, tokenless internal component genuinely needs
    // are listed here; every other section requires a signed-in user. The printer-discovery
    // microservice runs out-of-process, holds no user credential, and polls its own configuration
    // (NetworkDiscovery) to decide when/what to scan — see
    // src/printer-discovery/BackgroundServices/PeriodicDiscoveryBackgroundService.cs. Adding a new
    // setting can never silently expose it anonymously; a key must be listed here deliberately.
    private static readonly HashSet<string> _anonymousReadAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        NetworkDiscoverySettings.SectionName
    };

    private bool IsAuthenticated() => User.Identity?.IsAuthenticated == true;

    // Lazy-initialize this since it depends on _modularSettingsService
    private Dictionary<string, string>? _keyNameToClassNameMap;

    private Dictionary<string, string> _keyNameToClassNameMapCache => _keyNameToClassNameMap ??= BuildKeyNameToClassNameMap();

    /// <summary>
    /// Gets all application settings sections and their current values.
    /// </summary>
    /// <remarks>
    /// Returns a dictionary where each key is a settings section name (keyName) and the value is the current settings object for that section.
    /// Requires authentication (class-level <c>[Authorize]</c>): the aggregate surface exposes internal
    /// URLs, intervals, file paths, feature flags and hostnames, and has no anonymous consumer. The
    /// printer-discovery microservice reads only a single section via the per-key endpoint, so this
    /// endpoint deliberately does not carry <c>[AllowAnonymous]</c>.
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
            // Skip settings types that manage their own secret fields.
            // These must be accessed via their dedicated admin controllers.
            if (_settingsBlocklist.Contains(meta.Key))
            {
                continue;
            }

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
    [Authorize(Roles = "farm_admin")]
    [HttpPost]
    public async Task<ActionResult> UpdateAsync([FromBody] Dictionary<string, object> settingsSections)
    {
        // Track whether SpoolCoverage settings changed so we can broadcast a
        // coverage-invalidation event after all sections persist (#709 item 5).
        bool spoolCoverageChanged = false;

        // Tracks the section being processed so the outer catch can attribute a memberless
        // ValidationException (thrown from Save via reflection) to a real section key rather than
        // an empty string. See the outer catch and BuildValidationErrorResponse for the shape.
        string? currentKey = null;
        try
        {
            _logger.LogDebug("Settings POST: Raw payload object keys: {Keys}", string.Join(", ", settingsSections.Keys));
            Dictionary<string, Type> keyToType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<AppSettingAttribute>(t) != null)
                .ToDictionary(
                    t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<AppSettingAttribute>(t)!.Key,
                    t => t);

            foreach (KeyValuePair<string, object> kvp in settingsSections)
            {
                string key = kvp.Key;
                object value = kvp.Value;
                currentKey = key;
                _logger.LogDebug("Settings POST: Processing section key '{Key}'", LogSanitizer.Sanitize(key));

                // Skip settings types that manage their own secret fields.
                if (_settingsBlocklist.Contains(key))
                {
                    _logger.LogWarning("Settings POST: Skipping blocked section '{Key}' — use the dedicated admin endpoint", LogSanitizer.Sanitize(key));
                    continue;
                }

                if (!keyToType.TryGetValue(key, out Type? settingsType))
                {
                    _logger.LogWarning("Settings POST: Unknown section key '{Key}'", LogSanitizer.Sanitize(key));
                    continue;
                }

                if (value is System.Text.Json.JsonElement jsonElement)
                {
                    _logger.LogDebug("Settings POST: Deserializing section '{Key}' with type {Type}", LogSanitizer.Sanitize(key), settingsType);
                    try
                    {
                        object? typedSettings = JsonSerializer.Deserialize(jsonElement.GetRawText(), settingsType);
                        _logger.LogDebug("Settings POST: Deserialized section '{Key}' successfully", LogSanitizer.Sanitize(key));
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
                                _logger.LogDebug("Settings POST: Validating section '{Key}'", LogSanitizer.Sanitize(key));
                                try
                                {
                                    validatable.Validate();
                                    _logger.LogDebug("Settings POST: Validation succeeded for section '{Key}'", LogSanitizer.Sanitize(key));
                                }
                                catch (ValidationException vex)
                                {
                                    _logger.LogError(vex, "Settings POST: Validation failed for section '{Key}': {Error}", LogSanitizer.Sanitize(key), vex.Message);
                                    return BuildValidationErrorResponse(vex, key);
                                }
                            }

                            System.Reflection.MethodInfo? saveMethod = typeof(ISettingsService).GetMethod("Save");
                            if (saveMethod != null)
                            {
                                try
                                {
                                    System.Reflection.MethodInfo genericSaveMethod = saveMethod.MakeGenericMethod(settingsType);
                                    _logger.LogDebug("Settings POST: Invoking Save for section '{Key}'", LogSanitizer.Sanitize(key));
                                    _ = genericSaveMethod.Invoke(_modularSettingsService, new object[] { typedSettings });
                                    _logger.LogDebug("Settings POST: Save completed for section '{Key}'", LogSanitizer.Sanitize(key));

                                    // #709 item 5: coverage thresholds changed.
                                    if (string.Equals(key, SpoolCoverageSettings.SectionName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        spoolCoverageChanged = true;
                                    }
                                }
                                catch (System.Reflection.TargetInvocationException tie)
                                {
                                    // Unwrap the actual exception from reflection invoke
                                    Exception actualException = tie.InnerException ?? tie;
                                    _logger.LogError(actualException, "Settings POST: Save failed for section '{Key}'", LogSanitizer.Sanitize(key));
                                    throw actualException;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Settings POST: Deserialization returned null for section '{Key}'", LogSanitizer.Sanitize(key));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Settings POST: Deserialization/validation failed for section '{Key}'", LogSanitizer.Sanitize(key));
                        throw;
                    }
                }
                else
                {
                    _logger.LogWarning("Settings POST: Value for section '{Key}' is not a JsonElement", LogSanitizer.Sanitize(key));
                }
            }

            // No need to reload - Save() already updated the in-memory _settings dictionary
            // and cleared the change tracker to ensure fresh data on next query
            if (spoolCoverageChanged && _coverageBroadcaster is not null)
            {
                await _coverageBroadcaster.BroadcastFleetChangedAsync(
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.ThresholdChanged,
                    HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException if present
            Exception actualException = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;

            _logger.LogError(actualException, "Settings POST: Exception during settings update");

            // If it's a ValidationException thrown from Save via reflection, return the same
            // structured response the inline/per-key paths produce: top-level `message` carries the
            // concrete reason (not a generic "Validation failed"), and a memberless exception is
            // keyed under the section being processed rather than an unlookup-able empty string.
            if (actualException is ValidationException vex)
            {
                return BuildValidationErrorResponse(vex, currentKey ?? "settings");
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
            // Materialize inside the try: GetAllMetadata() is a lazy yield iterator, so without
            // this the enumeration (and any exception, e.g. a settings property missing
            // [JsonPropertyName]) would happen during response serialization — after the 200 status
            // and headers are already on the wire — surfacing to the browser as a truncated
            // ERR_INCOMPLETE_CHUNKED_ENCODING instead of a clean error. ToList() forces failure here.
            List<SettingMetadata> metadata = _modularSettingsService.GetAllMetadata()
                .Where(meta => !_settingsBlocklist.Contains(meta.Key))
                .ToList();
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to get settings metadata: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets metadata for all settings groups, including display names, icons, and ordering.
    /// </summary>
    /// <remarks>
    /// Used for organizing settings sections in the UI sidebar.
    /// Groups are defined via [SettingGroup] attributes on settings classes.
    /// </remarks>
    /// <returns>Metadata for all settings groups, ordered by their Order property.</returns>
    [HttpGet("groups")]
    public ActionResult<IEnumerable<SettingGroupMetadata>> GetGroups()
    {
        try
        {
            // Materialize inside the try (lazy yield iterator) so failures surface as a clean
            // 500 rather than a mid-stream truncated response. See GetMetadata() for details.
            List<SettingGroupMetadata> groups = _modularSettingsService.GetAllGroupMetadata().ToList();
            return Ok(groups);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to get settings group metadata: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the settings for a specific section by keyName.
    /// </summary>
    /// <remarks>
    /// Carries <c>[AllowAnonymous]</c> so the tokenless printer-discovery microservice can read its
    /// own configuration, but anonymous access is restricted to <see cref="_anonymousReadAllowlist"/>
    /// (fails closed). Any other section requires a signed-in user; secret-bearing sections in
    /// <see cref="_settingsBlocklist"/> are hidden entirely.
    /// </remarks>
    /// <param name="keyName">The key name of the settings section.</param>
    /// <returns>The settings object for the specified section.</returns>
    [AllowAnonymous]
    [HttpGet("{keyName}")]
    public ActionResult<object> GetSettingsByKeyName(string keyName)
    {
        // Block settings types that manage their own secret fields.
        if (_settingsBlocklist.Contains(keyName))
        {
            return NotFound($"Settings key '{keyName}' not found");
        }

        // Fail closed for anonymous callers: only allowlisted sections may be read without a user
        // token. This prevents the endpoint from leaking internal URLs, intervals, paths and feature
        // flags to unauthenticated callers, while still letting the discovery microservice read the
        // one section it depends on.
        if (!IsAuthenticated() && !_anonymousReadAllowlist.Contains(keyName))
        {
            return Unauthorized();
        }

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
    /// <remarks>
    /// Deliberately <c>[AllowAnonymous]</c>: the printer-discovery microservice posts this heartbeat
    /// on a timer (src/printer-discovery/BackgroundServices/HeartbeatBackgroundService.cs) and holds
    /// no user credential. The endpoint is narrowly scoped — it rejects any keyName other than
    /// <c>NetworkDiscovery</c> and only writes <c>LastHeartbeat</c> — so the residual abuse is
    /// limited to an anonymous caller keeping discovery <em>looking</em> alive and suppressing a
    /// genuine "discovery down" dashboard signal. It cannot read or mutate any other setting.
    /// Fully removing anonymous access here requires a coordinated change: a shared service
    /// credential provisioned to both the API and the discovery microservice (and wired through the
    /// compose templates). That is out of scope for this fix and tracked as a follow-up; closing it
    /// here without updating the microservice would silently break discovery heartbeats.
    /// </remarks>
    /// <param name="keyName">The key name - should be "NetworkDiscovery".</param>
    /// <returns>NoContent on success.</returns>
    [AllowAnonymous]
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

            // Notify the background service monitor so it appears in the dashboard widget
            _discoveryMonitor.OnHeartbeatReceived();

            _logger.LogDebug("Heartbeat received and recorded for NetworkDiscoverySettings at {Timestamp}", currentSettings.LastHeartbeat);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process heartbeat for key '{KeyName}'", LogSanitizer.Sanitize(keyName));
            return StatusCode(500, new { message = $"Failed to process heartbeat: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates the settings for a specific section by keyName.
    /// </summary>
    /// <param name="keyName">The key name of the settings section.</param>
    /// <param name="settingsValues">The updated settings object for the section.</param>
    /// <returns>Result of save operation for the specified section.</returns>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("{keyName}")]
    public async Task<ActionResult> UpdateSettingsByKeyNameAsync(string keyName, [FromBody] object settingsValues)
    {
        // Block settings types that manage their own secret fields.
        if (_settingsBlocklist.Contains(keyName))
        {
            return NotFound($"Settings key '{keyName}' not found");
        }

        try
        {
            // Use the modular settings service to save the individual settings
            await UpdateAppSettingsPropertyAsync(keyName, settingsValues);

            // #709 item 5: coverage thresholds changed → fleet-wide invalidation.
            if (_coverageBroadcaster is not null
                && string.Equals(keyName, SpoolCoverageSettings.SectionName, StringComparison.OrdinalIgnoreCase))
            {
                await _coverageBroadcaster.BroadcastFleetChangedAsync(
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.ThresholdChanged,
                    HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return Ok();
        }
        catch (ValidationException vex)
        {
            _logger.LogError(vex, "Settings POST: Validation failed for section '{Key}': {Error}", LogSanitizer.Sanitize(keyName), vex.Message);
            return BuildValidationErrorResponse(vex, keyName);
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
        return _keyNameToClassNameMapCache.TryGetValue(keyName, out string? className) ? className : null;
    }

    private async Task UpdateAppSettingsPropertyAsync(string keyName, object settingsValues)
    {
        // For now, we'll use the modular settings service to save individual settings
        // and then reload the unified AppSettings. This approach allows us to support
        // any settings class without hardcoding specific mappings.
        _ = MapKeyNameToClassName(keyName) ?? throw new ArgumentException($"Unknown settings key: {keyName}");

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
                // Run the same validation the bulk POST path runs. Without this, invalid values
                // that the bulk endpoint would reject with a structured 400 would silently persist
                // through the per-key endpoint. ValidationException bubbles to the caller, which
                // translates it into the shared structured 400 response.
                if (typedSettings is IValidatableSetting validatable)
                {
                    _logger.LogDebug("Settings POST (per-key): Validating section '{Key}'", LogSanitizer.Sanitize(keyName));
                    validatable.Validate();
                    _logger.LogDebug("Settings POST (per-key): Validation succeeded for section '{Key}'", LogSanitizer.Sanitize(keyName));
                }

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

    /// <summary>
    /// Translates a <see cref="ValidationException"/> into the structured <c>400 Bad Request</c>
    /// response the settings UI expects: an object with <c>message</c> (string) and
    /// <c>errors</c> (dictionary of field-name → error-message). The React SettingsPage parses
    /// <c>errors</c> keys, splitting on '.' into <c>section.field</c> and mapping unqualified
    /// keys back to their section via metadata. Called from both the bulk and per-key POST
    /// paths so both endpoints produce byte-for-byte identical error bodies.
    /// </summary>
    /// <remarks>
    /// The top-level <c>message</c> is the string the React SettingsPage renders in its save-error
    /// banner (<c>firstMessage ?? summary</c>). It carries <see cref="Exception.Message"/> — the
    /// concrete reason (e.g. "Invalid CIDR subnet: 10.0.0.0/foo") — rather than a generic
    /// "Validation failed for section 'X'". Most settings classes raise memberless
    /// <see cref="ValidationException"/>s (21 of the 23 <c>throw new ValidationException(...)</c>
    /// sites at time of writing); for those the <c>errors[sectionKey]</c> entry does not map to
    /// any rendered <c>prop.name</c> in <c>SettingsPagelet</c>, so the top-level <c>message</c>
    /// is the only place the concrete reason reaches the user.
    /// </remarks>
    private static BadRequestObjectResult BuildValidationErrorResponse(ValidationException vex, string sectionKey)
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
            // Memberless throws — the frontend recognises a bare key that equals the section key
            // as a section-level error and renders it via SettingsPagelet's `error` prop.
            errors[sectionKey] = vex.Message;
        }

        return new BadRequestObjectResult(new { message = vex.Message, errors });
    }
}
