import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * #1013 — one save bar per page, not one per group.
 *
 * The fixture below is the shape that made the old design fail: three groups,
 * each with its own dirty state, rendered on one page. Under the per-group bar
 * that produced three sticky bars stacked down the page, two of which read
 * "No unsaved changes", and none of which said what its own Save button would
 * write.
 *
 * What these tests pin down is the part that is easy to get wrong when
 * collapsing them: the groups must stay independent underneath. A save that
 * fails for one group has to leave that group — and only that group — dirty,
 * and has to say so by name.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

function numberProp(name: string, label: string) {
  return {
    name,
    type: 'number',
    attributes: [],
    display: { name: label, inputType: 'Number', minValue: 1, maxValue: 100000 },
  };
}

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        key: 'SystemLogSettings',
        className: 'SystemLogSettings',
        displayName: 'System Log',
        description: 'Log retention config.',
        group: 'System',
        order: 1,
        properties: [
          numberProp('retentionDays', 'Retention Days'),
          numberProp('maxSizeMb', 'Max Size Mb'),
        ],
      },
      {
        key: 'DiscoverySettings',
        className: 'DiscoverySettings',
        displayName: 'Network Discovery',
        description: 'Scan behaviour.',
        group: 'Networking',
        order: 1,
        properties: [numberProp('scanTimeoutSeconds', 'Scan Timeout Seconds')],
      },
      {
        key: 'DatabaseSettings',
        className: 'DatabaseSettings',
        displayName: 'Database',
        description: 'Storage behaviour.',
        group: 'Storage',
        order: 1,
        properties: [numberProp('backupIntervalHours', 'Backup Interval Hours')],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
      { key: 'Networking', displayName: 'Networking', order: 2 },
      { key: 'Storage', displayName: 'Storage', order: 3 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLogSettings: { retentionDays: 30, maxSizeMb: 500 },
      DiscoverySettings: { scanTimeoutSeconds: 5 },
      DatabaseSettings: { backupIntervalHours: 24 },
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
import { SettingsFooterSlotContext } from '@/features/settings/components/settingsFooterSlotContext';

async function renderPage() {
  const result = render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    expect(screen.getByLabelText('Scan Timeout Seconds')).toBeInTheDocument();
    expect(screen.getByLabelText('Backup Interval Hours')).toBeInTheDocument();
  });
  return result;
}

