import { describe, expect, it } from 'vitest';

import {
  ADMIN_DESTINATION_GROUPS,
  ADMIN_DESTINATIONS,
  filterDestinationsByAccess,
  getDestinationById,
  getDestinationsByGroup,
  getHubGroupedDestinations,
  type AdminDestination,
  type AdminDestinationGroup,
} from '../adminDestinations';

const validGroupIds = new Set<AdminDestinationGroup>(
  ADMIN_DESTINATION_GROUPS.map((group) => group.id),
);

function accessAs(
  role: 'farm_admin' | 'operator' | 'guest',
  permissionOverrides: Record<string, boolean> = {},
) {
  return {
    hasRole: (r: string) => r === role,
    hasPermission: (resource: string, action: string) => {
      const key = `${resource}:${action}`;
      return permissionOverrides[key] ?? true;
    },
  };
}

describe('adminDestinations registry', () => {
  it('has at least one destination', () => {
    expect(ADMIN_DESTINATIONS.length).toBeGreaterThan(0);
  });

  it('assigns every destination a unique id', () => {
    const ids = ADMIN_DESTINATIONS.map((destination) => destination.id);
    const uniqueIds = new Set(ids);
    expect(uniqueIds.size).toBe(ids.length);
  });

  it('assigns every destination a unique path', () => {
    const paths = ADMIN_DESTINATIONS.map((destination) => destination.path);
    const uniquePaths = new Set(paths);
    expect(uniquePaths.size).toBe(paths.length);
  });

  it('uses only groups declared in ADMIN_DESTINATION_GROUPS', () => {
    for (const destination of ADMIN_DESTINATIONS) {
      expect(validGroupIds.has(destination.group)).toBe(true);
    }
  });

  it('renders every path as an absolute route', () => {
    for (const destination of ADMIN_DESTINATIONS) {
      expect(destination.path.startsWith('/')).toBe(true);
    }
  });

  it('provides a non-empty label and description on every destination', () => {
    for (const destination of ADMIN_DESTINATIONS) {
      expect(destination.label.trim().length).toBeGreaterThan(0);
      expect(destination.description.trim().length).toBeGreaterThan(0);
    }
  });

  it('provides a react component icon on every destination', () => {
    for (const destination of ADMIN_DESTINATIONS) {
      expect(typeof destination.icon).toBe('function');
    }
  });

  it('marks the Admin Home entry as the sole overview destination', () => {
    const overviewEntries = ADMIN_DESTINATIONS.filter((destination) => destination.group === 'overview');
    expect(overviewEntries).toHaveLength(1);
    expect(overviewEntries[0].id).toBe('admin-home');
    expect(overviewEntries[0].path).toBe('/admin');
  });

  it('uses the canonical Locations index route', () => {
    expect(getDestinationById('hw-locations')?.path).toBe('/locations');
  });

  it('has at least one hub-tile destination per operational group', () => {
    const requiredHubGroups: AdminDestinationGroup[] = [
      'operations',
      'users',
      'data',
      'hardware',
      'slicing',
      'integrations',
      'general',
      'automation',
      'quotas',
    ];
    for (const group of requiredHubGroups) {
      const tilesInGroup = ADMIN_DESTINATIONS.filter(
        (destination) => destination.group === group && destination.isHubTile,
      );
      expect(tilesInGroup.length, `group ${group} needs at least one hub tile`).toBeGreaterThan(0);
    }
  });

  it('uses only farm_admin (or null) as the role gate', () => {
    for (const destination of ADMIN_DESTINATIONS) {
      const role = destination.requiredRole;
      const allowed = role === undefined || role === null || role === 'farm_admin';
      expect(allowed, `destination ${destination.id} has non-admin role: ${String(role)}`).toBe(true);
    }
  });

  // #1457 — permission-based gating. Every destination whose page is backed by
  // a real controller permission must set `requiredPermission` and clear the
  // role gate (`requiredRole: null`); the server permission is then the sole
  // client-side gate, matching the actual API enforcement. A handful of
  // destinations have no equivalent server permission (or, for `admin-home`,
  // exist only to avoid a self-referential hub tile) and stay on the default
  // `farm_admin` role gate instead — each documented inline in the registry.
  const ROLE_ONLY_EXCEPTIONS = new Set(['admin-home', 'slicing-profiles', 'int-connections']);

  it('sets requiredPermission (and clears requiredRole) on every migrated destination', () => {
    for (const destination of ADMIN_DESTINATIONS) {
      if (destination.requiredPermission) {
        expect(destination.requiredRole, `${destination.id} is permission-backed but still role-gated`).toBeNull();
        expect(destination.requiredPermission.resource.length).toBeGreaterThan(0);
        expect(destination.requiredPermission.action.length).toBeGreaterThan(0);
      }
    }
  });

  it('gates most of the registry on a permission rather than the farm_admin role name', () => {
    const permissionBacked = ADMIN_DESTINATIONS.filter((d) => d.requiredPermission);
    // 27 total destinations: 24 permission-backed, 3 documented role-only
    // exceptions (admin-home, slicing-profiles, int-connections).
    expect(permissionBacked.length).toBe(ADMIN_DESTINATIONS.length - ROLE_ONLY_EXCEPTIONS.size);
  });

  it('the documented role-only exceptions have no requiredPermission and stay on the default role gate', () => {
    for (const id of ROLE_ONLY_EXCEPTIONS) {
      const destination = getDestinationById(id);
      expect(destination, `expected ${id} to exist in the registry`).toBeDefined();
      expect(destination?.requiredPermission).toBeUndefined();
      expect(destination?.requiredRole === undefined || destination?.requiredRole === 'farm_admin').toBe(true);
    }
  });
});

