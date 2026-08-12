import { describe, it, expect } from 'vitest';
import {
  ADMIN_DESTINATIONS,
  filterDestinationsByAccess,
  getHubGroupedDestinations,
} from '@/features/admin/registry/adminDestinations';

/**
 * Issue #1457 — every admin destination must be gated behind a resource
 * permission (`requiredPermission`) whenever the underlying page is backed by
 * one, not the `farm_admin` role name. `requiredRole` is left unset (defaults
 * to `farm_admin`) for the small set of destinations with no equivalent
 * resource permission on the server, or where a permission gate would be
 * meaningless (see the inline comments on `admin-home`, `slicing-profiles`,
 * and `int-connections` in adminDestinations.ts). No destination in the
 * current registry uses `requiredRole: null` (public) — if one is ever added,
 * this test still accepts it as an explicit, reviewed opt-out.
 * A future author adding a destination with a genuine role string other than
 * `farm_admin`, or defaulting to `farm_admin` without either a
 * `requiredPermission` or a name in `ROLE_GATED_WITHOUT_PERMISSION`, should
 * fail this test and get a review before shipping.
 */
const ROLE_GATED_WITHOUT_PERMISSION = new Set(['admin-home', 'slicing-profiles', 'int-connections']);

describe('adminDestinations — permission gating (#1457)', () => {
  it('every destination is either permission-backed, explicitly public, or a documented role-only exception', () => {
    const misconfigured: { id: string; requiredRole: unknown; requiredPermission: unknown }[] = [];
    for (const destination of ADMIN_DESTINATIONS) {
      const explicitRole = destination.requiredRole;
      // `requiredRole` must be `undefined` (defaults to `farm_admin`), `null`
      // (public), or exactly `'farm_admin'`. A different literal role string
      // needs a review before shipping.
      const roleOk = explicitRole === undefined || explicitRole === null || explicitRole === 'farm_admin';
      if (!roleOk) {
        misconfigured.push({ id: destination.id, requiredRole: explicitRole, requiredPermission: destination.requiredPermission });
        continue;
      }

      if (destination.requiredPermission) {
        // Permission-backed destinations must not ALSO be role-gated — the
        // permission is the gate, so requiredRole must be explicitly null.
        if (explicitRole !== null) {
          misconfigured.push({ id: destination.id, requiredRole: explicitRole, requiredPermission: destination.requiredPermission });
        }
        continue;
      }

      // No requiredPermission: must be explicitly public (`null`), or one of
      // the documented role-only exceptions.
      if (explicitRole === null) {
        continue;
      }
      if (!ROLE_GATED_WITHOUT_PERMISSION.has(destination.id)) {
        misconfigured.push({ id: destination.id, requiredRole: explicitRole, requiredPermission: destination.requiredPermission });
      }
    }
    expect(misconfigured).toEqual([]);
  });

  it('every documented role-only exception actually exists in the registry', () => {
    const ids = new Set(ADMIN_DESTINATIONS.map((d) => d.id));
    for (const exceptionId of ROLE_GATED_WITHOUT_PERMISSION) {
      expect(ids.has(exceptionId)).toBe(true);
    }
  });

  it('a non-admin, non-permissioned user sees zero admin destinations', () => {
    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: (role) => role === 'operator',
      hasPermission: () => false,
    });
    expect(accessible).toEqual([]);
  });

  it('a farm_admin sees every registered destination', () => {
    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: (role) => role === 'farm_admin',
      hasPermission: () => true,
    });
    // Sanity: registry is non-empty.
    expect(accessible.length).toBeGreaterThan(0);
    // farm_admin passes both hasRole and hasPermission unconditionally, so
    // every destination in the registry survives the gate regardless of
    // whether it's permission-backed or one of the role-only exceptions.
    expect(accessible.length).toBe(ADMIN_DESTINATIONS.length);
  });

  it('a custom role with only the matching resource permission sees that destination but not others', () => {
    const target = ADMIN_DESTINATIONS.find((d) => d.id === 'hw-cameras');
    expect(target?.requiredPermission).toEqual({ resource: 'cameras', action: 'admin' });

    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: () => false,
      hasPermission: (resource, action) => resource === 'cameras' && action === 'admin',
    });

    const accessibleIds = accessible.map((d) => d.id);
    expect(accessibleIds).toContain('hw-cameras');
    // A destination backed by a different resource must stay hidden.
    expect(accessibleIds).not.toContain('hw-nfc');
    // The role-only exceptions must also stay hidden — no permission grants
    // any of them.
    expect(accessibleIds).not.toContain('admin-home');
    expect(accessibleIds).not.toContain('slicing-profiles');
    expect(accessibleIds).not.toContain('int-connections');
  });

  it('a permission-guarded destination is denied when hasPermission returns false', () => {
    const withPermGate = ADMIN_DESTINATIONS.filter((d) => d.requiredPermission);
    expect(withPermGate.length).toBeGreaterThan(0);
    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: () => true,
      hasPermission: () => false,
    });
    // None of the permission-gated destinations survive.
    for (const gated of withPermGate) {
      expect(accessible.map((d) => d.id)).not.toContain(gated.id);
    }
  });

  it('getHubGroupedDestinations returns [] for a caller with no admin role or permission', () => {
    const grouped = getHubGroupedDestinations({
      hasRole: () => false,
      hasPermission: () => false,
    });
    expect(grouped).toEqual([]);
  });

  it('every destination path starts with `/` and never contains a bare hostname', () => {
    // A relative path is the only shape SettingsShell + navigate() cope with;
    // an absolute URL would blow up ProtectedRoute redirects. This catches
    // an accidental `http://…` slipping into the registry.
    for (const destination of ADMIN_DESTINATIONS) {
      expect(destination.path).toMatch(/^\//);
      expect(destination.path).not.toMatch(/^https?:/);
    }
  });

  it('destination ids are unique — no shadow ambiguity for the palette', () => {
    const ids = ADMIN_DESTINATIONS.map((d) => d.id);
    const uniqueIds = new Set(ids);
    expect(uniqueIds.size).toBe(ids.length);
  });
});
