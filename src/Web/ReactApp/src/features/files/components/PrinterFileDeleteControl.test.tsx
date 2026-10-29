import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterFileDeleteControl, type PrinterFileDeleteIntent } from '@/features/files/components/PrinterFileDeleteControl';
import type { PrinterFileDeleteOperation } from '@/types/api';

const mocks = vi.hoisted(() => ({ prepare: vi.fn(), get: vi.fn(), confirm: vi.fn(), reconcile: vi.fn() }));
vi.mock('@/services/api', () => ({
  apiClient: {
    prepareIndexedPrinterFileDelete: mocks.prepare, getPrinterFileDeleteOperation: mocks.get,
    confirmPrinterFileDelete: mocks.confirm, reconcilePrinterFileDelete: mocks.reconcile,
  },
}));

const pending: PrinterFileDeleteOperation = {
  id: 'delete-intent', revision: 1, state: 'AwaitingDeleteConfirmation', locationId: 'location',
  sourcePrinterId: 'source', storageRoot: 'gcodes', relativePath: 'Parts/exact.gcode',
  observationRevision: 7, configurationGeneration: 4, guarantee: 'CurrentExactPath',
  confirmationBinding: 'actor-operation-exact-source-binding', currentPathRaceAccepted: false,
  deleteSentAtUtc: null, outcome: 'AwaitingConfirmation', reasonCode: null,
};
const intent: PrinterFileDeleteIntent = {
  key: 'stable-delete-key',
  entry: {
    id: 'location', printerId: 'source', printerName: 'Source printer', storageRoot: 'gcodes',
    relativePath: 'Parts/exact.gcode', displayName: 'exact.gcode', sizeBytes: 12,
    modifiedValue: null, observationRevision: 7, configurationGeneration: 4,
    availability: 'Available', lastSeenAtUtc: '2026-10-25T12:00:00Z',
    lastCompleteScanAtUtc: null, isStale: true, localCopies: [],
  },
};

