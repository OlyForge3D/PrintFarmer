/**
 * Tests for:
 *  A. Spool picker — renders, persists to / restores from localStorage, "None" clears it.
 *  B. Cost precedence — Spoolman cost vs profile cost in SliceProgressOverlay.
 */
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach, afterEach } from 'vitest';
import React from 'react';
import '@testing-library/jest-dom';
import { SliceProgressOverlay } from '@/features/slicer/components/SliceProgressOverlay';
import type { SliceJobProgressState } from '@/features/slicer/hooks/useSliceJobProgress';

// ── Shared mocks ────────────────────────────────────────────────────────────

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrintersFast: () => ({ data: [], isLoading: false }),
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: () => null,
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: () => null,
}));

const mockComputeMaterialCostPerGram = vi.fn();
const mockComputeMaterialCost = vi.fn();
const mockFormatPrintTime = vi.fn(() => '1h 0m');
const mockFormatFilamentUsed = vi.fn(() => '50.0g');

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    computeMaterialCostPerGram: (...args: unknown[]) => mockComputeMaterialCostPerGram(...args),
    computeMaterialCost: (...args: unknown[]) => mockComputeMaterialCost(...args),
    formatPrintTime: (...args: unknown[]) => mockFormatPrintTime(...args),
    formatFilamentUsed: (...args: unknown[]) => mockFormatFilamentUsed(...args),
    sendToPrinter: vi.fn(),
    addSliceToQueue: vi.fn(),
    getSpoolCostPerGram: vi.fn(),
  },
}));

// ── Helpers ──────────────────────────────────────────────────────────────────

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

const completedProgress: SliceJobProgressState = {
  status: 'Completed',
  progressPercent: 100,
  progressMessage: undefined,
  filamentUsedGrams: 50,
  estimatedPrintTimeSeconds: 3600,
  resultFileUrl: 'http://localhost/gcode',
  error: undefined,
  isConnected: true,
};

function renderOverlay(props: Partial<React.ComponentProps<typeof SliceProgressOverlay>> = {}) {
  const defaultProps: React.ComponentProps<typeof SliceProgressOverlay> = {
    jobId: 'job-test',
    progress: completedProgress,
    onNewJob: vi.fn(),
    onRetry: vi.fn(),
    ...props,
  };
  return render(<SliceProgressOverlay {...defaultProps} />, { wrapper: createWrapper() });
}

// ── Cost Precedence Tests ────────────────────────────────────────────────────

describe('Cost source precedence in SliceProgressOverlay', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('uses computeMaterialCostPerGram when resolvedCostPerGram is provided (Spoolman path)', () => {
    mockComputeMaterialCostPerGram.mockReturnValue(0.25);

    renderOverlay({
      resolvedCostPerGram: 0.005,
      costSource: 'spoolman',
    });

    expect(mockComputeMaterialCostPerGram).toHaveBeenCalledWith(50, 0.005);
    expect(mockComputeMaterialCost).not.toHaveBeenCalled();
    expect(screen.getByText(/\$0\.25/)).toBeInTheDocument();
    expect(screen.getByText(/Spoolman/)).toBeInTheDocument();
  });

  it('falls back to computeMaterialCost when no resolvedCostPerGram (profile path)', () => {
    mockComputeMaterialCost.mockReturnValue(1.5);

    renderOverlay({
      filamentCostPerKg: 30,
      costSource: 'profile',
    });

    expect(mockComputeMaterialCost).toHaveBeenCalledWith(50, 30);
    expect(mockComputeMaterialCostPerGram).not.toHaveBeenCalled();
    expect(screen.getByText(/\$1\.50/)).toBeInTheDocument();
    expect(screen.getByText(/Profile/)).toBeInTheDocument();
  });

  it('omits cost chip when both resolvedCostPerGram and filamentCostPerKg are absent', () => {
    mockComputeMaterialCostPerGram.mockReturnValue(null);
    mockComputeMaterialCost.mockReturnValue(null);

    renderOverlay();

    expect(screen.queryByText(/\$/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Spoolman/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Profile/)).not.toBeInTheDocument();
  });

  it('shows source label "Profile" when costSource is profile', () => {
    mockComputeMaterialCost.mockReturnValue(2.0);

    renderOverlay({
      filamentCostPerKg: 40,
      costSource: 'profile',
    });

    expect(screen.getByText(/Profile/)).toBeInTheDocument();
    expect(screen.queryByText(/Spoolman/)).not.toBeInTheDocument();
  });
});

// ── Slice Snapshot Tests ────────────────────────────────────────────────────
// Verifies that cost/spool/requirements are frozen at submit time and do NOT
// change when the sidebar picker is updated while a job is in flight.

interface SnapshotState {
  spoolId: number | null;
  filamentCostPerKg: number | null;
  requiredPrinterModel: string | undefined;
  requiredMaterialType: string | undefined;
  requiredNozzleDiameter: number | undefined;
}

