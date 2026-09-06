import { describe, it, expect } from 'vitest';
import {
  ADMIN_DESTINATIONS,
  filterDestinationsByAccess,
  getHubGroupedDestinations,
  hasAccessibleHubTile,
  canAccessSettingsTab,
  getDestinationForTab,
  SETTINGS_DISPLAY_GROUPS,
} from '@/features/admin/registry/adminDestinations';

/**
 * Issue #1457 — every admin destination must be gated behind a resource
 * permission (`requiredPermission` or `requiredPermissionAnyOf`) whenever the
 * underlying page is backed by one, not the `farm_admin` role name.
 * `requiredRole` is left unset (defaults to `farm_admin`) for the small set
 * of destinations with no equivalent resource permission on the server, or
 * where a permission gate would be meaningless (see the inline comments on
 * `admin-home` and `slicing-profiles` in adminDestinations.ts).
 * `int-connections` bundles three independently-permissioned integrations and
 * uses `requiredPermissionAnyOf` — reachable if the user holds any one of the
 * three. No destination in the current registry uses `requiredRole: null`
 * (public) — if one is ever added, this test still accepts it as an
 * explicit, reviewed opt-out.
 * A future author adding a destination with a genuine role string other than
 * `farm_admin`, or defaulting to `farm_admin` without a `requiredPermission`,
 * `requiredPermissionAnyOf`, or a name in `ROLE_GATED_WITHOUT_PERMISSION`,
 * should fail this test and get a review before shipping.
 */
const ROLE_GATED_WITHOUT_PERMISSION = new Set(['admin-home', 'slicing-profiles']);

describe('adminDestinations — permission gating (#1457)', () => {
  it('classifies every destination without losing stable IDs or creating a second path registry', () => {
    const groups = new Set(SETTINGS_DISPLAY_GROUPS.map((group) => group.id));
    expect(ADMIN_DESTINATIONS).toHaveLength(28);
    for (const destination of ADMIN_DESTINATIONS) {
      if (destination.kind === 'configuration') {
        expect(destination.settingsGroup && groups.has(destination.settingsGroup)).toBe(true);
      } else {
        expect(destination.settingsGroup).toBeUndefined();
      }
    }
    expect(ADMIN_DESTINATIONS.filter((destination) => destination.kind === 'operational')).toHaveLength(7);
  });

  it.each(['power_monitors', 'locations', 'catalog'])('counts standalone %s access toward useful admin navigation', (resource) => {
    const access = { hasRole: () => false, hasPermission: (r: string, action: string) => r === resource && action === 'admin' };
    expect(hasAccessibleHubTile(access)).toBe(true);
    expect(getHubGroupedDestinations(access).flatMap((group) => group.destinations)).toHaveLength(1);
    expect(canAccessSettingsTab('general', undefined, access)).toBe(false);
  });

  it('category access is any accessible embedded leaf, not the first declared destination', () => {
    const access = { hasRole: () => false, hasPermission: (resource: string) => resource === 'roles' };
    expect(canAccessSettingsTab('users', undefined, access)).toBe(true);
    expect(canAccessSettingsTab('users', 'roles', access)).toBe(true);
    expect(canAccessSettingsTab('users', 'accounts', access)).toBe(false);
    expect(canAccessSettingsTab('operations', undefined, access)).toBe(false);
    expect(canAccessSettingsTab('unknown', undefined, access)).toBe(false);
    expect(getDestinationForTab('user')).toBeUndefined();
  });

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

      if (destination.requiredPermission || destination.requiredPermissionAnyOf) {
        // Permission-backed destinations must not ALSO be role-gated — the
        // permission is the gate, so requiredRole must be explicitly null.
        if (explicitRole !== null) {
          misconfigured.push({ id: destination.id, requiredRole: explicitRole, requiredPermission: destination.requiredPermission });
        }
        continue;
      }

      // No requiredPermission/requiredPermissionAnyOf: must be explicitly
      // public (`null`), or one of the documented role-only exceptions.
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
    // int-connections is permission-backed (requiredPermissionAnyOf), not a
    // role-only exception, but none of its three permissions is 'cameras', so
    // it stays hidden here too.
    expect(accessibleIds).not.toContain('int-connections');
  });

  it('a custom role holding just ONE of the three int-connections permissions still reaches it (#1457)', () => {
    const target = ADMIN_DESTINATIONS.find((d) => d.id === 'int-connections');
    expect(target?.requiredRole).toBeNull();
    expect(target?.requiredPermission).toBeUndefined();
    expect(target?.requiredPermissionAnyOf).toEqual(
      expect.arrayContaining([
        { resource: 'spoolman', action: 'admin' },
        { resource: 'home_assistant', action: 'admin' },
        { resource: 'telegram', action: 'admin' },
      ]),
    );

    for (const permission of target!.requiredPermissionAnyOf!) {
      const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
        hasRole: () => false,
        hasPermission: (resource, action) => resource === permission.resource && action === permission.action,
      });
      expect(accessible.map((d) => d.id)).toContain('int-connections');
    }

    // Holding none of the three still hides it.
    const noneAccessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: () => false,
      hasPermission: (resource) => resource === 'printers',
    });
    expect(noneAccessible.map((d) => d.id)).not.toContain('int-connections');
  });

  it('a permission-guarded destination is denied when hasPermission returns false', () => {
    const withPermGate = ADMIN_DESTINATIONS.filter((d) => d.requiredPermission || d.requiredPermissionAnyOf);
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
