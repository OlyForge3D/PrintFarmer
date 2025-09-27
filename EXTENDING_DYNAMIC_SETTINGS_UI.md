# Dynamic Settings UI Extensibility Guide

This document explains how to extend PrintFarmer's dynamic settings system, including adding new settings classes, understanding backend-to-frontend metadata flow, and customizing the dynamic React UI.

---

## 1. Overview

PrintFarmer uses a modular, attribute-driven settings architecture. All persisted settings are defined as C# classes with metadata attributes. The backend exposes settings metadata and values via a unified API, and the React frontend renders dynamic forms using this metadata.

- **Backend:** Modular settings classes (C#), reflection-based SettingsService, metadata endpoint (`/api/settings/metadata`)
- **Frontend:** Dynamic React UI (`SettingsPagelet`), renders forms for any settings class using backend metadata

---

## 2. Adding a New Settings Class (Backend)

1. **Create a C# class** in `Farm.Infrastructure/Settings/` implementing `IAppSetting` (and optionally `IValidatableSetting`).
2. **Annotate properties** with `[AppSetting]` and validation attributes (e.g., `[Required]`, `[Range]`).
3. **Register the class** in the DI container if not using automatic discovery.
4. **SettingsService** will automatically pick up the new class and expose its metadata and values.

Example:
```csharp
public class MyFeatureSettings : IAppSetting, IValidatableSetting {
    [AppSetting(DisplayName = "Enable Feature", Description = "Toggle the feature.")]
    public bool EnableFeature { get; set; }

    [AppSetting(DisplayName = "Max Items", Description = "Maximum number of items.")]
    [Range(1, 100)]
    public int MaxItems { get; set; }
}
```

---

## 3. Backend Metadata Flow

- The backend exposes all settings classes and their property metadata via `GET /api/settings/metadata`.
- Each class and property includes:
  - `className`, `displayName`, `description`
  - For each property: `name`, `type`, `displayName`, `description`, validation info, enum values, etc.
- The endpoint returns both metadata and current values for all settings.

---

## 4. Frontend: Dynamic Settings UI

- The React component `SettingsPagelet` (in `src/components/SettingsPagelet.tsx`) renders a form for any settings class using the metadata.
- The main settings page (`SettingsPage.tsx`) fetches metadata and values, then renders a `SettingsPagelet` for each settings class.
- All form fields, labels, and validation are driven by backend metadata (no hardcoded forms).

### How to Extend the UI
- **No code changes needed** for most new settings classes—just add the class and annotate properties.
- To customize rendering for a specific property type, extend `SettingsPagelet` (e.g., add support for enums, arrays, or custom widgets).
- To add global UI features (e.g., grouping, search), update `SettingsPage.tsx`.

---

## 5. Validation & Saving

- Validation rules (e.g., required, range) are enforced both in the backend and (optionally) in the frontend.
- When a user edits and saves settings, the frontend sends the updated values to the backend, which validates and persists them.
- Errors are surfaced in the UI via the `error` prop on `SettingsPagelet`.

---

## 6. Best Practices

- Always use `[AppSetting(DisplayName = ...)]` for user-friendly labels.
- Use validation attributes to enforce constraints.
- Keep settings classes focused and modular.
- Document new settings classes and their purpose.

---

## 7. Example: Adding a New Setting

1. Add a new class `MyFeatureSettings` as shown above.
2. Rebuild and restart the backend.
3. The new settings will automatically appear in the React Admin UI under Settings.
4. No frontend code changes required unless you want custom UI for new property types.

---

## 8. Troubleshooting

- If a new settings class does not appear, ensure it is discoverable by `SettingsService` and properly annotated.
- Check the `/api/settings/metadata` endpoint for correct metadata.
- Use browser dev tools to inspect the API response and React component props.

---

## 9. References
- Backend: `Farm.Infrastructure/Settings/SettingsService.cs`, `UnifiedSettingsController.cs`
- Frontend: `src/components/SettingsPagelet.tsx`, `src/pages/SettingsPage.tsx`
- API: `GET /api/settings/metadata`, `POST /api/settings/{className}`

---

For further help, see the main README or open an issue on GitHub.
