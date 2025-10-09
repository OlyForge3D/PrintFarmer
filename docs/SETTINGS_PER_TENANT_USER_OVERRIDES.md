# Per-Tenant and Per-User Settings Overrides: Design Plan

## Overview
This document outlines a strategy for supporting per-tenant and per-user overrides in PrintFarmer's modular settings system. The goal is to allow global (system-wide), tenant-specific, and user-specific settings, with a clear precedence and seamless integration into the existing architecture and UI.

---

## 1. Requirements
- **Global Defaults:** All settings classes have a global value (current behavior).
- **Tenant Overrides:** For multi-tenant deployments, allow tenants to override global settings.
- **User Overrides:** Allow individual users to override tenant/global settings for their own experience (where applicable).
- **Precedence:** User > Tenant > Global.
- **Dynamic UI:** The Admin UI and (optionally) user UI must clearly indicate which value is in effect and allow editing at the appropriate scope.
- **API:** All settings endpoints must support scoping (global/tenant/user) and return effective values with source info.

---

## 2. Backend Design

### Data Model
- Extend settings storage to include `ScopeType` (Global, Tenant, User) and `ScopeId` (null, tenantId, userId).
- Each settings class can have multiple records: one global, zero or more tenant, zero or more user.
- On read, resolve effective value by precedence: user > tenant > global.

Example table:
| Id | Key | ScopeType | ScopeId | ValueJson |
|----|-----|-----------|---------|-----------|
| 1  | Slicer.MySlicer | Global | null    | {...} |
| 2  | Slicer.MySlicer | Tenant | tenantA | {...} |
| 3  | Slicer.MySlicer | User   | userX   | {...} |

### API Changes
- All settings endpoints accept optional `scopeType` and `scopeId` parameters.
- `GET /api/settings/{key}` returns the effective value and its source (user/tenant/global).
- `PUT /api/settings/{key}` allows updating at a specific scope.
- `GET /api/settings/metadata` includes info about which settings are overridable and at what scopes.

### Service Logic
- On read: check for user override, then tenant, then global.
- On write: update or insert at the specified scope.
- On delete: remove override at the specified scope (fall back to lower scope).
- Validation applies at all scopes.

---

## 3. UI/UX Design

### Admin UI
- Show current value, effective value, and source (user/tenant/global) for each setting.
- Allow editing at global or tenant scope (if multi-tenant is enabled).
- Indicate when a value is overridden at a lower scope.
- Optionally, allow searching/filtering by scope.

### User UI (optional)
- For user-overridable settings, allow users to view and override their own settings.
- Show which values are inherited from tenant/global.

### Example UI Table
| Setting | Effective Value | Source | Edit Global | Edit Tenant | Edit User |
|---------|----------------|--------|-------------|-------------|-----------|
| MaxItems | 10             | User   | [edit]      | [edit]      | [edit]    |
| Theme    | "Dark"         | Tenant | [edit]      | [edit]      |           |

---

## 4. Security & Validation
- Only admins can edit global/tenant settings.
- Users can only edit their own user-scoped settings.
- All validation rules apply at every scope.
- Audit log all changes with scope and user info.

---

## 5. Migration & Compatibility
- Existing settings are treated as global by default.
- Migration script can initialize global records for all settings.
- No breaking changes to existing settings classes or UI.

---

## 6. Extensibility
- Scopes can be extended (e.g., group, device) by adding new `ScopeType` values.
- UI and API can be extended to support new scopes with minimal changes.

---

## 7. References
- See `SETTINGS_ARCHITECTURE.md` for core model.
- See `EXTENDING_DYNAMIC_SETTINGS_UI.md` for UI extensibility.

---

This plan provides a foundation for robust, multi-scope settings management in PrintFarmer. Next: plan advanced dynamic settings management UI.
