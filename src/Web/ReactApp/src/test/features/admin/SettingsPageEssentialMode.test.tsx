import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act, within } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Coverage for the Essential/Everything toggle and inline search added in #937.
 *
 * The invariants we care about:
 *  - Essential mode hides advanced properties AND hides sections that have
 *    zero essential properties entirely (no empty section cards).
 *  - Everything mode shows every property in every section.
 *  - Mode selection persists to localStorage under `pf.settings.mode`.
 *  - Search ALWAYS looks across every property, regardless of the current
 *    mode — a search for an advanced setting still finds it in Essential mode.
 *  - Editing a search result routes through the same per-section save endpoint
 *    that per-group editing uses.
 *  - Empty search results show a clear "no results" state with a clear-search
 *    action.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        // In manifest: enabled, retentionDays are BOTH essential.
        key: 'SystemLog',
        className: 'SystemLogSettings',
        displayName: 'System Log',
        description: 'Application-wide log retention.',
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
            display: {
              name: 'Retention Days',
              inputType: 'Number',
              minValue: 1,
              maxValue: 365,
            },
          },
          {
            // Advanced — NOT in manifest.
            name: 'verboseTracing',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Verbose Tracing', inputType: 'Boolean' },
          },
        ],
      },
      {
        // Nothing in manifest — the whole section should hide in Essential mode.
        key: 'AdvancedTuning',
        className: 'AdvancedTuningSettings',
        displayName: 'Advanced Tuning',
        description: 'Low-level tuning knobs.',
        group: 'System',
        order: 2,
        properties: [
          {
            name: 'threadPoolSize',
            type: 'number',
            attributes: [],
            display: { name: 'Thread Pool Size', inputType: 'Number' },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLog: { enabled: true, retentionDays: 30, verboseTracing: false },
      AdvancedTuning: { threadPoolSize: 4 },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
    saveAllSettings: vi.fn(),
  };
});

vi.mock('@/common/components/admin', async () => {
  const actual = await vi.importActual<typeof import('@/common/components/admin')>(
    '@/common/components/admin',
  );
  return {
    ...actual,
    adminToast: {
      success: (msg: string) => toastSuccessMock(msg),
      error: (msg: string) => toastErrorMock(msg),
      info: vi.fn(),
      warning: vi.fn(),
    },
  };
});

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));

vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));

vi.mock('@/features/admin/tours/settings.tour', () => ({
  settingsTour: [],
}));

vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div', null, 'ObicoServersMock'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div', null, 'FailureDetectionMock'),
}));

import { SettingsPage } from '@/features/admin/pages/SettingsPage';

async function renderPage() {
  const result = render(<SettingsPage />);
  // Wait for load to finish — the mode-controls bar is always visible in the
  // loaded state and doesn't depend on which mode we started in.
  await waitFor(() => {
    expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
  });
  return result;
}