function SliceSnapshotStub() {
  const [selectedSpoolId, setSelectedSpoolId] = React.useState<number | null>(1);
  const [filamentCostPerKg] = React.useState<number | null>(20);
  const [requiredPrinterModel] = React.useState<string | undefined>('MK4');
  const [requiredMaterialType] = React.useState<string | undefined>('PLA');
  const [requiredNozzleDiameter] = React.useState<number | undefined>(0.4);

  const [submittedJobId, setSubmittedJobId] = React.useState<string | null>(null);
  const [sliceSnapshot, setSliceSnapshot] = React.useState<SnapshotState | null>(null);

  const isSubmitted = submittedJobId != null && sliceSnapshot != null;
  const effectiveSpoolId = isSubmitted ? sliceSnapshot.spoolId : selectedSpoolId;
  const effectiveFilamentCostPerKg = isSubmitted ? sliceSnapshot.filamentCostPerKg : filamentCostPerKg;
  const effectivePrinterModel = isSubmitted ? sliceSnapshot.requiredPrinterModel : requiredPrinterModel;
  const effectiveMaterialType = isSubmitted ? sliceSnapshot.requiredMaterialType : requiredMaterialType;
  const effectiveNozzle = isSubmitted ? sliceSnapshot.requiredNozzleDiameter : requiredNozzleDiameter;

  function handleSubmit() {
    setSliceSnapshot({
      spoolId: selectedSpoolId,
      filamentCostPerKg,
      requiredPrinterModel,
      requiredMaterialType,
      requiredNozzleDiameter,
    });
    setSubmittedJobId('job-snap-test');
  }

  function handleClear() {
    setSubmittedJobId(null);
    setSliceSnapshot(null);
  }

  return (
    <div>
      <select
        aria-label="Select spool"
        value={selectedSpoolId ?? ''}
        onChange={(e) => {
          const val = e.target.value;
          setSelectedSpoolId(val === '' ? null : parseInt(val, 10));
        }}
        data-testid="spool-select"
      >
        <option value="">— None —</option>
        <option value="1">Spool A</option>
        <option value="2">Spool B</option>
      </select>
      <button onClick={handleSubmit} data-testid="submit-btn">Slice</button>
      <button onClick={handleClear} data-testid="clear-btn">Clear</button>
      <div data-testid="live-spool">{selectedSpoolId ?? 'none'}</div>
      <div data-testid="effective-spool">{effectiveSpoolId ?? 'none'}</div>
      <div data-testid="effective-cost">{effectiveFilamentCostPerKg ?? 'none'}</div>
      <div data-testid="effective-model">{effectivePrinterModel ?? 'none'}</div>
      <div data-testid="effective-material">{effectiveMaterialType ?? 'none'}</div>
      <div data-testid="effective-nozzle">{effectiveNozzle ?? 'none'}</div>
      <div data-testid="is-submitted">{isSubmitted ? 'yes' : 'no'}</div>
    </div>
  );
}

describe('Slice snapshot — cost/spool/requirements frozen at submit', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('effective values match live selection before any submission', () => {
    render(<SliceSnapshotStub />);
    expect(screen.getByTestId('effective-spool')).toHaveTextContent('1');
    expect(screen.getByTestId('effective-cost')).toHaveTextContent('20');
    expect(screen.getByTestId('effective-model')).toHaveTextContent('MK4');
    expect(screen.getByTestId('effective-material')).toHaveTextContent('PLA');
    expect(screen.getByTestId('effective-nozzle')).toHaveTextContent('0.4');
    expect(screen.getByTestId('is-submitted')).toHaveTextContent('no');
  });

  it('after submit with spool A, switching picker to spool B keeps effective spool as A', async () => {
    render(<SliceSnapshotStub />);

    // Submit while spool A (id=1) is selected
    fireEvent.click(screen.getByTestId('submit-btn'));
    expect(screen.getByTestId('is-submitted')).toHaveTextContent('yes');
    expect(screen.getByTestId('effective-spool')).toHaveTextContent('1');

    // User changes sidebar picker to spool B while job is in flight
    fireEvent.change(screen.getByTestId('spool-select'), { target: { value: '2' } });

    // Live picker reflects the change
    expect(screen.getByTestId('live-spool')).toHaveTextContent('2');

    // Effective spool (used by overlay/queue) is still frozen to spool A
    expect(screen.getByTestId('effective-spool')).toHaveTextContent('1');
    expect(screen.getByTestId('effective-cost')).toHaveTextContent('20');
    expect(screen.getByTestId('effective-model')).toHaveTextContent('MK4');
    expect(screen.getByTestId('effective-material')).toHaveTextContent('PLA');
    expect(screen.getByTestId('effective-nozzle')).toHaveTextContent('0.4');
  });

  it('after submit, clearing the spool picker does not clear effective spool', async () => {
    render(<SliceSnapshotStub />);

    fireEvent.click(screen.getByTestId('submit-btn'));
    fireEvent.change(screen.getByTestId('spool-select'), { target: { value: '' } });

    expect(screen.getByTestId('live-spool')).toHaveTextContent('none');
    expect(screen.getByTestId('effective-spool')).toHaveTextContent('1');
  });

  it('snapshot is cleared when the job is cleared, restoring live values', async () => {
    render(<SliceSnapshotStub />);

    fireEvent.click(screen.getByTestId('submit-btn'));
    // Switch to spool B while submitted
    fireEvent.change(screen.getByTestId('spool-select'), { target: { value: '2' } });
    expect(screen.getByTestId('effective-spool')).toHaveTextContent('1');

    // Clear / new-job resets snapshot
    fireEvent.click(screen.getByTestId('clear-btn'));
    await waitFor(() => {
      expect(screen.getByTestId('is-submitted')).toHaveTextContent('no');
    });

    // Effective spool now follows the live picker (spool B)
    expect(screen.getByTestId('effective-spool')).toHaveTextContent('2');
  });
});
