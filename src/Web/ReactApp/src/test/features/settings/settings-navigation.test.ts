import { describe, expect, it } from 'vitest';
import {
  buildAdminDestinationCommandItems,
  buildSettingCommandItems,
  buildSettingsPath,
  resolveSettingsNavigationTarget,
  SETTINGS_GROUP_TO_LOCATION,
} from '@/features/settings/settings-navigation';
import { SUB_PAGE_ALLOWED_GROUPS } from '@/features/settings/subpage-groups';
import type { AdminDestination } from '@/features/admin/registry/adminDestinations';
import type { SettingMetadata } from '@/common/components/SettingsPagelet';
import type { SettingGroupMetadata } from '@/services/settingsApi';

describe('resolveSettingsNavigationTarget', () => {
  it('falls back to User Settings when scope is invalid', () => {
    expect(resolveSettingsNavigationTarget(undefined, undefined, 'not-a-real-scope')).toEqual({
      scopeId: 'user',
      categoryId: 'profile',
      subPageId: 'preferences',
    });
  });

  it('defaults missing and empty scopes to User Settings', () => {
    expect(resolveSettingsNavigationTarget(undefined, undefined, null)).toEqual({
      scopeId: 'user',
      categoryId: 'profile',
      subPageId: 'preferences',
    });

    expect(resolveSettingsNavigationTarget(undefined, undefined, '')).toEqual({
      scopeId: 'user',
      categoryId: 'profile',
      subPageId: 'preferences',
    });
  });

  it('falls back to the first available category when tab is invalid', () => {
    expect(resolveSettingsNavigationTarget('not-real', undefined, 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'general',
      subPageId: 'farm',
    });
  });

  it('keeps deep links with both tab and sub params intact', () => {
    expect(resolveSettingsNavigationTarget('hardware', 'printer-groups', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'hardware',
      subPageId: 'printer-groups',
    });
  });

  it('falls back within the route scope when the tab is not canonical', () => {
    expect(resolveSettingsNavigationTarget('operations', 'workers', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'general',
      subPageId: 'farm',
    });
  });
});

describe('buildSettingsPath', () => {
  it('routes accounts to the combined configuration workspace', () => {
    expect(
      buildSettingsPath({ scopeId: 'system', categoryId: 'users', subPageId: 'accounts' }),
    ).toBe('/admin/settings?scope=system&tab=users&sub=accounts');
  });

  it('routes system scope to /admin/settings', () => {
    expect(
      buildSettingsPath({ scopeId: 'system', categoryId: 'general', subPageId: 'farm' }),
    ).toBe('/admin/settings?scope=system&tab=general&sub=farm');
  });

  it('routes user scope to /settings and omits sub when not provided', () => {
    expect(buildSettingsPath({ scopeId: 'user', categoryId: 'profile' })).toBe(
      '/settings?scope=user&tab=profile',
    );
  });

  it('appends the field param when the palette is deep-linking a specific setting', () => {
    expect(
      buildSettingsPath({
        scopeId: 'system',
        categoryId: 'general',
        subPageId: 'farm',
        field: 'FarmName',
      }),
    ).toBe('/admin/settings?scope=system&tab=general&sub=farm&field=FarmName');
  });
});

describe('buildAdminDestinationCommandItems', () => {
  const StubIcon = () => null;
  const destination: AdminDestination = {
    kind: 'operational',
    id: 'users.audit',
    label: 'Login Audit',
    description: 'Recent sign-in attempts.',
    group: 'users',
    icon: StubIcon,
    path: '/admin/login-audit',
    keywords: ['audit', 'login'],
  };

  it('emits one destination item per registry entry with kind and href set', () => {
    const [item] = buildAdminDestinationCommandItems([destination]);
    expect(item.id).toBe('dest.users.audit');
    expect(item.kind).toBe('destination');
    expect(item.href).toBe('/admin/login-audit');
    expect(item.icon).toBe(StubIcon);
    expect(item.label).toBe('Login Audit');
    expect(item.breadcrumb).toContain('Admin');
    expect(item.keywords).toEqual(expect.arrayContaining(['audit', 'login', 'admin']));
  });

  it('returns an empty array when no destinations are supplied', () => {
    expect(buildAdminDestinationCommandItems([])).toEqual([]);
  });
});

