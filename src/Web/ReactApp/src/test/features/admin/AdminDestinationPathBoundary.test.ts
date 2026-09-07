import { describe, it, expect } from 'vitest';
import {
  ADMIN_DESTINATIONS,
  getStandaloneConfigurationDestinations,
  hasAccessibleDestinationWithPrefix,
  isPathWithin,
} from '@/features/admin/registry';

/**
 * Path-prefix matching on the admin registry must respect route boundaries.
 *
 * A bare `startsWith` treats `/admin/settings-foo` as living inside the
 * `/admin/settings` shell. That is wrong in both directions at once: the
 * sibling route is dropped from the dashboard's standalone configuration cards
 * *and* it lights up the generic "Farm & Admin Settings" card that does not
 * actually lead there — a delegate whose only grant is that route gets a card
 * pointing somewhere else (#2508, Hicks review).
 *
 * The registry holds no such sibling today, so this asserts the predicate
 * directly rather than waiting for one to be added.
 */
describe('admin destination path boundaries', () => {
  const allAccess = { hasRole: () => true, hasPermission: () => true };

  describe('isPathWithin', () => {
    it('matches the base path exactly', () => {
      expect(isPathWithin('/admin/settings', '/admin/settings')).toBe(true);
    });

    it('matches descendants across every route boundary character', () => {
      expect(isPathWithin('/admin/settings/users', '/admin/settings')).toBe(true);
      expect(isPathWithin('/admin/settings?tab=users&sub=accounts', '/admin/settings')).toBe(true);
      expect(isPathWithin('/admin/settings#section', '/admin/settings')).toBe(true);
    });

    it('does not match a sibling route that merely shares the prefix', () => {
      expect(isPathWithin('/admin/settings-foo', '/admin/settings')).toBe(false);
      expect(isPathWithin('/admin/settingsfoo', '/admin/settings')).toBe(false);
      expect(isPathWithin('/administration', '/admin')).toBe(false);
    });

    it('does not match an unrelated path', () => {
      expect(isPathWithin('/catalog', '/admin')).toBe(false);
    });
  });

  describe('registry consumers', () => {
    it('still resolves the real settings shell through the boundary-aware check', () => {
      expect(hasAccessibleDestinationWithPrefix(allAccess, '/admin/settings')).toBe(true);
      expect(hasAccessibleDestinationWithPrefix(allAccess, '/admin')).toBe(true);
    });

    it('partitions configuration destinations into exactly settings-shell and standalone', () => {
      const configuration = ADMIN_DESTINATIONS.filter((d) => d.kind === 'configuration');
      const standalone = getStandaloneConfigurationDestinations(allAccess);
      const inShell = configuration.filter((d) => isPathWithin(d.path, '/admin/settings'));

      // Every configuration destination is represented exactly once on the
      // dashboard — either behind the settings card or as its own card. A gap
      // here is the dead-end bug this fix exists to close.
      expect(standalone.length + inShell.length).toBe(configuration.length);
      expect(standalone.some((d) => isPathWithin(d.path, '/admin/settings'))).toBe(false);
    });

    it('keeps /admin/power-monitors out of the settings shell', () => {
      const standalone = getStandaloneConfigurationDestinations(allAccess);
      expect(standalone.map((d) => d.path)).toContain('/admin/power-monitors');
    });
  });
});
