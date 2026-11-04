import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BedClearEvidencePanel } from '@/features/printers/components/BedClearEvidencePanel';
import type { BedClearEvaluationDto } from '@/types/api';

const { service, auth, toast } = vi.hoisted(() => ({
  service: {
    getBedClearEvaluations: vi.fn(),
    getBedClearDetectorMetrics: vi.fn(),
    getBedClearEvaluationImage: vi.fn(),
    captureBedClearEvaluation: vi.fn(),
    labelBedClearEvaluation: vi.fn(),
    deleteBedClearEvaluation: vi.fn(),
  },
  auth: { hasPermission: vi.fn(() => true), user: { id: 'reviewer' } },
  toast: { success: vi.fn(), error: vi.fn() },
}));

vi.mock('@/services/cameraService', () => ({ cameraService: service }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => auth }));
vi.mock('sonner', () => ({ toast }));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({
  ConfirmationModal: ({ isOpen, onConfirm }: { isOpen: boolean; onConfirm: () => void }) =>
    isOpen ? <button type="button" onClick={onConfirm}>Confirm deletion</button> : null,
}));

const sample = (overrides: Partial<BedClearEvaluationDto> = {}): BedClearEvaluationDto => ({
  id: 'sample-1',
  printerId: 'printer-1',
  cameraId: 'camera-1',
  printJobId: null,
  bedTypeId: 'plate-1',
  sourceAcknowledgementId: 'ack-1',
  acknowledgedBySubject: 'capturer',
  acknowledgedAtUtc: '2026-11-04T18:30:00Z',
  capturedAtUtc: '2026-11-04T18:30:01Z',
  toolheadX: 10,
  toolheadY: 20,
  toolheadZ: 30,
  state: 'Complete',
  verdict: 'Clear',
  score: 1,
  reasonCode: null,
  groundTruth: null,
  groundTruthBySubject: null,
  groundTruthAtUtc: null,
  isActiveReference: false,
  isReferenceEligible: true,
  hasImage: true,
  ...overrides,
});

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  render(<QueryClientProvider client={client}><BedClearEvidencePanel printerId="printer-1" /></QueryClientProvider>);
  return invalidate;
}

