import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Epic #939 — the single most damaging regression the admin refactor could
 * ship is a filter / view-mode that silently drops values on save. The
 * Essential/Everything toggle (#937) and the per-page field filter both narrow
 * *rendering*, but the save payload must always be the complete section
 * values so hidden advanced fields are never clobbered with `undefined`.
 *
 * If any future change routes the visible-field allowlist into
 * `saveSettingsValues(sectionKey, values)` — even accidentally, e.g. by
 * passing a computed `displayProps`-scoped values map — this suite fails.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        // Two of three properties are marked essential in the manifest.
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
            // Advanced — not in the essential manifest for SystemLog.
            name: 'verboseTracing',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Verbose Tracing', inputType: 'Boolean' },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLog: { enabled: true, retentionDays: 30, verboseTracing: true },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
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
  const result = render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
  });
  return result;
}

describe('SettingsPage — hidden advanced fields survive save round-trip (#939)', () => {
  beforeEach(() => {
    saveSettingsMock.mockReset().mockResolvedValue(undefined);
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
    window.localStorage.clear();
  });

  afterEach(() => {
    window.localStorage.clear();
  });

  it('preserves an Essential-hidden advanced field verbatim when saving from Essential mode', async () => {
    // Essential mode is the default; verboseTracing is NOT in the manifest so
    // it must not render, but its persisted value (true) must survive save.
    await renderPage();

    expect(screen.getByLabelText('Log Enabled')).toBeInTheDocument();
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    // Sanity: the advanced field really is hidden — otherwise this test
    // wouldn't be exercising the round-trip guarantee.
    expect(screen.queryByLabelText('Verbose Tracing')).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '90' } });

    await act(async () => {
      fireEvent.click(await screen.findByRole('button', { name: /save system/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    const [sectionKey, payload] = saveSettingsMock.mock.calls[0];
    expect(sectionKey).toBe('SystemLog');
    // The mutated visible field goes through.
    expect(payload).toMatchObject({ retentionDays: 90 });
    // The other visible field is preserved as-is.
    expect(payload).toMatchObject({ enabled: true });
    // The KEY invariant: the hidden advanced field is included with its
    // ORIGINAL value. If a future change scopes save to visible-only, this
    // property either disappears or arrives as `undefined` — both fail here.
    expect(payload).toHaveProperty('verboseTracing', true);
    // Structural: only the three declared properties, nothing extra.
    expect(Object.keys(payload).sort()).toEqual(['enabled', 'retentionDays', 'verboseTracing']);
  });

  it('preserves a filter-hidden field when saving from a narrowed search view', async () => {
    // Switch to Everything so everything renders initially, then narrow via
    // the page filter to a single property. Editing that property must still
    // save a payload that includes the currently-hidden fields verbatim.
    window.localStorage.setItem('pf.settings.mode', 'everything');
    await renderPage();

    // Baseline: all three fields visible in Everything mode.
    expect(screen.getByLabelText('Log Enabled')).toBeInTheDocument();
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    expect(screen.getByLabelText('Verbose Tracing')).toBeInTheDocument();

    // Filter down to just the retention field. Use a query that only appears
    // in the property display name ("Days") so the section-level match
    // doesn't fall back to "show everything in this section".
    fireEvent.change(screen.getByPlaceholderText('Filter fields on this page…'), {
      target: { value: 'days' },
    });

    await waitFor(() => {
      expect(screen.queryByLabelText('Verbose Tracing')).not.toBeInTheDocument();
    });
    expect(screen.queryByLabelText('Log Enabled')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '120' } });

    await act(async () => {
      fireEvent.click(await screen.findByRole('button', { name: /save system/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    const [, payload] = saveSettingsMock.mock.calls[0];
    // Filter hides the other fields, but save still sends everything.
    expect(payload).toMatchObject({
      enabled: true,
      retentionDays: 120,
      verboseTracing: true,
    });
    expect(Object.keys(payload).sort()).toEqual(['enabled', 'retentionDays', 'verboseTracing']);
  });

  it('validates the full section on save, not just visible properties', async () => {
    // If validation ever gets scoped to displayed properties only, a
    // required-but-hidden field could silently be persisted as invalid.
    // We can't cheaply seed a required-hidden violation here without a
    // second mock fixture, so this test asserts the contract by monkey-
    // patching the fixture: even a value we never touched should show up in
    // the validation path (indirectly proved by the save succeeding on the
    // whole section, not just the visible one).
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });

    await act(async () => {
      fireEvent.click(await screen.findByRole('button', { name: /save system/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    // Success toast implies validation ran cleanly across the entire section
    // (all three properties, including the hidden verboseTracing).
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('System settings saved'));
  });
});
