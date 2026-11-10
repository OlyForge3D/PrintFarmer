import { describe, expect, it } from 'vitest';
import {
  buildAdminDestinationCommandItems,
  buildSettingCommandItems,
  buildSettingsPath,
  buildSettingsCommandItems,
  getFuzzyMatchIndices,
  getFuzzyResult,
  getSettingsCategoryDestinations,
  groupRankedResults,
  KIND_SECTION_ORDER,
  normalizeSearchQuery,
  rankSettingsCommandItems,
  resolveSettingsNavigationTarget,
  withRetainedQuery,
  type SettingsCommandItem,
} from '@/features/settings/settings-navigation';
import {
  getSettingsSectionLocation,
  getSettingsSectionPage,
  SETTINGS_GROUP_TO_LOCATION,
  SETTINGS_SECTION_TO_LOCATION,
} from '@/features/settings/subpage-groups';
import type { AdminDestination } from '@/features/admin/registry/adminDestinations';
import type { SettingMetadata } from '@/common/components/SettingsPagelet';
import type { SettingGroupMetadata } from '@/services/settingsApi';

describe('getSettingsCategoryDestinations', () => {
  it('uses the approved system categories and never adds personal settings', () => {
    const cards = getSettingsCategoryDestinations({ hasRole: () => true, hasPermission: () => true });
    expect(cards.map(card => card.label)).toEqual([
      'System',
      'Failure Detection',
      'Costs & Energy',
      'Planning',
      'Operator Features',
      'Slicing',
      'Hardware',
      'Integrations',
      'Quotas',
      'Users',
      'Data',
    ]);
    expect(cards.every(card => card.icon && card.description)).toBe(true);
    expect(new Set(cards.map(card => card.path)).size).toBe(11);
  });

  it.each([
    ['system_settings', [
      'system.',
      'failure-detection.',
      'costs.',
      'planning.',
      'operator-features.',
      'slicing.defaults',
      'integrations.connections',
      'data.auto-tagging',
    ]],
    ['roles', ['users.roles']],
    ['users', ['users.accounts']],
    ['printers', ['hardware.printer-groups']],
    ['nfc_devices', ['hardware.nfc']],
    ['bed_type', ['slicing.bed-types']],
    ['telegram', ['integrations.connections']],
    ['quota', ['quotas.']],
    ['tags', ['data.tags']],
    ['catalog', []],
    ['power_monitors', []],
    ['locations', []],
  ])('routes a %s-only delegate directly to their first authorized leaf', (resource, expected) => {
    const cards = getSettingsCategoryDestinations({
      hasRole: () => false,
      hasPermission: (r, action) => r === resource && action === 'admin',
    });
    expect(cards.map(card => {
      const params = new URLSearchParams(card.path.split('?')[1]);
      return `${params.get('tab')}.${params.get('sub') ?? ''}`;
    })).toEqual(expected);
  });

  it('does not invent a settings category for a user with no grants', () => {
    expect(getSettingsCategoryDestinations({ hasRole: () => false, hasPermission: () => false })).toEqual([]);
  });
});

