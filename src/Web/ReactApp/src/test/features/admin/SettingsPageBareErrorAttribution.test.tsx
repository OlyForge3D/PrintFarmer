import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * Regression guard for the wrong-section error attribution defect. Property
 * names are not unique across settings classes — `enabled` alone is declared
 * on 13 backend settings — and multiple sections render on one page. Before
 * the fix, a bare error key (no `.`) fell through to
 * `metadata.find((m) => m.properties.some((p) => p.name === fieldName))`, so
 * the error pinned to whichever section rendered first regardless of what the
 * user was actually saving.
 *
 * Saves are per-section (one `POST /api/settings/{sectionKey}` per changed
 * group), so bare keys unambiguously belong to the section that was just
 * posted — this test asserts that behaviour, and that dotted `section.field`
 * keys from the backend still route to the section they name.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        key: 'ObicoSettings',
        className: 'ObicoSettings',
        displayName: 'Obico',
        description: 'Print failure detection.',
        group: 'Integrations',
        order: 1,
        properties: [
          {
            name: 'enabled',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Obico Enabled', inputType: 'Boolean' },
          },
        ],
      },
      {
        key: 'TelegramSettings',
        className: 'TelegramSettings',
        displayName: 'Telegram',
        description: 'Chat notifications.',
        group: 'Integrations',
        order: 2,
        properties: [
          {
            name: 'enabled',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Telegram Enabled', inputType: 'Boolean' },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'Integrations', displayName: 'Integrations', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      ObicoSettings: { enabled: false },
      TelegramSettings: { enabled: false },
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
vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));
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
    expect(screen.getByLabelText('Obico Enabled')).toBeInTheDocument();
    expect(screen.getByLabelText('Telegram Enabled')).toBeInTheDocument();
  });
  return result;
}

describe('SettingsPage — wrong-section error attribution', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
  });

  it('attributes a bare error key to the section that was posted, not the first section that declares the property', async () => {
    // Simulate the backend returning `{ errors: { enabled: '...' } }` from
    // saving *Telegram*. Before the fix, `extractFieldErrors` walked the
    // metadata and picked the FIRST section that declared `enabled` — which
    // for this fixture is Obico, ordered before Telegram — so the error
    // rendered under the wrong control.
    saveSettingsMock.mockRejectedValueOnce({
      response: { data: { errors: { enabled: 'Bot token is required to enable Telegram' } } },
    });

    const { container } = await renderPage();

    // Edit Telegram's Enabled checkbox — this is the section that will fail.
    fireEvent.click(screen.getByLabelText('Telegram Enabled'));

    const saveBtn = await screen.findByRole('button', { name: /save integrations/i });
    await act(async () => {
      fireEvent.click(saveBtn);
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(saveSettingsMock).toHaveBeenCalledWith('TelegramSettings', expect.any(Object));

    // The error must render inside the Telegram row, not the Obico row.
    // `data-setting-property` on each field row is the fully-qualified marker
    // written by SettingsPagelet — checking there ties the error to the
    // correct section unambiguously.
    const telegramRow = await waitFor(() => {
      const row = container.querySelector('[data-setting-property="TelegramSettings.enabled"]');
      expect(row).not.toBeNull();
      expect(row!.textContent).toMatch(/bot token is required/i);
      return row!;
    });

    const obicoRow = container.querySelector('[data-setting-property="ObicoSettings.enabled"]');
    expect(obicoRow).not.toBeNull();
    expect(obicoRow!.textContent).not.toMatch(/bot token is required/i);

    // Sanity: only one error alert on the page for this failure.
    const alerts = container.querySelectorAll('[role="alert"]');
    const matchingAlerts = Array.from(alerts).filter((el) =>
      /bot token is required/i.test(el.textContent ?? ''),
    );
    expect(matchingAlerts).toHaveLength(1);
    expect(telegramRow.contains(matchingAlerts[0])).toBe(true);
  });

  it('honours a structured section.field error key regardless of which section was posted', async () => {
    // Backend returns a dotted key naming Obico even though the caller posted
    // Telegram (e.g. cross-section validation). The dotted key must win over
    // the default-section fallback.
    saveSettingsMock.mockRejectedValueOnce({
      response: { data: { errors: { 'ObicoSettings.enabled': 'Requires an Obico server URL' } } },
    });

    const { container } = await renderPage();
    fireEvent.click(screen.getByLabelText('Telegram Enabled'));

    await act(async () => {
      fireEvent.click(await screen.findByRole('button', { name: /save integrations/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));

    const obicoRow = await waitFor(() => {
      const row = container.querySelector('[data-setting-property="ObicoSettings.enabled"]');
      expect(row).not.toBeNull();
      expect(row!.textContent).toMatch(/requires an obico server url/i);
      return row!;
    });

    const telegramRow = container.querySelector('[data-setting-property="TelegramSettings.enabled"]');
    expect(telegramRow).not.toBeNull();
    expect(telegramRow!.textContent).not.toMatch(/requires an obico server url/i);
    // Belt-and-braces: obicoRow is a distinct element from telegramRow.
    expect(obicoRow).not.toBe(telegramRow);
  });
});
