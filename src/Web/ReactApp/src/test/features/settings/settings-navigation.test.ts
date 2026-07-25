import { describe, expect, it } from 'vitest';
import {
  buildAdminDestinationCommandItems,
  buildSettingCommandItems,
  buildSettingsPath,
  resolveSettingsNavigationTarget,
  SETTINGS_GROUP_TO_LOCATION,
} from '@/features/settings/settings-navigation';
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

  it('maps legacy category aliases to the scoped destination', () => {
    expect(resolveSettingsNavigationTarget('system', 'workers', undefined)).toEqual({
      scopeId: 'admin',
      categoryId: 'operations',
      subPageId: 'workers',
    });
  });
});

describe('buildSettingsPath', () => {
  it('routes admin scope to /admin/manage', () => {
    expect(
      buildSettingsPath({ scopeId: 'admin', categoryId: 'users', subPageId: 'audit' }),
    ).toBe('/admin/manage?scope=admin&tab=users&sub=audit');
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
    id: 'users.audit',
    label: 'Login Audit',
    description: 'Recent sign-in attempts.',
    group: 'users',
    icon: StubIcon,
    path: '/admin/manage?tab=users&sub=audit',
    keywords: ['audit', 'login'],
  };

  it('emits one destination item per registry entry with kind and href set', () => {
    const [item] = buildAdminDestinationCommandItems([destination]);
    expect(item.id).toBe('dest.users.audit');
    expect(item.kind).toBe('destination');
    expect(item.href).toBe('/admin/manage?tab=users&sub=audit');
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
});
