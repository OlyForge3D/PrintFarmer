

# Modular Extensible Settings System Implementation Plan

## ✅ Status Summary (as of 2025-09-26)

**All phases of the modular, extensible settings system are now complete.**

- All backend settings are modular, discoverable, validated, and loaded via attribute-driven reflection.
- The SettingsService is the single source of truth; all legacy settings code is removed.
- The backend exposes settings metadata and values via `/api/settings/metadata` for dynamic UI.
- The React Admin UI now dynamically renders all settings classes as pagelets using backend metadata (no hardcoded forms).
- Adding a new settings class in the backend automatically exposes it in the UI with no frontend code changes required.
- All frontend and backend tests for settings are passing.
- Documentation for extensibility is available in `EXTENDING_DYNAMIC_SETTINGS_UI.md`.

---

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
# Implementation Status (as of 2025-09-26)

**Backend:**
- All settings classes are now modular, discoverable, and decorated with `[AppSetting]`.
- The `IAppSetting` interface now requires a static `SectionKey` property (no longer empty).
- All legacy settings services and registrations (e.g., `AppSettingsService`, `IAppSettingsService`, `IOptions<T>`) have been removed from the backend.
- The `SettingsService` is the single source of truth for all settings, using reflection and attributes for discovery and loading.
- All settings classes implement validation via `IValidatableSetting` where appropriate.
- Duplicate/ambiguous types (e.g., `TempTargets`, `PerEngineSlicerSetting`) have been resolved; only one canonical definition is used throughout the codebase.
- All usages in controllers and services have been migrated to use the new `SettingsService` model.
- Lint/style issues (formatting, nullability, collection types) have been resolved in all settings-related files.
{
**Frontend/UI:**
- The backend is ready for dynamic UI discovery: all settings classes are exposed and can be listed/queried via the service.
- The Admin UI migration to dynamic, pagelet-based settings is planned/underway (see Phase 5).
    public string Key { get; }
**Documentation:**
- This document and all onboarding guides are up to date with the new architecture and usage patterns.
    public AppSettingAttribute(string key) => Key = key;
**Outstanding/Next Steps:**
- Complete dynamic UI migration (Phase 5) and add frontend tests for settings pagelets.
- Continue to add new settings classes as needed using the documented pattern.
- Optional: implement advanced features (versioning, per-tenant overrides, etc.).
}
---

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
- [x] Define `[AppSetting]` attribute for marking settings classes.
- [x] Define `IAppSetting` marker interface (for type safety).
- [x] Define `IValidatableSetting` interface with `Validate()`/`ValidateAsync()`.
- [x] Refactor existing settings classes to use attribute and interfaces.

---

## Phase 2: Settings Service & Discovery
- [x] Implement `SettingsService`:
    - [x] Use reflection to scan assemblies for `[AppSetting]` classes.
    - [x] Register discovered settings using the attribute's key.
    - [x] Load settings from configuration (JSON, env vars, etc.) by key.
    - [x] Expose `Get<T>()`, `GetByKey(string)`, and `GetAll()` APIs.
- [x] Support reload/refresh of settings at runtime (optional).

---

## Phase 3: Validation Pipeline
- [x] On load, call `Validate()`/`ValidateAsync()` for all settings implementing `IValidatableSetting`.
- [x] Aggregate and surface validation errors at startup (fail fast or log as warnings).
- [x] Add tests for validation logic and error handling.

---


## Phase 4: Migration, Integration & UI Alignment
- [x] Audit all settings currently editable in the Admin UI (review existing settings pages/components and API endpoints).
- [x] For each setting, ensure it is represented as a settings class using `[AppSetting]` and interfaces.
- [x] For any setting not yet represented as a settings class, create a new class in `src/Farm.Infrastructure/Settings/`:
    - [x] Decorate with `[AppSetting("Key")]`.
    - [x] Implement `IAppSetting` and, if needed, `IValidatableSetting`.
    - [x] Add properties for each setting field.
    - [x] Add validation logic as appropriate.
- [x] Migrate configuration binding and persistence to use the new settings classes.
- [x] Update the settings service to ensure all new classes are discoverable and loaded.
- [x] Update all usages to use `SettingsService` instead of direct property access.
- [x] Update configuration binding logic to support new model.
- [x] Update startup and validation logic to use new service.
- [x] Expose a generic API endpoint (e.g., `/api/settings`) to:
    - [x] List all available settings classes (with metadata: key, display name, description).
    - [x] Get and update values for each settings class by key.
- [x] Ensure validation is enforced on update.
- [x] Add OpenAPI documentation for the new endpoints.

---



## Phase 5: Extensibility, Dynamic UI & Documentation
- [x] Refactor the Admin UI to fetch the list of all available settings classes from the backend (via the new API endpoint).
- [x] For each settings class, dynamically generate a UI section ("settings pagelet") based on its metadata and properties.
- [x] Group settings pagelets logically (e.g., by feature, integration, or category) using metadata from the backend or a local mapping.
- [x] Each pagelet should display all editable fields for its settings class, with appropriate input types and validation messages.
- [x] Provide clear display names, descriptions, and help text for each field (using metadata or annotations).
- [x] Allow users to edit settings in each pagelet and submit changes individually.
- [x] On save, call the backend API to update the settings class; display validation errors inline.
- [x] Provide feedback on successful save or error.
- [x] Ensure the UI automatically reflects new settings classes added in the backend, with no frontend code changes required.
- [x] Add search/filtering for settings if the list grows large.
- [x] Ensure accessibility and responsive design for all settings pagelets.
- [x] Add frontend tests for dynamic rendering, editing, and validation of settings pagelets.
- [x] Document how to add new settings classes (with attribute, interface, and validation).
- [x] Add example: custom slicer settings with validation.
- [x] Add developer guide for extending settings (see `EXTENDING_DYNAMIC_SETTINGS_UI.md`).

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


