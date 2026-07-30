import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, within, act, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { HarvestJobDialog } from '../HarvestJobDialog';
import { configurePartsHarvestClient } from '@/services/partsHarvest';

interface StubClient {
  get: ReturnType<typeof vi.fn>;
  post: ReturnType<typeof vi.fn>;
}

function makeStub(): StubClient {
  return { get: vi.fn().mockResolvedValue({ data: [] }), post: vi.fn() };
}

function renderDialog(props: React.ComponentProps<typeof HarvestJobDialog>) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
  return { qc, ...render(<HarvestJobDialog {...props} />, { wrapper }) };
}

function axiosError(status: number, data: unknown): Error {
  return Object.assign(new Error('AxiosError'), {
    isAxiosError: true,
    response: { status, data, headers: {}, statusText: '', config: {} },
    config: {},
  });
}

const baseJob = { id: 'job-1', name: 'Cool Bracket ×4' } as const;

describe('HarvestJobDialog', () => {
  let stub: StubClient;

  beforeEach(() => {
    stub = makeStub();
    configurePartsHarvestClient(stub);
  });

  afterEach(() => {
    configurePartsHarvestClient(null);
  });

  it('is a labelled dialog when opened', async () => {
    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    const dialog = screen.getByRole('dialog');
    expect(dialog).toBeInTheDocument();
    expect(dialog).toHaveAttribute('aria-labelledby');
    // The dialog kicks off a `listParts()` fetch on mount for the manual
    // fallback rows; let that microtask (and its `setParts` state update)
    // settle inside `act(...)` before the test ends, otherwise it resolves
    // after the test body returns and React logs an act() warning.
    await waitFor(() => expect(stub.get).toHaveBeenCalled());
  });

  it('renders already-harvested view when the job carries harvestedAt', async () => {
    renderDialog({
      isOpen: true,
      onClose: vi.fn(),
      job: { ...baseJob, harvestedAt: '2026-01-01T12:00:00Z' },
    });
    expect(screen.getByTestId('harvest-already-harvested')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /confirm harvest/i })).not.toBeInTheDocument();
    // Footer close button — scope by exact name to avoid the modal's own
    // top-right "Close modal" button.
    expect(screen.getByRole('button', { name: 'Close' })).toBeInTheDocument();
    // See note above: let the mount-time `listParts()` fetch settle inside
    // `act(...)` before the test ends.
    await waitFor(() => expect(stub.get).toHaveBeenCalled());
  });

  it('submits a mapped harvest and shows the success view with outputs', async () => {
    const user = userEvent.setup();
    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        binCode: 'BIN-A',
        alreadyHarvested: false,
        adjustments: [
          { id: 'a1', partInventoryId: 'p1', sku: 'SKU-A', binCode: 'BIN-A', delta: 4, resultingBalance: 24, reason: 'harvest', createdAt: '2026-01-01T00:00:00Z' },
        ],
        outputs: [
          { sequence: 1, partInventoryId: 'p1', partSku: 'SKU-A', quantity: 4, actualBinId: 'b1', actualBinCode: 'BIN-A', origin: 'GcodeMapping', overrideApplied: false, createdAt: '2026-01-01T00:00:00Z' },
        ],
      },
    });

    const onHarvested = vi.fn();
    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob, onHarvested });

    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));

    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());
    expect(stub.post).toHaveBeenCalledWith(
      '/job-queue/job-1/harvest',
      expect.objectContaining({ operationKey: expect.any(String) }),
    );
    // SKU-A appears in both outputs and adjustments lists; both are fine.
    const success = screen.getByTestId('harvest-success');
    expect(within(success).getAllByText(/SKU-A/).length).toBeGreaterThan(0);
    expect(onHarvested).toHaveBeenCalledTimes(1);
  });

  it('shows already-harvested style when the server returns alreadyHarvested=true', async () => {
    const user = userEvent.setup();
    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: true,
        adjustments: [],
        outputs: [],
      },
    });
    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());
    const success = screen.getByTestId('harvest-success');
    expect(within(success).getByText(/already harvested/i)).toBeInTheDocument();
  });

  it('renders the wrong-bin step with non-color-only warning and requires an override reason', async () => {
    const user = userEvent.setup();
    stub.post.mockRejectedValueOnce(
      axiosError(409, {
        code: 'wrongBin',
        detail: 'Wrong bin scanned.',
        mismatches: [{ partSku: 'SKU-A', expectedBinCode: 'BIN-1', scannedBinCode: 'BIN-9' }],
      }),
    );

    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));

    await waitFor(() => expect(screen.getByTestId('harvest-wrong-bin')).toBeInTheDocument());
    const region = screen.getByTestId('harvest-wrong-bin');
    // Non-color-only: text describing mismatch
    expect(within(region).getByText(/SKU-A/)).toBeInTheDocument();
    expect(within(region).getByText(/BIN-1/)).toBeInTheDocument();
    expect(within(region).getByText(/BIN-9/)).toBeInTheDocument();

    // V1/V5: focus moves to the override-reason field after the transition.
    const overrideReasonField = screen.getByLabelText(/override reason/i);
    await waitFor(() => expect(overrideReasonField).toHaveFocus());
    expect(overrideReasonField).toHaveAttribute('maxLength', '1000');
    expect(within(region).getByText('0/1000')).toBeInTheDocument();

    const overrideBtn = screen.getByRole('button', { name: /override & harvest/i });
    expect(overrideBtn).toBeDisabled();
    // V3: destructive styling (danger variant token) rather than primary.
    expect(overrideBtn.className).toContain('pf-button-danger-bg');

    const overrideReason = 'Bin relabeled today';
    await user.type(overrideReasonField, overrideReason);
    expect(within(region).getByText(`${overrideReason.length}/1000`)).toBeInTheDocument();
    expect(overrideBtn).toBeEnabled();

    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });

    await user.click(overrideBtn);
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());

    // Second call must carry allowWrongBin + overrideReason and reuse operationKey.
    const first = stub.post.mock.calls[0][1];
    const second = stub.post.mock.calls[1][1];
    expect(second.allowWrongBin).toBe(true);
    expect(second.overrideReason).toBe('Bin relabeled today');
    expect(second.operationKey).toBe(first.operationKey);
  });

  it('switches to the manual outputs form on partMappingRequired and posts explicit outputs', async () => {
    const user = userEvent.setup();
    stub.post.mockRejectedValueOnce(
      axiosError(409, {
        code: 'partMappingRequired',
        detail: 'No mapping.',
        jobId: 'job-1',
        gcodeFileId: null,
        projectFileId: null,
        guidance: 'Enter outputs manually.',
      }),
    );

    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));

    await waitFor(() =>
      expect(screen.getByTestId('harvest-mapping-required')).toBeInTheDocument(),
    );

    const rows = screen.getAllByTestId('harvest-manual-row');
    expect(rows).toHaveLength(1);
    // V1/V5: focus moves to the first SKU input after the transition.
    await waitFor(() =>
      expect(within(rows[0]).getByLabelText(/SKU #1/i)).toHaveFocus(),
    );
    await user.type(within(rows[0]).getByLabelText(/SKU #1/i), 'SKU-Z');

    // H2: explicit outputs require an audit reason — submit stays disabled
    // until one is provided.
    const manualSubmit = screen.getByRole('button', { name: /confirm manual harvest/i });
    expect(manualSubmit).toBeDisabled();
    const manualReasonField = screen.getByLabelText(/audit reason for manual outputs/i);
    expect(manualReasonField).toHaveAttribute('maxLength', '1000');
    expect(screen.getByText('0/1000')).toBeInTheDocument();
    const manualReason = 'Salvaged from failed plate';
    await user.type(manualReasonField, manualReason);
    expect(screen.getByText(`${manualReason.length}/1000`)).toBeInTheDocument();
    expect(manualSubmit).toBeEnabled();

    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });

    await user.click(manualSubmit);

    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());
    const secondCall = stub.post.mock.calls[1][1];
    expect(secondCall.outputs).toEqual([{ sku: 'SKU-Z', quantity: 1 }]);
    expect(secondCall.overrideReason).toBe(manualReason);
  });

  it('accepts a manual output quantity above 100 and posts it unchanged', async () => {
    const user = userEvent.setup();
    stub.post
      .mockRejectedValueOnce(
        axiosError(409, {
          code: 'partMappingRequired',
          detail: 'No mapping.',
          jobId: 'job-1',
          guidance: 'Enter outputs manually.',
        }),
      )
      .mockResolvedValueOnce({
        data: {
          printJobId: 'job-1',
          harvestedAt: '2026-01-01T00:00:00Z',
          alreadyHarvested: false,
          adjustments: [],
          outputs: [],
        },
      });

    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() =>
      expect(screen.getByTestId('harvest-mapping-required')).toBeInTheDocument(),
    );

    const row = screen.getByTestId('harvest-manual-row');
    await user.type(within(row).getByLabelText(/SKU #1/i), 'SKU-Z');
    const quantityField = within(row).getByRole('spinbutton', { name: /quantity/i });
    fireEvent.change(quantityField, { target: { value: '500' } });
    expect(quantityField).toHaveValue(500);
    await user.type(
      screen.getByLabelText(/audit reason for manual outputs/i),
      'Verified high-volume batch',
    );

    await user.click(screen.getByRole('button', { name: /confirm manual harvest/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());
    expect(stub.post.mock.calls[1][1].outputs).toEqual([{ sku: 'SKU-Z', quantity: 500 }]);
  });

  it('supports adding and removing multiple SKU rows in manual mode', async () => {
    const user = userEvent.setup();
    stub.post.mockRejectedValueOnce(
      axiosError(409, {
        code: 'partMappingRequired',
        detail: 'No mapping.',
        jobId: 'job-1',
        guidance: 'Enter outputs manually.',
      }),
    );

    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));

    await waitFor(() =>
      expect(screen.getByTestId('harvest-mapping-required')).toBeInTheDocument(),
    );

    await user.click(screen.getByRole('button', { name: /add another sku/i }));
    let rows = screen.getAllByTestId('harvest-manual-row');
    expect(rows).toHaveLength(2);

    await user.type(within(rows[0]).getByLabelText(/SKU #1/i), 'A');
    await user.type(within(rows[1]).getByLabelText(/SKU #2/i), 'B');

    // Remove row 2
    await user.click(within(rows[1]).getByRole('button', { name: /remove row 2/i }));
    rows = screen.getAllByTestId('harvest-manual-row');
    expect(rows).toHaveLength(1);
  });

  it('shows feature-disabled empty state and offers only a close action', async () => {
    const user = userEvent.setup();
    stub.post.mockRejectedValueOnce(
      axiosError(404, {
        code: 'featureDisabled',
        detail: 'Printed-parts inventory is not enabled.',
      }),
    );
    const onClose = vi.fn();
    renderDialog({ isOpen: true, onClose, job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() =>
      expect(screen.getByTestId('harvest-feature-disabled')).toBeInTheDocument(),
    );
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalled();
  });

  it('shows a generic error step for network failures and reuses the same operationKey on retry', async () => {
    const user = userEvent.setup();
    stub.post.mockRejectedValueOnce(
      Object.assign(new Error('Network Error'), { isAxiosError: true }),
    );
    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));

    await waitFor(() => expect(screen.getByTestId('harvest-error')).toBeInTheDocument());
    const errorBox = screen.getByTestId('harvest-error');
    expect(within(errorBox).getByText(/Harvest failed/i)).toBeInTheDocument();

    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });
    await user.click(screen.getByRole('button', { name: /retry/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());

    const first = stub.post.mock.calls[0][1];
    const second = stub.post.mock.calls[1][1];
    expect(second.operationKey).toBe(first.operationKey);
  });

  it('regenerates operationKey when reopened', async () => {
    const user = userEvent.setup();
    stub.post.mockResolvedValue({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });
    const { rerender } = renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());
    const keyA = stub.post.mock.calls[0][1].operationKey;

    // Close and reopen.
    await act(async () => {
      rerender(<HarvestJobDialog isOpen={false} onClose={vi.fn()} job={baseJob} />);
    });
    await act(async () => {
      rerender(<HarvestJobDialog isOpen={true} onClose={vi.fn()} job={baseJob} />);
    });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() => expect(stub.post).toHaveBeenCalledTimes(2));
    const keyB = stub.post.mock.calls[1][1].operationKey;
    expect(keyA).not.toBe(keyB);
  });

  it('sends binCode, quantityOverride and overrideReason when overriding completed copies', async () => {
    const user = userEvent.setup();
    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });
    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });

    await user.type(screen.getByLabelText(/destination bin/i), 'BIN-7');
    // H4: the checkbox is a copy multiplier, relabelled accordingly.
    await user.click(screen.getByLabelText(/override completed copies/i));

    // Intentional UX gate: mapped quantityOverride is accepted without a reason
    // by the backend, but the client requires one for a stronger audit trail.
    const confirm = screen.getByRole('button', { name: /confirm harvest/i });
    expect(confirm).toBeDisabled();
    const copiesReasonField = screen.getByLabelText(/override reason/i);
    expect(copiesReasonField).toHaveAttribute('maxLength', '1000');
    expect(screen.getByText('0/1000')).toBeInTheDocument();
    const copiesReason = 'Two plates failed mid-run';
    await user.type(copiesReasonField, copiesReason);
    expect(screen.getByText(`${copiesReason.length}/1000`)).toBeInTheDocument();
    expect(confirm).toBeEnabled();

    await user.click(confirm);

    await waitFor(() => expect(stub.post).toHaveBeenCalled());
    const body = stub.post.mock.calls[0][1];
    expect(body.binCode).toBe('BIN-7');
    expect(body.quantityOverride).toBe(1);
    expect(body.overrideReason).toBe(copiesReason);
  });

  it('replays the failed override request (not an empty preview) when retrying after an error (#722 B2)', async () => {
    const user = userEvent.setup();
    // First submit → wrongBin.
    stub.post.mockRejectedValueOnce(
      axiosError(409, {
        code: 'wrongBin',
        detail: 'Wrong bin scanned.',
        mismatches: [{ partSku: 'SKU-A', expectedBinCode: 'BIN-1', scannedBinCode: 'BIN-9' }],
      }),
    );
    renderDialog({ isOpen: true, onClose: vi.fn(), job: baseJob });
    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-wrong-bin')).toBeInTheDocument());

    // Override submit → transient network failure → generic error step.
    await user.type(screen.getByLabelText(/override reason/i), 'Bin relabeled');
    stub.post.mockRejectedValueOnce(
      Object.assign(new Error('Network Error'), { isAxiosError: true }),
    );
    await user.click(screen.getByRole('button', { name: /override & harvest/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-error')).toBeInTheDocument());

    // Retry must replay the override request (allowWrongBin + overrideReason),
    // reusing the same operationKey — not rebuild an empty mapped preview.
    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });
    await user.click(screen.getByRole('button', { name: /retry/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());

    const overrideCall = stub.post.mock.calls[1][1];
    const retryCall = stub.post.mock.calls[2][1];
    expect(retryCall.allowWrongBin).toBe(true);
    expect(retryCall.overrideReason).toBe('Bin relabeled');
    expect(retryCall.operationKey).toBe(overrideCall.operationKey);
  });

  it('keeps the success view mounted and defers the parent refresh until close (#722 H5)', async () => {
    const user = userEvent.setup();
    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-02-02T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [
          { sequence: 1, partInventoryId: 'p1', partSku: 'SKU-A', quantity: 4, actualBinId: 'b1', actualBinCode: 'BIN-A', origin: 'JobSnapshot', overrideApplied: false, createdAt: '2026-02-02T00:00:00Z' },
        ],
      },
    });
    const onHarvested = vi.fn();
    const onCloseAfterSuccess = vi.fn();
    const onClose = vi.fn();
    renderDialog({ isOpen: true, onClose, job: baseJob, onHarvested, onCloseAfterSuccess });

    await user.click(screen.getByRole('button', { name: /confirm harvest/i }));
    await waitFor(() => expect(screen.getByTestId('harvest-success')).toBeInTheDocument());

    // Optimistic callback fired, but the expensive refresh is NOT triggered yet
    // and the success/output details remain on screen for the operator.
    expect(onHarvested).toHaveBeenCalledTimes(1);
    expect(onCloseAfterSuccess).not.toHaveBeenCalled();
    expect(screen.getByTestId('harvest-success')).toBeInTheDocument();
    expect(within(screen.getByTestId('harvest-success')).getByText(/SKU-A/)).toBeInTheDocument();

    // Only on Done (close) does the deferred refresh run.
    await user.click(screen.getByRole('button', { name: 'Done' }));
    expect(onCloseAfterSuccess).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