function mount(options: { intent?: PrinterFileDeleteIntent; resume?: boolean; allowed?: boolean } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[options.resume ? '/files?source=printers&deleteOperation=delete-intent' : '/files?source=printers']}>
        <PrinterFileDeleteControl intent={options.intent ?? null} onClose={vi.fn()}
          canDelete={options.allowed ?? true} printers={[{ id: 'source', name: 'Source printer' }]} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('Exact-source remote Delete', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mocks.get.mockResolvedValue(pending);
    mocks.prepare.mockResolvedValue(pending);
    mocks.confirm.mockResolvedValue({ ...pending, state: 'Blocked', outcome: 'Blocked', reasonCode: 'source_changed' });
    mocks.reconcile.mockResolvedValue({ ...pending, state: 'ReconcilingDelete', outcome: 'OutcomeUnknown' });
  });

  it('prepares only after intent and reuses its exact key after response loss', async () => {
    mocks.prepare.mockRejectedValueOnce(new Error('Response lost')).mockResolvedValueOnce(pending);
    const user = userEvent.setup();
    mount({ intent });
    expect(screen.getByText('gcodes/Parts/exact.gcode')).toBeInTheDocument();
    expect(mocks.prepare).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Review exact delete' }));
    await user.click(await screen.findByRole('button', { name: 'Recover same delete intent' }));
    await waitFor(() => expect(mocks.prepare).toHaveBeenCalledTimes(2));
    expect(mocks.prepare.mock.calls).toEqual([
      ['location', 7, 'stable-delete-key'], ['location', 7, 'stable-delete-key'],
    ]);
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  it('requires independent irreversible and weak-path-race consents, bound to the exact operation', async () => {
    const user = userEvent.setup();
    mount({ resume: true });
    const confirm = await screen.findByRole('button', { name: 'Confirm irreversible delete' });
    expect(confirm).toBeDisabled();
    expect(screen.getByText('Source printer')).toBeInTheDocument();
    expect(screen.getByText('gcodes/Parts/exact.gcode')).toBeInTheDocument();
    await user.click(screen.getByRole('checkbox', { name: /I accept irreversible deletion/ }));
    expect(confirm).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /I accept the current-path replacement race/ }));
    await user.click(confirm);
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledWith('delete-intent', {
      confirmationBinding: pending.confirmationBinding,
      acceptIrreversibleDelete: true, acceptCurrentPathRace: true,
    }));
    expect(screen.queryByText(/printer acknowledged this source deletion/i)).not.toBeInTheDocument();
  });

  it('native conditional delete still needs irreversible consent but not weak-race consent', async () => {
    mocks.get.mockResolvedValue({ ...pending, guarantee: 'NativeConditional' });
    const user = userEvent.setup();
    mount({ resume: true });
    const confirm = await screen.findByRole('button', { name: 'Confirm irreversible delete' });
    expect(confirm).toBeDisabled();
    expect(screen.queryByRole('checkbox', { name: /replacement race/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('checkbox', { name: /I accept irreversible deletion/ }));
    await user.click(confirm);
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledWith('delete-intent',
      expect.objectContaining({ acceptCurrentPathRace: false })));
  });

  it('reconnect never inherits consent or automatically confirms', async () => {
    const user = userEvent.setup();
    const first = mount({ resume: true });
    await user.click(await screen.findByRole('checkbox', { name: /I accept irreversible deletion/ }));
    await user.click(screen.getByRole('checkbox', { name: /I accept the current-path replacement race/ }));
    first.unmount();
    mount({ resume: true });
    expect(await screen.findByRole('button', { name: 'Confirm irreversible delete' })).toBeDisabled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  it('unknown outcome offers only read-only reconciliation and never deletion replay', async () => {
    mocks.get.mockResolvedValue({ ...pending, state: 'SendingDelete', outcome: 'OutcomeUnknown',
      reasonCode: 'outcome_unknown_observed_absent' });
    const user = userEvent.setup();
    mount({ resume: true });
    await user.click(await screen.findByRole('button', { name: 'Read-only reconcile' }));
    await waitFor(() => expect(mocks.reconcile).toHaveBeenCalledWith('delete-intent'));
    expect(screen.getByText('Outcome unknown - do not resend')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Confirm irreversible delete|Retry/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/printer acknowledged this source deletion/i)).not.toBeInTheDocument();
    expect(mocks.confirm).not.toHaveBeenCalled();
    expect(mocks.prepare).not.toHaveBeenCalled();
  });

  it('a lost confirmation response disables resend even if the last cached status still awaited consent', async () => {
    mocks.confirm.mockRejectedValue(new Error('Response lost'));
    const user = userEvent.setup();
    mount({ resume: true });
    await user.click(await screen.findByRole('checkbox', { name: /I accept irreversible deletion/ }));
    await user.click(screen.getByRole('checkbox', { name: /I accept the current-path replacement race/ }));
    await user.click(screen.getByRole('button', { name: 'Confirm irreversible delete' }));
    expect(await screen.findByText('Outcome unknown - do not resend')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Confirm irreversible delete' })).not.toBeInTheDocument();
    expect(mocks.confirm).toHaveBeenCalledTimes(1);
  });

  it('only acknowledged Deleted is shown as success', async () => {
    mocks.get.mockResolvedValue({ ...pending, state: 'Deleted', outcome: 'Acknowledged' });
    mount({ resume: true });
    expect(await screen.findByText(/printer acknowledged this source deletion/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Confirm irreversible delete' })).not.toBeInTheDocument();
  });

  it('does not expose or poll delete state without permission', async () => {
    mount({ resume: true, allowed: false });
    await Promise.resolve();
    expect(mocks.get).not.toHaveBeenCalled();
    expect(screen.queryByText(/Remote delete/)).not.toBeInTheDocument();
  });
});