describe('filterDestinationsByAccess', () => {
  it('returns every destination for a farm_admin', () => {
    const result = filterDestinationsByAccess(ADMIN_DESTINATIONS, accessAs('farm_admin'));
    expect(result).toHaveLength(ADMIN_DESTINATIONS.length);
  });

  it('hides admin-gated destinations from a non-admin user', () => {
    const result = filterDestinationsByAccess(ADMIN_DESTINATIONS, accessAs('operator'));
    const remainingAdminGated = result.filter(
      (destination) => destination.requiredRole === undefined || destination.requiredRole === 'farm_admin',
    );
    expect(remainingAdminGated).toHaveLength(0);
  });

  it('honours a destination that opts out with requiredRole: null', () => {
    const openDestination: AdminDestination = {
      id: 'open',
      label: 'Open',
      description: 'Open to everyone',
      path: '/admin/open',
      icon: ADMIN_DESTINATIONS[0].icon,
      group: 'overview',
      requiredRole: null,
    };

    const result = filterDestinationsByAccess([openDestination], accessAs('operator'));
    expect(result).toHaveLength(1);
  });

  it('respects requiredPermission in addition to requiredRole', () => {
    const gated: AdminDestination = {
      ...ADMIN_DESTINATIONS[1],
      id: 'gated',
      requiredPermission: { resource: 'printers', action: 'admin' },
    };

    const noPermission = filterDestinationsByAccess(
      [gated],
      accessAs('farm_admin', { 'printers:admin': false }),
    );
    expect(noPermission).toHaveLength(0);

    const withPermission = filterDestinationsByAccess(
      [gated],
      accessAs('farm_admin', { 'printers:admin': true }),
    );
    expect(withPermission).toHaveLength(1);
  });

  it('#1457 — a custom (non-admin) role granted only the matching permission reaches that destination', () => {
    const destination = getDestinationById('hw-cameras');
    expect(destination?.requiredPermission).toEqual({ resource: 'cameras', action: 'admin' });

    // Non-admin role, and hasPermission is scoped to exactly `cameras:admin`.
    const result = filterDestinationsByAccess(ADMIN_DESTINATIONS, {
      hasRole: () => false,
      hasPermission: (resource, action) => resource === 'cameras' && action === 'admin',
    });

    const ids = result.map((d) => d.id);
    expect(ids).toContain('hw-cameras');
    // A destination gated on a different permission must stay hidden.
    expect(ids).not.toContain('hw-nfc');
  });
});

describe('registry lookup helpers', () => {
  it('getDestinationById returns the matching destination', () => {
    const known = ADMIN_DESTINATIONS[0];
    expect(getDestinationById(known.id)).toEqual(known);
  });

  it('getDestinationById returns undefined for unknown ids', () => {
    expect(getDestinationById('not-a-real-destination')).toBeUndefined();
  });

  it('getDestinationsByGroup returns destinations only for the requested group', () => {
    const operations = getDestinationsByGroup('operations');
    expect(operations.length).toBeGreaterThan(0);
    for (const destination of operations) {
      expect(destination.group).toBe('operations');
    }
  });

  it('getHubGroupedDestinations returns hub tiles grouped in canonical order', () => {
    const grouped = getHubGroupedDestinations(accessAs('farm_admin'));
    expect(grouped.length).toBeGreaterThan(0);

    const groupOrder = grouped.map((entry) => entry.group.id);
    const canonicalOrder = ADMIN_DESTINATION_GROUPS.map((group) => group.id);

    let previousIndex = -1;
    for (const groupId of groupOrder) {
      const index = canonicalOrder.indexOf(groupId);
      expect(index).toBeGreaterThan(previousIndex);
      previousIndex = index;
    }
  });

  it('getHubGroupedDestinations excludes destinations that are not hub tiles', () => {
    const grouped = getHubGroupedDestinations(accessAs('farm_admin'));
    for (const entry of grouped) {
      for (const destination of entry.destinations) {
        expect(destination.isHubTile).toBe(true);
      }
    }
  });

  it('getHubGroupedDestinations returns nothing for a user without admin access', () => {
    // accessAs('operator') defaults hasPermission to true for any resource/action
    // it isn't explicitly told to deny (see the helper above) — a convenience for
    // tests that only care about role gating. That default is unrealistic for
    // this test post-#1457: most destinations are now permission-backed, so an
    // operator who (unrealistically) holds every permission legitimately sees
    // them. Use an access object with no granted permissions at all to assert
    // the real invariant: a user with neither the farm_admin role nor any
    // resource permission sees nothing.
    const noAccess = { hasRole: () => false, hasPermission: () => false };
    const grouped = getHubGroupedDestinations(noAccess);
    expect(grouped).toHaveLength(0);
  });
});