describe('SettingsPage — Essential / Everything toggle (#937)', () => {
  beforeEach(() => {
    saveSettingsMock.mockReset().mockResolvedValue(undefined);
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
    window.localStorage.clear();
  });

  afterEach(() => {
    window.localStorage.clear();
  });

  it('defaults to Essential mode when nothing is persisted', async () => {
    await renderPage();
    const essentialRadio = screen.getByRole('radio', { name: /^Essential:/ });
    expect(essentialRadio).toHaveAttribute('aria-checked', 'true');
  });

  it('hides non-essential fields AND fully-advanced sections in Essential mode', async () => {
    await renderPage();
    // Essential SystemLog fields — visible.
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    expect(screen.getByLabelText('Log Enabled')).toBeInTheDocument();
    // Advanced field inside a partially-essential section — hidden.
    expect(screen.queryByLabelText('Verbose Tracing')).not.toBeInTheDocument();
    // Fully-advanced section — the section card should not render at all.
    expect(screen.queryByText('Advanced Tuning')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Thread Pool Size')).not.toBeInTheDocument();
  });

  it('shows every property in every section when Everything is selected', async () => {
    await renderPage();
    fireEvent.click(screen.getByRole('radio', { name: /^Everything:/ }));

    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    expect(screen.getByLabelText('Verbose Tracing')).toBeInTheDocument();
    expect(screen.getByText('Advanced Tuning')).toBeInTheDocument();
    expect(screen.getByLabelText('Thread Pool Size')).toBeInTheDocument();
  });

  it('persists the mode selection to localStorage', async () => {
    await renderPage();
    fireEvent.click(screen.getByRole('radio', { name: /^Everything:/ }));
    expect(window.localStorage.getItem('pf.settings.mode')).toBe('everything');

    fireEvent.click(screen.getByRole('radio', { name: /^Essential:/ }));
    expect(window.localStorage.getItem('pf.settings.mode')).toBe('essential');
  });

  it('honours a persisted "everything" mode on first render', async () => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    await renderPage();
    expect(screen.getByRole('radio', { name: /^Everything:/ })).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByLabelText('Thread Pool Size')).toBeInTheDocument();
  });

  it('search finds advanced fields even while Essential mode is active', async () => {
    await renderPage();
    expect(screen.queryByLabelText('Thread Pool Size')).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('Search all settings…'), {
      target: { value: 'thread' },
    });

    expect(await screen.findByLabelText('Thread Pool Size')).toBeInTheDocument();
    // Essential-mode fields that don't match are dropped from the results.
    expect(screen.queryByLabelText('Log Enabled')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Retention Days')).not.toBeInTheDocument();
  });

  it('search matching a section name shows every property in that section', async () => {
    await renderPage();
    fireEvent.change(screen.getByPlaceholderText('Search all settings…'), {
      target: { value: 'advanced tuning' },
    });

    // Section matched by name -> every property in it is shown.
    expect(await screen.findByLabelText('Thread Pool Size')).toBeInTheDocument();
    // Non-matched sections stay hidden.
    expect(screen.queryByLabelText('Retention Days')).not.toBeInTheDocument();
  });

  it('editing a search result routes through the per-section save endpoint', async () => {
    await renderPage();
    fireEvent.change(screen.getByPlaceholderText('Search all settings…'), {
      target: { value: 'thread' },
    });

    const input = await screen.findByLabelText('Thread Pool Size');
    fireEvent.change(input, { target: { value: '8' } });

    const saveBtn = await screen.findByRole('button', { name: /save system/i });
    await act(async () => {
      fireEvent.click(saveBtn);
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    // Uses the section key, not a batch endpoint.
    expect(saveSettingsMock).toHaveBeenCalledWith('AdvancedTuning', { threadPoolSize: 8 });
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('System settings saved'));
  });

  it('clears search via the Clear button and restores mode-filtered view', async () => {
    await renderPage();
    const searchInput = screen.getByPlaceholderText('Search all settings…');
    fireEvent.change(searchInput, { target: { value: 'thread' } });
    expect(await screen.findByLabelText('Thread Pool Size')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /clear search/i }));

    // Essential mode is back, so the advanced section disappears again.
    expect(screen.queryByLabelText('Thread Pool Size')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
  });

  it('shows an empty state with a clear action when nothing matches', async () => {
    await renderPage();
    fireEvent.change(screen.getByPlaceholderText('Search all settings…'), {
      target: { value: 'zzzzzz-nothing-matches' },
    });

    const empty = await screen.findByText(/no settings match your search/i);
    expect(empty).toBeInTheDocument();
    const clearBtn = within(empty.parentElement as HTMLElement).getByRole('button', {
      name: /clear search/i,
    });
    fireEvent.click(clearBtn);
    expect(screen.queryByText(/no settings match your search/i)).not.toBeInTheDocument();
  });

  it('exposes the mode toggle as an accessible radiogroup', async () => {
    await renderPage();
    const group = screen.getByRole('radiogroup', { name: /settings visibility/i });
    const radios = within(group).getAllByRole('radio');
    expect(radios).toHaveLength(2);
    // Selected radio is the group's tab stop; the other is -1.
    const selected = radios.find((r) => r.getAttribute('aria-checked') === 'true');
    const unselected = radios.find((r) => r.getAttribute('aria-checked') === 'false');
    expect(selected).toHaveAttribute('tabindex', '0');
    expect(unselected).toHaveAttribute('tabindex', '-1');
  });
});
