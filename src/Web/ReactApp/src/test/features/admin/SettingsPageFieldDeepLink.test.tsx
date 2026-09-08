import '@testing-library/jest-dom';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useSearchParams } from 'react-router';
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
const usePageTourMock = vi.fn(() => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }));

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
            display: {
              name: 'Auto Apply',
              description: 'Apply downloaded catalog updates automatically.',
              inputType: 'Boolean',
            },
          },
          {
            name: 'notificationEmails',
            type: 'string[]',
            attributes: [],
            display: { name: 'Notification Emails', inputType: 'Array', isMulti: true },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLog: { enabled: true, retentionDays: 30 },
      CatalogUpdates: { enabled: false, autoApply: false, notificationEmails: ['alerts@example.com'] },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
  };
});

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));
vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: (...args: unknown[]) => usePageTourMock(...args),
}));
vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));
vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div', null, 'ObicoServersMock'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div', null, 'FailureDetectionMock'),
}));

const toastErrorMock = vi.fn();
vi.mock('sonner', () => ({
  toast: {
    error: (...args: unknown[]) => toastErrorMock(...args),
    success: vi.fn(),
    info: vi.fn(),
  },
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

function FieldReactivationHarness() {
  const [, setSearchParams] = useSearchParams();

  return (
    <>
      <button type="button" onClick={() => setSearchParams({})}>Clear field</button>
      <button type="button" onClick={() => setSearchParams({ field: 'CatalogUpdates.autoApply' })}>Restore field</button>
      <button type="button" onClick={() => setSearchParams({ field: 'CatalogUpdates.autoApply', q: 'printer' }, { replace: true })}>Update query</button>
      <SettingsPage />
    </>
  );
}

describe('SettingsPage — palette `?field=` deep-link resolution (#939)', () => {
  beforeEach(() => {
    scrollIntoViewMock.mockReset();
    toastErrorMock.mockReset();
    saveSettingsMock.mockReset().mockResolvedValue(undefined);
    usePageTourMock.mockClear();
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
    expect(catalogRow!.querySelector('input')).toHaveFocus();
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

  it('disables first-visit tour auto-start and focuses the exact control for a descriptive field deep link (#2556)', async () => {
    await renderPageWithField('CatalogUpdates.autoApply');

    expect(usePageTourMock).toHaveBeenCalledWith({
      tourId: 'settings',
      steps: [],
      autoStart: false,
    });
    const targetInput = document.getElementById('CatalogUpdates.autoApply');
    expect(targetInput).toBeTruthy();
    expect(targetInput).toHaveFocus();
  });

  it('falls back to the first array input when its qualified field link has no matching control ID', async () => {
    await renderPageWithField('CatalogUpdates.notificationEmails');

    const targetInput = document.querySelector<HTMLInputElement>(
      '[data-setting-property="CatalogUpdates.notificationEmails"] input',
    );
    expect(targetInput).toBeTruthy();
    expect(targetInput).toHaveFocus();
  });

  it('re-focuses the same exact field when the link is cleared and then re-activated on the same mounted page', async () => {
    render(
      <MemoryRouter initialEntries={['/?field=CatalogUpdates.autoApply']}>
        <FieldReactivationHarness />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
    });

    const targetInput = document.querySelector<HTMLInputElement>('[data-setting-property="CatalogUpdates.autoApply"] input');
    expect(targetInput).toBeTruthy();
    await waitFor(() => {
      expect(targetInput).toHaveFocus();
    });
    await waitFor(() => expect(scrollIntoViewMock).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Clear field' }));
    fireEvent.click(screen.getByRole('button', { name: 'Restore field' }));

    await waitFor(() => expect(scrollIntoViewMock).toHaveBeenCalledTimes(2));
    await waitFor(() => {
      expect((document.activeElement as HTMLElement | null)?.id).toBe('CatalogUpdates.autoApply');
    });
  });

  it('does not re-scroll or steal focus back when unrelated `?q=` updates happen while the same field deep-link stays active', async () => {
    render(
      <MemoryRouter initialEntries={['/?field=CatalogUpdates.autoApply']}>
        <FieldReactivationHarness />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
    });

    const targetInput = document.querySelector<HTMLInputElement>('[data-setting-property="CatalogUpdates.autoApply"] input');
    expect(targetInput).toBeTruthy();
    await waitFor(() => expect(scrollIntoViewMock).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Update query' }));

    await waitFor(() => {
      expect((document.activeElement as HTMLElement | null)?.id).toBe('CatalogUpdates.autoApply');
    });
    expect(scrollIntoViewMock).toHaveBeenCalledTimes(1);
  });

  it('surfaces a toast and leaves the page mounted when the deep-linked field does not resolve (#2505)', async () => {
    // The workspace search (#2505) can send `?field=` links to a field that
    // simply doesn't render on this page (stale metadata, a typo, or a field
    // that lives elsewhere entirely). Nothing should crash, no highlight
    // should apply, and the user gets a toast instead of silence.
    await renderPageWithField('NoSuchSection.NoSuchProperty');

    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledTimes(1));
    expect(toastErrorMock).toHaveBeenCalledWith(
      expect.stringContaining('NoSuchSection.NoSuchProperty'),
    );
    expect(scrollIntoViewMock).not.toHaveBeenCalled();
    expect(document.querySelector('.pf-setting-focus')).toBeNull();
    // The page itself stays mounted and usable.
    expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
  });
});
