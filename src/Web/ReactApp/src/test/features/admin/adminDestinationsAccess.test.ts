import { describe, it, expect } from 'vitest';
import {
  ADMIN_DESTINATIONS,
  filterDestinationsByAccess,
  getHubGroupedDestinations,
} from '@/features/admin/registry/adminDestinations';

/**
 * Epic #939 — every admin destination must be gated behind `farm_admin`
 * unless explicitly opted out via `requiredRole: null`. A silent omission
 * (say, someone forgets to set `requiredRole` on a new destination and it
 * defaults to `farm_admin` — good) is safe; the danger is a future author
 * writing `requiredRole: 'operator'` and inadvertently exposing an admin
 * surface to a role that never had it. The registry is small enough to make
 * that invariant explicit and enforceable.
 */
describe('adminDestinations — role gating (#939)', () => {
  it('every destination is either admin-only or explicitly `requiredRole: null`', () => {
    const misconfigured: { id: string; requiredRole: unknown }[] = [];
    for (const destination of ADMIN_DESTINATIONS) {
      // `undefined` defaults to `farm_admin` (safe). Any explicit value must
      // be either `null` (public) or exactly `'farm_admin'`. If a new role
      // string appears here, it needs a review before shipping.
      const explicit = destination.requiredRole;
      const ok = explicit === undefined || explicit === null || explicit === 'farm_admin';
      if (!ok) {
        misconfigured.push({ id: destination.id, requiredRole: explicit });
      }
    }
    expect(misconfigured).toEqual([]);
  });

  it('a non-admin (operator) sees zero admin destinations', () => {
    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: (role) => role === 'operator',
      hasPermission: () => false,
    });
    expect(accessible).toEqual([]);
  });

  it('a farm_admin sees every registered destination that has no extra permission gate', () => {
    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: (role) => role === 'farm_admin',
      hasPermission: () => true,
    });
    // Sanity: registry is non-empty.
    expect(accessible.length).toBeGreaterThan(0);
    // With hasRole('farm_admin') → true and hasPermission → true, every
    // destination in the registry survives the gate — because everything is
    // either public (requiredRole: null) or farm_admin-gated (default), and
    // no destination is blocked from farm_admin.
    expect(accessible.length).toBe(ADMIN_DESTINATIONS.length);
  });

  it('a permission-guarded destination is denied when hasPermission returns false', () => {
    const withPermGate = ADMIN_DESTINATIONS.filter((d) => d.requiredPermission);
    if (withPermGate.length === 0) {
      // Nothing to assert against — the invariant is vacuous. Skip cleanly
      // rather than failing spuriously if the registry has none right now.
      return;
    }
    const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: () => true,
      hasPermission: () => false,
    });
    // None of the permission-gated destinations survive.
    for (const gated of withPermGate) {
      expect(accessible.map((d) => d.id)).not.toContain(gated.id);
    }
  });

  it('getHubGroupedDestinations returns [] for a caller with no admin role', () => {
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
