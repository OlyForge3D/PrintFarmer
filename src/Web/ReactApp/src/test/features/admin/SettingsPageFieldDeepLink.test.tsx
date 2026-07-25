import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Epic #939 — palette deep-links must land on the *right* setting.
 *
 * The `?field=` param has two dialects:
 *   1. `?field=Section.Property` (qualified) — must match the row exactly.
 *   2. `?field=Property` (bare) — legacy, suffix-matches the first row.
 *
 * The property name `enabled` alone is declared on ~13 settings sections and
 * several appear on the same page, so a bare-name selector will silently land
 * on the wrong row. #938 fixed this by qualifying palette-generated links —
 * these tests lock that in.
 *
 * Additionally the deep-link must NOT persist an Essential-mode override to
 * localStorage. `effectiveMode = fieldParam ? 'everything' : mode` gives the
 * requested override but must never mutate the persisted preference.
 */

const scrollIntoViewMock = vi.fn();
const saveSettingsMock = vi.fn();
const matchMediaMock = vi.fn().mockImplementation(() => ({
  matches: false,
  addEventListener: vi.fn(),
  removeEventListener: vi.fn(),
}));

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        key: 'SystemLog',
        className: 'SystemLogSettings',
        displayName: 'System Log',
        description: 'Log retention.',
        group: 'System',
        order: 1,
        properties: [
          {
            name: 'enabled',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Log Enabled', inputType: 'Boolean' },
          },
          {
            name: 'retentionDays',
            type: 'number',
            attributes: [],
            display: { name: 'Retention Days', inputType: 'Number' },
          },
        ],
      },
      {
        key: 'CatalogUpdates',
        className: 'CatalogUpdateSettings',
        displayName: 'Catalog Updates',
        description: 'Manufacturer/model catalog refresh.',
        group: 'System',
        order: 2,
        properties: [
          {
            name: 'enabled',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Catalog Enabled', inputType: 'Boolean' },
          },
          {
            // Deliberately advanced — not in essential-manifest for CatalogUpdates.
            name: 'autoApply',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Auto Apply', inputType: 'Boolean' },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLog: { enabled: true, retentionDays: 30 },
      CatalogUpdates: { enabled: false, autoApply: false },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
  };
});

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));
vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));
vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));
vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div', null, 'ObicoServersMock'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div', null, 'FailureDetectionMock'),
}));

import { SettingsPage } from '@/features/admin/pages/SettingsPage';

async function renderPageWithField(fieldParam?: string) {
  const entry = fieldParam ? `/?field=${encodeURIComponent(fieldParam)}` : '/';
  const result = render(
    <MemoryRouter initialEntries={[entry]}>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
  });
  return result;
}

describe('SettingsPage — palette `?field=` deep-link resolution (#939)', () => {
  beforeEach(() => {
    scrollIntoViewMock.mockReset();
    saveSettingsMock.mockReset().mockResolvedValue(undefined);
    // JSDOM does not implement scrollIntoView — polyfill so the effect runs.
    Element.prototype.scrollIntoView = scrollIntoViewMock;
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: matchMediaMock,
    });
    // Deep-link scrolling runs inside requestAnimationFrame; JSDOM ships a
    // trivial version but be explicit so the callback runs immediately.
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      cb(performance.now());
      return 0;
    });
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    window.localStorage.clear();
  });

  it('qualified `?field=Section.Property` scrolls the *specific* row and not any suffix match', async () => {
    await renderPageWithField('CatalogUpdates.enabled');

    // Both `enabled` rows exist in the DOM (Essential mode; both are essential).
    const systemLogRow = document.querySelector('[data-setting-property="SystemLog.enabled"]');
    const catalogRow = document.querySelector('[data-setting-property="CatalogUpdates.enabled"]');
    expect(systemLogRow).toBeTruthy();
    expect(catalogRow).toBeTruthy();

    // The effect calls scrollIntoView exactly once, on the qualified target.
    await waitFor(() => expect(scrollIntoViewMock).toHaveBeenCalledTimes(1));
    // `scrollIntoView` is on the element instance — assert via `this` context
    // by checking the mocked element received the transient highlight class.
    await waitFor(() => {
      expect(catalogRow!.classList.contains('pf-setting-focus')).toBe(true);
    });
    // The unrelated SystemLog.enabled row does NOT get the highlight —
    // that would be the regression the qualifier prevents.
    expect(systemLogRow!.classList.contains('pf-setting-focus')).toBe(false);
  });

  it('qualified deep-link to an advanced field bypasses Essential mode without persisting the change', async () => {
    // Persist Essential mode, then deep-link to an advanced field.
    window.localStorage.setItem('pf.settings.mode', 'essential');

    await renderPageWithField('CatalogUpdates.autoApply');

    // Advanced field is visible even though the user is in Essential mode —
    // `effectiveMode` overrode to 'everything' for this render.
    const autoApplyRow = document.querySelector('[data-setting-property="CatalogUpdates.autoApply"]');
    expect(autoApplyRow).toBeTruthy();
    expect(autoApplyRow!.querySelector('input[type="checkbox"]')).toBeTruthy();

    // Essential-mode fields still render.
    const catalogEnabledRow = document.querySelector('[data-setting-property="CatalogUpdates.enabled"]');
    expect(catalogEnabledRow).toBeTruthy();

    // CRUCIAL: the persisted preference must be untouched. Any code path that
    // called `setMode('everything')` here would leave the user in Everything
    // mode on their next page load — a silent, sticky mode flip.
    expect(window.localStorage.getItem('pf.settings.mode')).toBe('essential');

    // Deep-link highlight lands on the correct row.
    await waitFor(() => {
      expect(autoApplyRow!.classList.contains('pf-setting-focus')).toBe(true);
    });
  });

  it('a URL with no `?field=` does not scroll or highlight anything', async () => {
    await renderPageWithField(undefined);
    // Nothing gets highlighted when there's no deep-link.
    expect(scrollIntoViewMock).not.toHaveBeenCalled();
    const anyHighlight = document.querySelector('.pf-setting-focus');
    expect(anyHighlight).toBeNull();
  });

  it('bare `?field=<Property>` still resolves (legacy suffix-match) for older links', async () => {
    // Existing bookmarks that pre-date the palette qualification fix — must
    // still land *somewhere* rather than dead-linking. Which of the two
    // `enabled` rows wins depends on DOM order, but the effect must fire.
    await renderPageWithField('retentionDays');

    await waitFor(() => expect(scrollIntoViewMock).toHaveBeenCalledTimes(1));
    const target = document.querySelector('[data-setting-property$=".retentionDays"]');
    expect(target).toBeTruthy();
    await waitFor(() => {
      expect(target!.classList.contains('pf-setting-focus')).toBe(true);
    });
  });
});