describe('buildSettingCommandItems', () => {
  const metadata: SettingMetadata[] = [
    {
      key: 'FarmSettings',
      className: 'FarmSettings',
      displayName: 'Farm Settings',
      group: 'General',
      properties: [
        {
          name: 'FarmName',
          type: 'string',
          attributes: [],
          display: { name: 'Farm Name', description: 'Human-readable farm name.' },
        },
        {
          name: 'MotdMessage',
          type: 'string',
          attributes: [],
        },
      ],
    },
    {
      key: 'UnknownSection',
      className: 'UnknownSection',
      group: 'DoesNotExistInMap',
      properties: [{ name: 'Foo', type: 'string', attributes: [] }],
    },
  ];

  const groups: SettingGroupMetadata[] = [
    { key: 'General', displayName: 'General' },
  ];

  it('emits one item per property with a field-scoped href', () => {
    const items = buildSettingCommandItems(metadata, groups);
    const farmName = items.find((item) => item.id === 'setting.FarmSettings.FarmName');
    expect(farmName).toBeDefined();
    expect(farmName?.kind).toBe('setting');
    expect(farmName?.label).toBe('Farm Name');
    // Qualified with the section key — property names are not unique across
    // sections, and several sections render on the same page.
    expect(farmName?.href).toContain('field=FarmSettings.FarmName');
    expect(farmName?.href).toContain('scope=system');
    expect(farmName?.href).toContain('tab=general');
  });

  it('falls back to the property name when no display metadata is present', () => {
    const items = buildSettingCommandItems(metadata, groups);
    const motd = items.find((item) => item.id === 'setting.FarmSettings.MotdMessage');
    expect(motd?.label).toBe('MotdMessage');
  });

  it('routes the Job Queue group to the automation sub-page', () => {
    // `HistorySeedingBackgroundService` declares Group = "Job Queue". A group with
    // no entry here is silently skipped by the palette (`if (!location) continue`),
    // which is how that section ended up unreachable by any route.
    //
    // NOTE: this only asserts the single Job Queue mapping — it does NOT enumerate
    // backend section metadata. The real guard that every backend-declared group
    // has a location entry is tracked as issue #951.
    expect(SETTINGS_GROUP_TO_LOCATION['Job Queue']).toBeDefined();
    expect(SETTINGS_GROUP_TO_LOCATION['Job Queue']?.subPageId).toBe('automation');
  });

  it('disambiguates a property name shared by two sections on the same page', () => {
    // `Enabled` exists on 13 backend settings classes, several of which render
    // on a single page. A bare `field=Enabled` link would resolve to whichever
    // section rendered first, so the href must carry the section key.
    const shared: SettingMetadata[] = [
      {
        key: 'ObicoSettings',
        className: 'ObicoSettings',
        group: 'General',
        properties: [{ name: 'Enabled', type: 'bool', attributes: [] }],
      },
      {
        key: 'TelegramSettings',
        className: 'TelegramSettings',
        group: 'General',
        properties: [{ name: 'Enabled', type: 'bool', attributes: [] }],
      },
    ];

    const items = buildSettingCommandItems(shared, groups);
    const obico = items.find((item) => item.id === 'setting.ObicoSettings.Enabled');
    const telegram = items.find((item) => item.id === 'setting.TelegramSettings.Enabled');

    expect(obico?.href).toContain('field=ObicoSettings.Enabled');
    expect(telegram?.href).toContain('field=TelegramSettings.Enabled');
    expect(obico?.href).not.toBe(telegram?.href);
  });

  it('skips sections whose group is not mapped to a settings location', () => {
    const items = buildSettingCommandItems(metadata, groups);
    expect(items.some((item) => item.id.startsWith('setting.UnknownSection'))).toBe(false);
  });

  it('returns an empty array when metadata is missing', () => {
    expect(buildSettingCommandItems(undefined, undefined)).toEqual([]);
    expect(buildSettingCommandItems([], undefined)).toEqual([]);
  });
});

