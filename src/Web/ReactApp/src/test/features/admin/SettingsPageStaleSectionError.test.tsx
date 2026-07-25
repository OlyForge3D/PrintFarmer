import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * Regression guard for the stale section-level error defect (#941).
 *
 * Section-level errors (memberless backend `ValidationException`s, surfaced as
 * `errors: { [sectionKey]: reason }`) are held in `sectionErrors` and rendered
 * inline on each section card. Unlike `fieldErrors`, they have no self-healing
 * path, so the previous fix's partial-failure merge —
 * `setSectionErrors((prev) => ({ ...prev, ...perSectionMessages }))` — let a
 * section that SUCCEEDED on a later save keep the error from an earlier failed
 * attempt, because successful sections contribute nothing to
 * `perSectionMessages` and their old entry survived the spread over `prev`.
 *
 * This test drives the partial-failure path: first save fails both sections,
 * second save (same dirty state, no edits) succeeds one and fails the other.
 * The succeeded section must no longer show its section-level alert.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

const OBICO_REASON = 'Obico server URL is required to enable Obico.';
const TELEGRAM_REASON = 'Bot token is required to enable Telegram.';

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

function cardFor(container: HTMLElement, sectionKey: string): HTMLElement {
  const row = container.querySelector<HTMLElement>(`[data-setting-property="${sectionKey}.enabled"]`);
  expect(row, `could not locate the ${sectionKey} field row`).not.toBeNull();
  const card = row!.closest('.bg-pf-panel');
  expect(card, `could not locate the ${sectionKey} Card container`).not.toBeNull();
  return card as HTMLElement;
}

function cardHasAlert(card: HTMLElement, reason: string): boolean {
  return Array.from(card.querySelectorAll('[role="alert"]')).some((el) =>
    el.textContent?.includes(reason),
  );
}

describe('SettingsPage — stale section-level error on partial failure', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
  });

  it('clears a section-level error for a section that succeeds on a later save while another still fails', async () => {
    // Both sections are posted on every save (both stay dirty after a failure).
    // Attempt 1: both reject with memberless section errors.
    // Attempt 2: Obico now resolves, Telegram keeps failing.
    const callCounts: Record<string, number> = {};
    saveSettingsMock.mockImplementation(async (sectionKey: string) => {
      callCounts[sectionKey] = (callCounts[sectionKey] ?? 0) + 1;
      if (sectionKey === 'TelegramSettings') {
        throw {
          response: {
            data: { message: TELEGRAM_REASON, errors: { TelegramSettings: TELEGRAM_REASON } },
          },
        };
      }
      if (sectionKey === 'ObicoSettings') {
        if (callCounts[sectionKey] === 1) {
          throw {
            response: {
              data: { message: OBICO_REASON, errors: { ObicoSettings: OBICO_REASON } },
            },
          };
        }
        return; // second attempt succeeds
      }
    });

    const { container } = await renderPage();

    // Dirty both sections so both are posted on each save.
    fireEvent.click(screen.getByLabelText('Obico Enabled'));
    fireEvent.click(screen.getByLabelText('Telegram Enabled'));

    const saveBtn = await screen.findByRole('button', { name: /save integrations/i });

    // First save — both fail, both cards show their section-level alerts.
    await act(async () => {
      fireEvent.click(saveBtn);
    });
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(2));

    await waitFor(() => {
      expect(cardHasAlert(cardFor(container, 'ObicoSettings'), OBICO_REASON)).toBe(true);
      expect(cardHasAlert(cardFor(container, 'TelegramSettings'), TELEGRAM_REASON)).toBe(true);
    });

    // Second save — no edits between attempts, so the succeeded section is NOT
    // masked by the field-edit self-healing path. This exercises the partial-
    // failure merge in handleSave directly.
    await act(async () => {
      fireEvent.click(await screen.findByRole('button', { name: /save integrations/i }));
    });
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(4));

    // Obico succeeded this round: its stale section-level error must be gone.
    await waitFor(() => {
      expect(cardHasAlert(cardFor(container, 'ObicoSettings'), OBICO_REASON)).toBe(false);
    });

    // Telegram failed again: its section-level error remains.
    expect(cardHasAlert(cardFor(container, 'TelegramSettings'), TELEGRAM_REASON)).toBe(true);
  });
});