describe('resolveSettingsNavigationTarget', () => {
  it('resolves the new appearance subtab without redirect aliases', () => {
    expect(resolveSettingsNavigationTarget('profile', 'appearance', 'user')).toEqual({
      scopeId: 'user', categoryId: 'profile', subPageId: 'appearance',
    });
    expect(buildSettingsPath({ scopeId: 'user', categoryId: 'profile', subPageId: 'appearance' }))
      .toBe('/settings?scope=user&tab=profile&sub=appearance');
  });

  it.each([['theme', 'appearance'], ['preview', 'appearance'], ['expert', 'preferences'], ['guided', 'preferences'], ['printables', 'preferences']])(
    'indexes %s under %s, not unrelated sibling tabs', (query, subPageId) => {
      const items = buildSettingsCommandItems().filter(item => item.scopeId === 'user' && item.subPageId);
      const matches = items.filter(item => item.keywords.includes(query));
      expect(matches.map(item => item.subPageId)).toEqual([subPageId]);
    },
  );

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
      categoryId: 'system',
    });
  });

  it('keeps deep links with both tab and sub params intact', () => {
    expect(resolveSettingsNavigationTarget('hardware', 'printer-groups', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'hardware',
      subPageId: 'printer-groups',
    });
  });

  it('maps legacy General and Automation tabs to their replacement categories', () => {
    expect(resolveSettingsNavigationTarget('operations', 'workers', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'system',
    });

    expect(resolveSettingsNavigationTarget('general', 'farm', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'costs',
    });
    expect(resolveSettingsNavigationTarget('general', 'system', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'system',
    });
    expect(resolveSettingsNavigationTarget('general', 'automation', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'planning',
    });
    expect(resolveSettingsNavigationTarget('automation', undefined, 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'planning',
    });
  });

  it('uses section-aware legacy links and canonicalizes retired hardware sub-pages', () => {
    expect(resolveSettingsNavigationTarget('general', 'farm', 'system', 'Obico.Enabled')).toEqual({
      scopeId: 'system',
      categoryId: 'failure-detection',
    });
    expect(resolveSettingsNavigationTarget('hardware', 'nfc-bindings', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'hardware',
      subPageId: 'nfc',
    });
    expect(resolveSettingsNavigationTarget('hardware', 'custom-fields', 'system')).toEqual({
      scopeId: 'system',
      categoryId: 'data',
      subPageId: 'custom-fields',
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
      buildSettingsPath({ scopeId: 'system', categoryId: 'system' }),
    ).toBe('/admin/settings?scope=system&tab=system');
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
        categoryId: 'costs',
        field: 'CostTracking.ElectricityRate',
      }),
    ).toBe('/admin/settings?scope=system&tab=costs&field=CostTracking.ElectricityRate');
  });

  it('routes maintenance field links to the maintenance settings tab', () => {
    expect(buildSettingsPath({
      scopeId: 'system',
      categoryId: 'maintenance',
      subPageId: 'settings',
      field: 'MaintenanceAlerts.Enabled',
    })).toBe('/maintenance?tab=settings&field=MaintenanceAlerts.Enabled');
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
      key: 'CostTracking',
      className: 'CostTrackingSettings',
      displayName: 'Cost Tracking',
      group: 'Operations',
      properties: [
        {
          name: 'ElectricityRate',
          type: 'string',
          attributes: [],
          display: { name: 'Electricity Rate', description: 'Default electricity rate.' },
        },
        {
          name: 'Currency',
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

  const groups: SettingGroupMetadata[] = [{ key: 'Operations', displayName: 'Operations', order: 0 }];

  it('emits one item per property with a field-scoped href', () => {
    const items = buildSettingCommandItems(metadata, groups);
    const electricityRate = items.find((item) => item.id === 'setting.CostTracking.ElectricityRate');
    expect(electricityRate).toBeDefined();
    expect(electricityRate?.kind).toBe('setting');
    expect(electricityRate?.label).toBe('Electricity Rate');
    // Qualified with the section key — property names are not unique across
    // sections, and several sections render on the same page.
    expect(electricityRate?.href).toBe(
      '/admin/settings?scope=system&tab=costs&field=CostTracking.ElectricityRate',
    );
  });

  it('falls back to the property name when no display metadata is present', () => {
    const items = buildSettingCommandItems(metadata, groups);
    const currency = items.find((item) => item.id === 'setting.CostTracking.Currency');
    expect(currency?.label).toBe('Currency');
  });

  it('routes the Job Queue group to Planning', () => {
    // `HistorySeedingBackgroundService` declares Group = "Job Queue". A group with
    // no entry here is silently skipped by the palette (`if (!location) continue`),
    // which is how that section ended up unreachable by any route.
    //
    // NOTE: this only asserts the single Job Queue mapping — it does NOT enumerate
    // backend section metadata. The real guard that every backend-declared group
    // has a location entry is tracked as issue #951.
    expect(SETTINGS_GROUP_TO_LOCATION['Job Queue']).toBeDefined();
    expect(SETTINGS_GROUP_TO_LOCATION['Job Queue']).toEqual({
      scopeId: 'system',
      categoryId: 'planning',
    });
  });

  it.each([
    ['CostTracking', 'Operations', '/admin/settings?scope=system&tab=costs&field=CostTracking.Enabled'],
    ['Obico', 'Monitoring', '/admin/settings?scope=system&tab=failure-detection&field=Obico.Enabled'],
    ['SlicerSettings', 'Operations', '/admin/settings?scope=system&tab=slicing&sub=defaults&field=SlicerSettings.Enabled'],
    ['OperatorFeatures', 'Operations', '/admin/settings?scope=system&tab=operator-features&field=OperatorFeatures.Enabled'],
    ['AutoTagging', 'Operations', '/admin/settings?scope=system&tab=data&sub=auto-tagging&field=AutoTagging.Enabled'],
    ['QueuePlanning', 'Operations', '/admin/settings?scope=system&tab=planning&field=QueuePlanning.Enabled'],
    ['ShiftPlan', 'Operations', '/admin/settings?scope=system&tab=planning&field=ShiftPlan.Enabled'],
    ['SpoolCoverage', 'Operations', '/admin/settings?scope=system&tab=planning&field=SpoolCoverage.Enabled'],
    ['HistorySeeding', 'Job Queue', '/admin/settings?scope=system&tab=planning&field=HistorySeeding.Enabled'],
    ['MaintenanceAlerts', 'Maintenance', '/maintenance?tab=settings&field=MaintenanceAlerts.Enabled'],
    ['PrintStatisticsSync', 'Maintenance', '/maintenance?tab=settings&field=PrintStatisticsSync.Enabled'],
  ])('links real %s section metadata directly to its home', (key, group, href) => {
    const section: SettingMetadata = {
      key,
      className: `${key}Settings`,
      group,
      properties: [{ name: 'Enabled', type: 'bool', attributes: [] }],
    };

    const [item] = buildSettingCommandItems([section], [{ key: group, displayName: group, order: 0 }]);
    expect(item?.href).toBe(href);
  });

  it('disambiguates a property name shared by two sections on the same page', () => {
    // `Enabled` exists on 13 backend settings classes, several of which render
    // on a single page. A bare `field=Enabled` link would resolve to whichever
    // section rendered first, so the href must carry the section key.
    const shared: SettingMetadata[] = [
      {
        key: 'QueuePlanning',
        className: 'QueuePlanningSettings',
        group: 'Operations',
        properties: [{ name: 'Enabled', type: 'bool', attributes: [] }],
      },
      {
        key: 'ShiftPlan',
        className: 'ShiftPlanSettings',
        group: 'Operations',
        properties: [{ name: 'Enabled', type: 'bool', attributes: [] }],
      },
    ];

    const items = buildSettingCommandItems(shared, [
      { key: 'Operations', displayName: 'Operations', order: 0 },
    ]);
    const queuePlanning = items.find((item) => item.id === 'setting.QueuePlanning.Enabled');
    const shiftPlan = items.find((item) => item.id === 'setting.ShiftPlan.Enabled');

    expect(queuePlanning?.href).toContain('field=QueuePlanning.Enabled');
    expect(shiftPlan?.href).toContain('field=ShiftPlan.Enabled');
    expect(queuePlanning?.href).not.toBe(shiftPlan?.href);
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

describe('settings section locations', () => {
  it('maps persisted setting groups to the approved settings homes', () => {
    expect(SETTINGS_GROUP_TO_LOCATION).toEqual({
      System: { scopeId: 'system', categoryId: 'system' },
      Networking: { scopeId: 'system', categoryId: 'system' },
      Catalog: { scopeId: 'system', categoryId: 'system' },
      Files: { scopeId: 'system', categoryId: 'system' },
      Printers: { scopeId: 'system', categoryId: 'system' },
      Operations: { scopeId: 'system', categoryId: 'planning' },
      Monitoring: { scopeId: 'system', categoryId: 'failure-detection' },
      Maintenance: { scopeId: 'system', categoryId: 'maintenance', subPageId: 'settings' },
      'Job Queue': { scopeId: 'system', categoryId: 'planning' },
      Integrations: { scopeId: 'system', categoryId: 'integrations', subPageId: 'connections' },
      Slicing: { scopeId: 'system', categoryId: 'slicing', subPageId: 'defaults' },
    });
  });

  it('maps real section keys to their canonical homes, including browser slicer and maintenance settings', () => {
    expect(SETTINGS_SECTION_TO_LOCATION).toEqual({
      CostTracking: { scopeId: 'system', categoryId: 'costs' },
      Obico: { scopeId: 'system', categoryId: 'failure-detection' },
      SlicerSettings: { scopeId: 'system', categoryId: 'slicing', subPageId: 'defaults' },
      OperatorFeatures: { scopeId: 'system', categoryId: 'operator-features' },
      AutoTagging: { scopeId: 'system', categoryId: 'data', subPageId: 'auto-tagging' },
      QueuePlanning: { scopeId: 'system', categoryId: 'planning' },
      ShiftPlan: { scopeId: 'system', categoryId: 'planning' },
      SpoolCoverage: { scopeId: 'system', categoryId: 'planning' },
      HistorySeeding: { scopeId: 'system', categoryId: 'planning' },
      MaintenanceAlerts: { scopeId: 'system', categoryId: 'maintenance', subPageId: 'settings' },
      PrintStatisticsSync: { scopeId: 'system', categoryId: 'maintenance', subPageId: 'settings' },
    });
    expect(getSettingsSectionPage('SlicerSettings', 'Operations')).toBe('slicing.defaults');
    expect(getSettingsSectionPage('MaintenanceAlerts', 'Maintenance')).toBe('maintenance.settings');
  });

  it('prefers an exact section override to its broader metadata group', () => {
    expect(getSettingsSectionLocation('CostTracking', 'Operations')).toEqual({
      scopeId: 'system',
      categoryId: 'costs',
    });
    expect(getSettingsSectionLocation('UnknownSection', 'Operations')).toEqual({
      scopeId: 'system',
      categoryId: 'planning',
    });
  });

  it('returns no location or page for an unknown section and group', () => {
    expect(getSettingsSectionLocation('UnknownSection', 'Unmapped Group')).toBeUndefined();
    expect(getSettingsSectionPage('UnknownSection', 'Unmapped Group')).toBeUndefined();
  });
});

function makeItem(overrides: Partial<SettingsCommandItem> = {}): SettingsCommandItem {
  return {
    id: 'item-1',
    kind: 'setting',
    scopeId: 'system',
    categoryId: 'general',
    label: 'System Log',
    description: 'System log settings',
    breadcrumb: 'System Settings > General',
    keywords: ['log'],
    href: '/admin/settings?scope=system&tab=general',
    ...overrides,
  };
}

describe('withRetainedQuery (#2505)', () => {
  it('appends q onto a /admin/settings href', () => {
    expect(withRetainedQuery('/admin/settings?scope=system&tab=general', 'slicer')).toBe(
      '/admin/settings?scope=system&tab=general&q=slicer',
    );
  });

  it('appends q onto a /settings href', () => {
    expect(withRetainedQuery('/settings?scope=user&tab=profile', 'theme')).toBe(
      '/settings?scope=user&tab=profile&q=theme',
    );
  });

  it('overwrites an existing q param rather than duplicating it', () => {
    expect(withRetainedQuery('/admin/settings?scope=system&tab=general&q=old', 'new')).toBe(
      '/admin/settings?scope=system&tab=general&q=new',
    );
  });

  it('does not append q onto a standalone destination outside the settings shell', () => {
    // Standalone admin destinations (e.g. an audit log page) must not leak a
    // settings search param onto an unrelated surface.
    expect(withRetainedQuery('/admin/login-audit', 'slicer')).toBe('/admin/login-audit');
  });

  it('returns the href unchanged when the query is empty', () => {
    expect(withRetainedQuery('/admin/settings?scope=system&tab=general', '')).toBe(
      '/admin/settings?scope=system&tab=general',
    );
  });
});

describe('getFuzzyMatchIndices', () => {
  it('returns an empty array (not null) for an empty query', () => {
    expect(getFuzzyMatchIndices('System Log', '')).toEqual([]);
  });

  it('matches a case-insensitive subsequence', () => {
    expect(getFuzzyMatchIndices('System Log', 'sylog')).toEqual([0, 1, 7, 8, 9]);
  });

  it('returns null when the query is not a subsequence', () => {
    expect(getFuzzyMatchIndices('System Log', 'zzz')).toBeNull();
  });
});

describe('getFuzzyResult', () => {
  it('matches on an exact qualified field name using real wire-format casing', () => {
    // Regression guard for "use actual metadata wire names" (#2505): the
    // field-scoped item's label/keywords must be reachable by the exact
    // camelCase wire name the backend serializes, not an invented PascalCase
    // variant.
    const item = makeItem({
      id: 'setting.SystemLog.enabled',
      label: 'Enabled',
      breadcrumb: 'System Settings > General > System',
      keywords: ['systemlog.enabled', 'enabled'],
    });
    const result = getFuzzyResult(item, normalizeSearchQuery('systemlog.enabled'));
    expect(result).not.toBeNull();
  });

  it('returns null when neither label, breadcrumb, nor keywords match', () => {
    expect(getFuzzyResult(makeItem(), normalizeSearchQuery('zzz-nomatch'))).toBeNull();
  });

  it('ranks an exact label match ahead of a breadcrumb-only match', () => {
    const exact = makeItem({ id: 'exact', label: 'Slicing', breadcrumb: 'Other > Section' });
    const breadcrumbOnly = makeItem({ id: 'breadcrumb-only', label: 'Unrelated', breadcrumb: 'Slicing > Section' });
    const query = normalizeSearchQuery('slicing');
    const exactResult = getFuzzyResult(exact, query);
    const breadcrumbResult = getFuzzyResult(breadcrumbOnly, query);
    expect(exactResult).not.toBeNull();
    expect(breadcrumbResult).not.toBeNull();
    expect(exactResult!.score).toBeLessThan(breadcrumbResult!.score);
  });
});

describe('rankSettingsCommandItems / groupRankedResults', () => {
  it('drops non-matching items and sorts matches best-first', () => {
    const items = [
      makeItem({ id: 'a', label: 'Zebra Settings', breadcrumb: 'Z' }),
      makeItem({ id: 'b', label: 'Slicing Defaults', breadcrumb: 'S' }),
      makeItem({ id: 'c', label: 'Completely Unrelated', breadcrumb: 'U', keywords: [] }),
    ];
    const ranked = rankSettingsCommandItems(items, 'slicing');
    expect(ranked.map((result) => result.item.id)).toEqual(['b']);
  });

  it('respects maxVisible', () => {
    const items = Array.from({ length: 5 }, (_, index) =>
      makeItem({ id: `item-${index}`, label: `Slicing ${index}`, breadcrumb: `Slicing ${index}` }));
    const ranked = rankSettingsCommandItems(items, 'slicing', { maxVisible: 2 });
    expect(ranked).toHaveLength(2);
  });

  it('groups results by kind in KIND_SECTION_ORDER, dropping empty sections', () => {
    const items = [
      makeItem({ id: 'dest', kind: 'destination', label: 'Slicing Destination', breadcrumb: 'Admin' }),
      makeItem({ id: 'field', kind: 'setting', label: 'Slicing Field', breadcrumb: 'Settings' }),
    ];
    const ranked = rankSettingsCommandItems(items, 'slicing');
    const grouped = groupRankedResults(ranked);
    expect(grouped.map((group) => group.kind)).toEqual(['destination', 'setting']);
    expect(grouped.every((group) => group.results.length > 0)).toBe(true);
    // Confirms the order tracks KIND_SECTION_ORDER rather than insertion order.
    const destinationOrderIndex = KIND_SECTION_ORDER.findIndex((entry) => entry.kind === 'destination');
    const settingOrderIndex = KIND_SECTION_ORDER.findIndex((entry) => entry.kind === 'setting');
    expect(destinationOrderIndex).toBeLessThan(settingOrderIndex);
  });

  it('returns an empty list for an empty query with no items matching (a workspace search with no text)', () => {
    expect(rankSettingsCommandItems([], 'slicing')).toEqual([]);
  });
});
