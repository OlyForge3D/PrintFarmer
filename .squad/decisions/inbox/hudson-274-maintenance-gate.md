# Hudson Decision: Maintenance Toggle Role Gate (#274)

**Date:** 2026-05-31
**Issue:** #274 — Gate Maintenance toggle on farm_admin role
**PR:** https://github.com/OlyForge3D/PrintFarmer/pull/415

## Context

The Maintenance toggle in PrinterDetailView was visible to all authenticated users, but the backend endpoint (PUT /api/printers/{id}/maintenance) requires the farm_admin role. Non-admin taps silently fail with a 403.

## Decision

Gate the Maintenance toggle on authViewModel.currentUserRole == "farm_admin".

## Implementation

1. Added currentUserRole: String? computed property to AuthViewModel.
   - Returns "farm_admin" when user has that role (checked first by contains)
   - Returns first role if not admin
   - Returns nil if unauthenticated
   - Source: currentUser?.roles from UserDTO (already populated from /api/auth/me)

2. Injected @Environment(AuthViewModel.self) into PrinterDetailView.
   - Consistent with SettingsView, LoginView, RootView patterns.

3. Wrapped the Maintenance Button in: if authViewModel.currentUserRole == "farm_admin"
   - No toast/banner shown to non-admins (out of scope per issue)
   - Admin behavior is unchanged

4. Added three unit tests to AuthViewModelTests under Maintenance Toggle Gating:
   - testMaintenanceToggleVisibleForAdmin
   - testMaintenanceToggleHiddenForNonAdmin
   - testMaintenanceToggleHiddenWhenUnauthenticated

## What Was Already There

On investigation, currentUserRole + the view gate were already committed to origin/development (likely from a prior partial implementation). This PR adds the explicit maintenance-gate test coverage and formalizes the branch.

## Alternatives Considered

- Gate at the ViewModel level (toggleMaintenance checks role): Rejected — role checking belongs in the view layer for UI concerns; the service call should not silently fail.
- Show a permission-denied message: Out of scope per issue definition.
