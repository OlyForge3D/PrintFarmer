
# Modular Extensible Settings System Implementation Plan

## Overview
This plan describes how to migrate PrintFarmer's settings architecture to a modular, attribute-driven, and extensible system. Each settings class is independently discoverable, validated, and loaded at runtime, enabling easy addition of new settings (e.g., for slicers, integrations, or features) without central code changes.


## Goals
- **No static coupling:** No more hardcoded properties in a central `AppSettings` class.
- **Runtime discovery:** All settings classes are discovered via reflection and attributes.
- **Per-class validation:** Each settings class can implement its own validation logic.
- **Extensibility:** Adding new settings is as simple as creating a new class and decorating it.
- **Centralized access:** All settings are available via a unified service API.
- **UI-Driven:** All settings currently editable in the Admin UI are migrated to settings classes, and the Admin UI dynamically discovers and displays all settings classes as logical, extensible pagelets.

---

## Sample Code and Usage Examples

### 1. Attribute and Interfaces

```csharp
// Attribute to mark settings classes
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AppSettingAttribute : Attribute
{
    public string Key { get; }
    public AppSettingAttribute(string key) => Key = key;
}

// Marker interface (optional)
public interface IAppSetting { }

// Validation interface
public interface IValidatableSetting
{
    void Validate();
    // Optionally: Task ValidateAsync();
}
```

### 2. Example Settings Class

```csharp
[AppSetting("Slicer.MySlicer")]
public class MySlicerSettings : IAppSetting, IValidatableSetting
{
    public string Path { get; set; }
    public int Threads { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ValidationException("Path is required.");
        if (Threads < 1)
            throw new ValidationException("Threads must be >= 1.");
    }
}
```

### 3. Settings Service Discovery (Simplified)

```csharp
public class SettingsService
{
    private readonly Dictionary<string, object> _settings = new();

    public SettingsService(IConfiguration config)
    {
        var settingTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttribute<AppSettingAttribute>() != null);
        foreach (var type in settingTypes)
        {
            var attr = type.GetCustomAttribute<AppSettingAttribute>();
            var instance = config.GetSection(attr.Key).Get(type) ?? Activator.CreateInstance(type);
            if (instance is IValidatableSetting validatable)
                validatable.Validate();
            _settings[attr.Key] = instance;
        }
    }

    public T Get<T>() where T : class, IAppSetting => _settings.Values.OfType<T>().First();
    public object GetByKey(string key) => _settings[key];
    public IEnumerable<object> GetAll() => _settings.Values;
}
```

### 4. Usage Example

```csharp
// In DI registration:
services.AddSingleton<SettingsService>();

// In a controller or service:
var mySlicerSettings = settingsService.Get<MySlicerSettings>();
```

---

## Phase 1: Foundation & Attribute Model
- [ ] Define `[AppSetting]` attribute for marking settings classes.
- [ ] Define `IAppSetting` marker interface (for type safety).
- [ ] Define `IValidatableSetting` interface with `Validate()`/`ValidateAsync()`.
- [ ] Refactor existing settings classes to use attribute and interfaces.

---

## Phase 2: Settings Service & Discovery
- [ ] Implement `SettingsService`:
    - Use reflection to scan assemblies for `[AppSetting]` classes.
    - Register discovered settings using the attribute's key.
    - Load settings from configuration (JSON, env vars, etc.) by key.
    - Expose `Get<T>()`, `GetByKey(string)`, and `GetAll()` APIs.
- [ ] Support reload/refresh of settings at runtime (optional).

---

## Phase 3: Validation Pipeline
- [ ] On load, call `Validate()`/`ValidateAsync()` for all settings implementing `IValidatableSetting`.
- [ ] Aggregate and surface validation errors at startup (fail fast or log as warnings).
- [ ] Add tests for validation logic and error handling.

---


## Phase 4: Migration, Integration & UI Alignment
- [ ] Audit all settings currently editable in the Admin UI (review existing settings pages/components and API endpoints).
- [ ] For each setting, ensure it is represented as a settings class using `[AppSetting]` and interfaces.
- [ ] For any setting not yet represented as a settings class, create a new class in `src/Farm.Infrastructure/Settings/`:
    - Decorate with `[AppSetting("Key")]`.
    - Implement `IAppSetting` and, if needed, `IValidatableSetting`.
    - Add properties for each setting field.
    - Add validation logic as appropriate.
- [ ] Migrate configuration binding and persistence to use the new settings classes.
- [ ] Update the settings service to ensure all new classes are discoverable and loaded.
- [ ] Update all usages to use `SettingsService` instead of direct property access.
- [ ] Update configuration binding logic to support new model.
- [ ] Update startup and validation logic to use new service.
- [ ] Expose a generic API endpoint (e.g., `/api/settings`) to:
    - List all available settings classes (with metadata: key, display name, description).
    - Get and update values for each settings class by key.
- [ ] Ensure validation is enforced on update.
- [ ] Add OpenAPI documentation for the new endpoints.

---


## Phase 5: Extensibility, Dynamic UI & Documentation
- [ ] Refactor the Admin UI to fetch the list of all available settings classes from the backend (via the new API endpoint).
- [ ] For each settings class, dynamically generate a UI section ("settings pagelet") based on its metadata and properties.
- [ ] Group settings pagelets logically (e.g., by feature, integration, or category) using metadata from the backend or a local mapping.
- [ ] Each pagelet should display all editable fields for its settings class, with appropriate input types and validation messages.
- [ ] Provide clear display names, descriptions, and help text for each field (using metadata or annotations).
- [ ] Allow users to edit settings in each pagelet and submit changes individually.
- [ ] On save, call the backend API to update the settings class; display validation errors inline.
- [ ] Provide feedback on successful save or error.
- [ ] Ensure the UI automatically reflects new settings classes added in the backend, with no frontend code changes required.
- [ ] Add search/filtering for settings if the list grows large.
- [ ] Ensure accessibility and responsive design for all settings pagelets.
- [ ] Add frontend tests for dynamic rendering, editing, and validation of settings pagelets.
- [ ] Document how to add new settings classes (with attribute, interface, and validation).
- [ ] Add example: custom slicer settings with validation.
- [ ] Add developer guide for extending settings.

---

## Phase 6: Advanced Features (Optional)
- [ ] Support settings versioning/migration.
- [ ] Support per-tenant/user overrides.
- [ ] Add UI for dynamic settings management (future).

---

## File Locations
- Attribute, interfaces: `src/Farm.Infrastructure/Settings/`
- Service: `src/Farm.Infrastructure/Settings/SettingsService.cs`
- Settings classes: `src/Farm.Infrastructure/Settings/` (or feature-specific folders)
- Documentation: `docs/SETTINGS_ARCHITECTURE.md`

---


## Acceptance Criteria
- All settings are discoverable and loaded via attribute at runtime.
- Each settings class can provide its own validation logic.
- Adding a new settings class requires no changes to central code or UI code.
- All existing settings and all settings editable in the Admin UI are migrated and validated.
- The Admin UI displays all settings classes as logical, discoverable pagelets, and adding a new settings class in the backend automatically exposes it in the UI.
- All settings are validated and persisted via the new model.
- Documentation and onboarding guides are updated.

---