describe('BedClearEvidencePanel', () => {
  afterEach(() => vi.unstubAllGlobals());

  beforeEach(() => {
    vi.clearAllMocks();
    auth.hasPermission.mockReturnValue(true);
    auth.user.id = 'reviewer';
    service.getBedClearEvaluations.mockResolvedValue([sample()]);
    service.getBedClearEvaluationImage.mockResolvedValue(new Blob(['image']));
    service.getBedClearDetectorMetrics.mockResolvedValue({
      sampleCount: 1,
      independentlyLabeledCount: 0,
      agreementRate: null,
      independentlyLabeledOccupiedCount: 0,
      falseClearCount: 0,
      falseClearRate: null,
    });
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = vi.fn(() => 'blob:sample-image');
      static revokeObjectURL = vi.fn();
    });
  });

  it('does not present missing occupied evidence as a zero false-clear rate', async () => {
    renderPanel();

    expect(await screen.findByText(/False-clear rate Unavailable/)).toBeInTheDocument();
    expect(screen.getByText(/until at least one independently labeled occupied sample/)).toBeInTheDocument();
    expect(screen.queryByText(/False-clear rate 0.0%/)).not.toBeInTheDocument();
    expect(screen.getByText(/Detector output never changes dispatch/)).toBeInTheDocument();
  });

  it('hides the model prediction before labeling and saves the independent visual label', async () => {
    const user = userEvent.setup();
    const invalidate = renderPanel();

    await user.click(await screen.findByRole('button', { name: 'Review image' }));
    fireEvent.load(await screen.findByRole('img', { name: /Camera image captured/ }));
    expect(screen.queryByText('Detector: Clear')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Occupied' }));

    await waitFor(() => expect(service.labelBedClearEvaluation).toHaveBeenCalledWith('sample-1', 'Occupied'));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['bed-clear-evaluations', 'printer-1'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['bed-clear-detector-metrics', 'printer-1'] });
  });

  it('handles null fields omitted by the API without NaN metrics or similarity', async () => {
    const evaluation = sample({ verdict: 'Unknown', groundTruth: 'Occupied', groundTruthBySubject: 'reviewer' });
    delete evaluation.score;
    delete evaluation.reasonCode;
    service.getBedClearEvaluations.mockResolvedValue([evaluation]);
    service.getBedClearDetectorMetrics.mockResolvedValue({
      sampleCount: 1,
      independentlyLabeledCount: 0,
      independentlyLabeledOccupiedCount: 0,
      falseClearCount: 0,
    });
    renderPanel();

    expect(await screen.findByText(/False-clear rate Unavailable/)).toBeInTheDocument();
    expect(screen.getByText(/Agreement Unavailable/)).toBeInTheDocument();
    expect(screen.getByText(/until at least one independently labeled occupied sample/)).toBeInTheDocument();
    expect(await screen.findByText('Detector: Unknown')).toBeInTheDocument();
    expect(screen.queryByText(/NaN|Similarity/)).not.toBeInTheDocument();
  });

  it('does not offer self-labeling to the capturing operator', async () => {
    auth.user.id = 'capturer';
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Review image' }));
    fireEvent.load(await screen.findByRole('img', { name: /Camera image captured/ }));
    expect(screen.queryByRole('button', { name: 'Bed clear' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Occupied' })).not.toBeInTheDocument();
  });

  it('only fetches images on demand and requires an image to load before labeling', async () => {
    const user = userEvent.setup();
    renderPanel();
    const review = await screen.findByRole('button', { name: 'Review image' });
    expect(service.getBedClearEvaluationImage).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Occupied' })).not.toBeInTheDocument();

    await user.click(review);
    const image = await screen.findByRole('img', { name: /Camera image captured/ });
    expect(screen.queryByRole('button', { name: 'Occupied' })).not.toBeInTheDocument();
    fireEvent.load(image);
    expect(screen.getByRole('button', { name: 'Occupied' })).toBeInTheDocument();
  });

  it('requests occupied evaluation evidence through shadow capture, not readiness', async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.click(await screen.findByRole('button', { name: 'Capture shadow sample (no dispatch)' }));

    await waitFor(() => expect(service.captureBedClearEvaluation).toHaveBeenCalledWith('printer-1'));
    expect(service.labelBedClearEvaluation).not.toHaveBeenCalled();
    expect(toast.success).toHaveBeenCalledWith('Shadow sample requested. Readiness and dispatch are unchanged.');
  });

  it('deletes a reference only after confirmation', async () => {
    const user = userEvent.setup();
    service.getBedClearEvaluations.mockResolvedValue([sample({
      isActiveReference: true,
      groundTruth: 'Clear',
      groundTruthBySubject: 'reviewer',
    })]);
    renderPanel();

    await user.click(await screen.findByRole('button', { name: 'Delete sample/reference' }));
    expect(service.deleteBedClearEvaluation).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Confirm deletion' }));

    await waitFor(() => expect(service.deleteBedClearEvaluation).toHaveBeenCalledWith('sample-1'));
  });

  it('does not request evidence without queue read permission', () => {
    auth.hasPermission.mockReturnValue(false);
    renderPanel();

    expect(screen.getByText(/Queue read permission is required/)).toBeInTheDocument();
    expect(service.getBedClearEvaluations).not.toHaveBeenCalled();
    expect(service.getBedClearDetectorMetrics).not.toHaveBeenCalled();
  });

  it('reports API failures instead of presenting an empty successful dataset', async () => {
    service.getBedClearEvaluations.mockRejectedValue(new Error('offline'));
    service.getBedClearDetectorMetrics.mockRejectedValue(new Error('offline'));
    renderPanel();

    expect(await screen.findByText('Failed to load bed-clear detector samples.')).toBeInTheDocument();
    expect(screen.getByText('Failed to load bed-clear detector metrics.')).toBeInTheDocument();
    expect(screen.queryByText('No acknowledgement samples are available yet.')).not.toBeInTheDocument();
  });
});