function edit(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

/** The bar's message line. Read by test id so the assertion survives rewording. */
function barSummary(): string {
  return screen.getByTestId('admin-save-bar').querySelector('p')?.textContent?.trim() ?? '';
}

describe('SettingsPage — single page-level save bar (#1013)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
    saveSettingsMock.mockResolvedValue(undefined);
  });

  it('renders no bar while every group is clean', async () => {
    await renderPage();
    expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0);
  });

  it('renders exactly one bar no matter how many groups are dirty', async () => {
    await renderPage();

    edit('Retention Days', '45');
    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(1));

    edit('Scan Timeout Seconds', '9');
    edit('Backup Interval Hours', '12');
    await waitFor(() => expect(barSummary()).toContain('3 sections'));

    // The count is the point: three dirty groups used to mean three bars.
    expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: /save changes/i })).toHaveLength(1);
  });

  it('counts edited fields, not edited sections', async () => {
    await renderPage();

    edit('Retention Days', '45');
    await waitFor(() => expect(barSummary()).toBe('1 change in System Log'));

    // Same section, second field. A section-based count would still say "1".
    edit('Max Size Mb', '900');
    await waitFor(() => expect(barSummary()).toBe('2 changes in System Log'));
  });

  it('names both sections when two are dirty', async () => {
    await renderPage();

    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');

    await waitFor(() => {
      expect(barSummary()).toBe('2 changes in System Log and Network Discovery');
    });
  });

  it('counts sections past two and keeps the full list in a tooltip', async () => {
    await renderPage();

    edit('Retention Days', '45');
    edit('Max Size Mb', '900');
    edit('Scan Timeout Seconds', '9');
    edit('Backup Interval Hours', '12');

    await waitFor(() => expect(barSummary()).toBe('4 changes in 3 sections'));
    expect(screen.getByTitle('System Log, Network Discovery, Database')).toBeInTheDocument();
  });

  it('reads in page order, not in the order the user happened to edit', async () => {
    await renderPage();

    // Edited bottom-up. The bar must still read top-down.
    edit('Backup Interval Hours', '12');
    edit('Retention Days', '45');

    await waitFor(() => expect(barSummary()).toBe('2 changes in System Log and Database'));
  });

  it('saves every dirty group and reports what was written', async () => {
    await renderPage();

    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');

    const saveBtn = await screen.findByRole('button', { name: /save changes/i });
    await act(async () => {
      fireEvent.click(saveBtn);
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(2));
    expect(saveSettingsMock.mock.calls.map((c) => c[0])).toEqual([
      'SystemLogSettings',
      'DiscoverySettings',
    ]);
    // Untouched group is never written.
    expect(saveSettingsMock.mock.calls.map((c) => c[0])).not.toContain('DatabaseSettings');

    await waitFor(() => {
      expect(toastSuccessMock).toHaveBeenCalledWith('Saved System Log, Network Discovery');
    });
    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0));
  });

  it('leaves only the failed group dirty and names it', async () => {
    saveSettingsMock.mockImplementation((key: string) => (
      key === 'DiscoverySettings'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(undefined)
    ));

    await renderPage();
    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');
    edit('Backup Interval Hours', '12');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(3));

    // Both halves are named: what landed, and what did not.
    await waitFor(() => {
      expect(toastErrorMock).toHaveBeenCalledWith(
        'Saved System Log, Database. Failed to save Network Discovery',
      );
    });

    // The bar survives, narrowed to exactly the group that still needs saving.
    await waitFor(() => expect(barSummary()).toBe('1 change in Network Discovery'));

    // The two that succeeded are clean and hold their new values.
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('45');
    expect((screen.getByLabelText('Backup Interval Hours') as HTMLInputElement).value).toBe('12');
    // The one that failed keeps the user's edit so they can retry.
    expect((screen.getByLabelText('Scan Timeout Seconds') as HTMLInputElement).value).toBe('9');
  });

  it('retrying after a partial failure writes only what is left', async () => {
    saveSettingsMock.mockImplementation((key: string) => (
      key === 'DiscoverySettings'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(undefined)
    ));

    await renderPage();
    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(2));

    saveSettingsMock.mockReset();
    saveSettingsMock.mockResolvedValue(undefined);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    // The already-saved group is not written a second time.
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(saveSettingsMock).toHaveBeenCalledWith('DiscoverySettings', { scanTimeoutSeconds: 9 });
    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0));
  });

  it('discard reverts every dirty group at once', async () => {
    await renderPage();

    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');
    edit('Backup Interval Hours', '12');
    await waitFor(() => expect(barSummary()).toContain('3 sections'));

    fireEvent.click(screen.getByRole('button', { name: /discard/i }));

    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0));
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('30');
    expect((screen.getByLabelText('Scan Timeout Seconds') as HTMLInputElement).value).toBe('5');
    expect((screen.getByLabelText('Backup Interval Hours') as HTMLInputElement).value).toBe('24');
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });

  it('guards unload while any group is dirty, and stops once all are clean', async () => {
    const addSpy = vi.spyOn(window, 'addEventListener');
    const removeSpy = vi.spyOn(window, 'removeEventListener');
    const count = (spy: typeof addSpy) =>
      spy.mock.calls.filter((c) => c[0] === 'beforeunload').length;

    try {
      await renderPage();
      expect(count(addSpy)).toBe(0);

      // A dirty group anywhere on the page arms the guard.
      edit('Backup Interval Hours', '12');
      await waitFor(() => expect(count(addSpy)).toBeGreaterThan(0));

      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
      });

      await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0));
      await waitFor(() => expect(count(removeSpy)).toBeGreaterThan(0));
    } finally {
      addSpy.mockRestore();
      removeSpy.mockRestore();
    }
  });

  it('blocks the save and explains why when a value fails validation', async () => {
    await renderPage();

    // maxValue is 100000; anything past it is rejected before the wire.
    edit('Retention Days', '999999');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    expect(saveSettingsMock).not.toHaveBeenCalled();
    // The bar says why the save was blocked; the field itself says what is wrong.
    await waitFor(() => {
      const bar = screen.getByTestId('admin-save-bar');
      expect(bar.querySelector('[role="alert"]')).toHaveTextContent(/fix validation errors/i);
    });
    expect(screen.getAllByTestId('admin-save-bar')).toHaveLength(1);
    // The bar stays up: the change is still unsaved and still needs a decision.
    expect(barSummary()).toBe('1 change in System Log');
  });
});