describe('SETTINGS_GROUP_TO_LOCATION', () => {
  it('covers every group used by the settings backend that has a rendered destination', () => {
    // Regression guard: if a new backend group ships without a mapping the
    // palette will silently drop those settings. Explicitly assert the ones we
    // rely on so anyone renaming a group in the shell has to update this map.
    expect(SETTINGS_GROUP_TO_LOCATION.General).toBeDefined();
    expect(SETTINGS_GROUP_TO_LOCATION.Slicing).toBeDefined();
    expect(SETTINGS_GROUP_TO_LOCATION.Integrations).toBeDefined();
  });

  it('maps every group referenced from a sub-page\'s allowedGroups', () => {
    // Adding a settings group requires editing BOTH `SUB_PAGE_ALLOWED_GROUPS`
    // in `SettingsShell.tsx` AND `SETTINGS_GROUP_TO_LOCATION` here. Miss either
    // and the group silently disappears from both the UI (the shell doesn't
    // render it) and the command palette (`if (!location) continue`). This is
    // exactly how the Job Queue group went missing in the first place.
    const referencedGroups = new Set<string>();
    for (const groups of Object.values(SUB_PAGE_ALLOWED_GROUPS)) {
      for (const group of groups) {
        referencedGroups.add(group);
      }
    }
    for (const group of referencedGroups) {
      expect(
        SETTINGS_GROUP_TO_LOCATION[group],
        `group "${group}" appears in a sub-page's allowedGroups but has no SETTINGS_GROUP_TO_LOCATION mapping`,
      ).toBeDefined();
    }
  });

  it('every mapped location is reachable from some sub-page\'s allowedGroups', () => {
    // The reverse direction — every group with a palette mapping must also
    // render on some page. If a group is mapped here but no sub-page owns it,
    // the palette will deep-link to a page that doesn't display the setting.
    const owningGroups = new Set<string>();
    for (const groups of Object.values(SUB_PAGE_ALLOWED_GROUPS)) {
      for (const group of groups) {
        owningGroups.add(group);
      }
    }
    for (const group of Object.keys(SETTINGS_GROUP_TO_LOCATION)) {
      expect(
        owningGroups.has(group),
        `group "${group}" is mapped in SETTINGS_GROUP_TO_LOCATION but no sub-page's allowedGroups renders it`,
      ).toBe(true);
    }
  });

  it('each sub-page\'s mapped locations resolve to that sub-page', () => {
    // The palette lands the user on the sub-page pointed at by the group's
    // SETTINGS_GROUP_TO_LOCATION entry. If those entries drift from the shell's
    // wiring, clicking a Job Queue setting could route the user to (say) the
    // Farm sub-page — where the field is nowhere to be seen.
    for (const [subPageKey, groups] of Object.entries(SUB_PAGE_ALLOWED_GROUPS)) {
      // Guard against silently mis-inferring the sub-page id if a nested key
      // like "general.security.advanced" is ever introduced: `.split('.')` +
      // destructuring on `[, expectedSubPageId]` would truncate to the middle
      // segment, so this test would push the developer to map a group to the
      // wrong sub-page. Force the failure to point at the guard instead.
      const segments = subPageKey.split('.');
      expect(
        segments,
        `SUB_PAGE_ALLOWED_GROUPS key "${subPageKey}" must be a "category.sub" pair — update this guard before introducing nested keys`,
      ).toHaveLength(2);
      const expectedSubPageId = segments[1];
      for (const group of groups) {
        const location = SETTINGS_GROUP_TO_LOCATION[group];
        expect(location, `mapping missing for group "${group}"`).toBeDefined();
        expect(
          location.subPageId,
          `group "${group}" is rendered on ${subPageKey} but SETTINGS_GROUP_TO_LOCATION points elsewhere`,
        ).toBe(expectedSubPageId);
      }
    }
  });
});
