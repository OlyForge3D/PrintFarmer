import { describe, expect, it } from 'vitest';
import { resolveSettingsNavigationTarget } from '@/features/settings/settings-navigation';

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