describe('SettingsPage — the save bar is docked outside the scrollport (Vasquez #1)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    saveSettingsMock.mockResolvedValue(undefined);
  });

  /**
   * The bar used to be `position: sticky; bottom: 0` inside the settings scroll
   * pane, and it never once pinned. The pane could not scroll — its `flex-1
   * min-h-0` chain bottomed out in `PageTemplate`'s `min-h-full`, a minimum
   * rather than a bound — so the bar simply flowed to the end of the content,
   * measured 249px below the fold on a 1440x900 System Config. Dirty edits
   * offered a Save the user could not see.
   *
   * jsdom has no layout, so this cannot assert geometry. What it can assert is
   * the structural property that made the bug possible and that the fix
   * removes: the bar must not live inside the scrolled content. Placement is
   * now the shell's, delivered through a portal.
   */
  it('portals the bar into the shell footer slot, out of the page content', async () => {
    const slot = document.createElement('div');
    slot.setAttribute('data-test-footer-slot', '');
    document.body.appendChild(slot);

    const { container } = render(
      <MemoryRouter>
        <SettingsFooterSlotContext.Provider value={slot}>
          <SettingsPage />
        </SettingsFooterSlotContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    });
    edit('Retention Days', '45');

    const bar = await screen.findByTestId('admin-save-bar');
    expect(slot.contains(bar)).toBe(true);
    // The decisive half: it is *not* in the page's own flow, which is what the
    // shell puts inside the scrollport.
    expect(container.contains(bar)).toBe(false);

    slot.remove();
  });

  /**
   * Standalone mounts have no shell and therefore no slot. Falling through to
   * nothing would delete the only way to commit a change, so the bar has to
   * stay in flow when there is nowhere to dock it.
   */
  it('keeps the bar in flow when no footer slot is provided', async () => {
    const { container } = render(
      <MemoryRouter>
        <SettingsPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    });
    edit('Retention Days', '45');

    const bar = await screen.findByTestId('admin-save-bar');
    expect(container.contains(bar)).toBe(true);
  });

  /** Exactly one bar either way — never one in flow *and* one docked. */
  it('never renders two bars when a slot is present', async () => {
    const slot = document.createElement('div');
    document.body.appendChild(slot);
    render(
      <MemoryRouter>
        <SettingsFooterSlotContext.Provider value={slot}>
          <SettingsPage />
        </SettingsFooterSlotContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    });
    edit('Retention Days', '45');

    await waitFor(() => {
      expect(screen.getAllByTestId('admin-save-bar')).toHaveLength(1);
    });
    slot.remove();
  });
});

describe('SettingsPage — a partial failure settles only what saved (Hicks #2)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
  });

  /**
   * Cross-group: each band keeps its own dirty state, so a band that 400s
   * cannot hold a sibling hostage. That was already true — this pins it, since
   * nothing proved it before.
   *
   * The *intra*-group case (one band holding several cards, some saving and
   * some not) cannot be staged here: this fixture gives every group exactly one
   * section. `useDirtyState.acceptKeys` owns that half and is tested directly
   * in `useDirtyState.test.tsx`.
   */
  it('re-sends only the group that failed', async () => {
    saveSettingsMock.mockImplementation((key: string) =>
      key === 'DiscoverySettings'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(undefined),
    );

    await renderPage();
    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');
    edit('Backup Interval Hours', '12');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(3));
    saveSettingsMock.mockClear();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    // Assert after `act` has flushed the whole sequential loop. A `waitFor` on
    // "has been called" would resolve on the *first* request and pass even if
    // two more followed it.
    expect(saveSettingsMock.mock.calls.map((c) => c[0])).toEqual(['DiscoverySettings']);
  });

  /** And once it succeeds, nothing is left over. */
  it('clears the bar when the retry succeeds', async () => {
    let failing = true;
    saveSettingsMock.mockImplementation((key: string) =>
      failing && key === 'DiscoverySettings'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(undefined),
    );

    await renderPage();
    edit('Retention Days', '45');
    edit('Scan Timeout Seconds', '9');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });
    await waitFor(() => expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument());

    failing = false;
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    await waitFor(() => {
      expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
    });
  });
});

/**
 * The success path must not discard an edit the user made while the request was
 * in flight.
 *
 * `handleSave` snapshots `state.values` when Save is clicked and awaits the POST.
 * The old code then called `markPristine(state.values)` on that snapshot, and
 * `markPristine` writes *both* the baseline and the working values — so a keystroke
 * landing during the request was overwritten by the click-time value and the field
 * silently reverted. `acceptKeys` moves only the baseline, so the newer edit
 * survives and correctly stays dirty.
 */
describe('SettingsPage — an edit made during a save survives it (Hicks #1)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
  });

  it('keeps the newer value and leaves the bar dirty', async () => {
    let release: () => void = () => {};
    const inFlight = new Promise<void>((resolve) => {
      release = resolve;
    });
    saveSettingsMock.mockImplementation(() => inFlight.then(() => undefined));

    await renderPage();
    edit('Retention Days', '45');

    // Click Save, then type again before the request resolves.
    let saveDone: Promise<unknown>;
    await act(async () => {
      saveDone = Promise.resolve();
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
      await saveDone;
    });
    edit('Retention Days', '99');

    await act(async () => {
      release();
      await inFlight;
    });

    // The field keeps what the user actually typed last...
    expect(screen.getByLabelText('Retention Days')).toHaveValue(99);
    // ...and the page still knows that value is unsaved.
    await waitFor(() => {
      expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument();
    });
  });
});

/**
 * A group holds several sections and each is its own request, so "the group
 * failed" almost always means "one section failed and the others landed". The
 * bar has to say both, or the user re-sends writes that already succeeded.
 *
 * This needs two sections sharing one group, which the fixture above cannot
 * express — every section there is its own group, so a "partial" failure is
 * really one whole group failing and another whole group succeeding, a path
 * that was already correct. The real case lives in
 * `SettingsPartialFailure.test.tsx` with a fixture built for it.
 */
