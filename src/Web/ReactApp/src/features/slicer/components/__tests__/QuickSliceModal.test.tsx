import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { QuickSliceModal } from '@/features/slicer/components/QuickSliceModal';
import type { Model } from '@/types/models';

// Mock navigation
const mockNavigate = vi.fn();
vi.mock('react-router', async () => {
  const actual = await vi.importActual('react-router');
  return { ...actual, useNavigate: () => mockNavigate };
});

// Mock auth
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: { id: 'user-1', name: 'Test User' } }),
}));

// Mock API
vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinters: vi.fn(() => Promise.resolve([
      { id: 'printer-1', name: 'Test Printer', modelId: 'model-1' },
    ])),
    getPrinterDetails: vi.fn(() => Promise.resolve({
      id: 'printer-1',
      name: 'Test Printer',
      modelId: 'model-1',
    })),
  },
}));

// Mock slicerProfilesService
vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    getMachineProfilesForModel: vi.fn(() => Promise.resolve([
      { name: 'Machine A', manufacturer: 'Prusa', nozzleDiameter: 0.4 },
    ])),
    getFilamentProfilesForMachines: vi.fn(() => Promise.resolve([
      { name: 'PLA Generic', material: 'PLA', nozzleTemperature: 210, bedTemperature: 60, printSpeed: 60, compatible_printers: ['Machine A'] },
    ])),
    getProcessProfilesForMachines: vi.fn(() => Promise.resolve([
      { name: '0.20mm Standard', quality: 'standard', layerHeight: 0.2, infillPercentage: 15, printSpeed: 60, supports: false, compatible_printers: ['Machine A'] },
    ])),
  },
}));

// Mock sliceJobService
const mockSubmitJob = vi.fn(() => Promise.resolve({ jobId: 'job-123', status: 'Queued', queuedAt: '2026-05-31T00:00:00Z', queuePosition: 1 }));
vi.mock('@/services/sliceJobService', async () => {
  const actual = await vi.importActual<typeof import('@/services/sliceJobService')>('@/services/sliceJobService');
  return {
    sliceJobService: {
      submitJob: (...args: unknown[]) => mockSubmitJob(...args),
    },
    formatQueuePositionSuffix: actual.formatQueuePositionSuffix,
  };
});

// Mock slicerService (issue #578 registry query — QuickSlice reads latest online version)
vi.mock('@/services/slicerService', () => ({
  slicerService: {
    listEngines: vi.fn(() => Promise.resolve([
      { engine: 'OrcaSlicer', versions: ['2.4.1'], versionEntries: [{ version: '2.4.1', available: true }], latest: '2.4.1' },
    ])),
  },
}));

// Mock apiUrlHelpers
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
  getHubUrl: () => 'http://localhost:5245/hubs',
}));

// Mock toast (issue #1869 — queue position message must not render literal "null")
const mockToastSuccess = vi.fn();
vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => mockToastSuccess(...args),
  },
}));

const mockModel: Model = {
  id: 'model-abc',
  path: '/test/model.stl',
  name: 'test-model.stl',
  fileName: 'test-model.stl',
  fileSize: 1024,
  fileType: 'stl',
  uploadedAt: '2026-05-31T00:00:00Z',
};

function renderModal(props: Partial<React.ComponentProps<typeof QuickSliceModal>> = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    model: mockModel,
    ...props,
  };

  return {
    ...render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <QuickSliceModal {...defaultProps} />
        </MemoryRouter>
      </QueryClientProvider>
    ),
    onClose: defaultProps.onClose,
  };
}

describe('QuickSliceModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('open/close', () => {
    it('renders modal title when open', () => {
      renderModal();
      expect(screen.getByText('Quick Slice')).toBeInTheDocument();
    });

    it('does not render content when closed', () => {
      renderModal({ isOpen: false });
      expect(screen.queryByText('Quick Slice')).not.toBeInTheDocument();
    });

    it('shows model name', () => {
      renderModal();
      expect(screen.getByText('test-model.stl')).toBeInTheDocument();
    });

    it('calls onClose when Cancel clicked', () => {
      const { onClose } = renderModal();
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
      expect(onClose).toHaveBeenCalled();
    });
  });

  describe('profile dropdowns', () => {
    it('populates printer dropdown', async () => {
      renderModal();
      await waitFor(() => {
        expect(screen.getByLabelText(/printer/i)).toBeInTheDocument();
      });
    });

    it('populates machine profile dropdown after printer loads', async () => {
      renderModal();
      await waitFor(() => {
        const machineSelect = screen.getByLabelText(/machine profile/i);
        expect(machineSelect).not.toBeDisabled();
      });
    });

    it('populates process profile dropdown after machine loads', async () => {
      renderModal();
      await waitFor(() => {
        const processSelect = screen.getByLabelText(/process profile/i);
        expect(processSelect).not.toBeDisabled();
      });
    });

    it('populates filament profile dropdown after machine loads', async () => {
      renderModal();
      await waitFor(() => {
        const filamentSelect = screen.getByLabelText(/filament profile/i);
        expect(filamentSelect).not.toBeDisabled();
      });
    });
  });

  describe('submit', () => {
    it('submits slice job with correct parameters', async () => {
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockSubmitJob).toHaveBeenCalledWith(
          expect.objectContaining({
            userId: 'user-1',
            modelFileUrl: expect.stringContaining('model-abc'),
            modelFileName: 'test-model.stl',
            slicerEngine: 0,
          })
        );
      });
    });

    it('navigates to the canonical jobs page on success', async () => {
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockNavigate).toHaveBeenCalledWith(
          '/admin/workers?workerTab=jobs',
        );
      });
    });

    it('shows the queue position in the confirmation toast when the API returns one (issue #1869)', async () => {
      mockSubmitJob.mockResolvedValueOnce({
        jobId: 'job-123',
        status: 'Queued',
        queuedAt: '2026-05-31T00:00:00Z',
        queuePosition: 3,
      });
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockToastSuccess).toHaveBeenCalledWith('Slice job queued — position 3');
      });
    });

    it('omits the position phrase (and never shows literal "null") when the API returns no queue position (issue #1869)', async () => {
      mockSubmitJob.mockResolvedValueOnce({
        jobId: 'job-456',
        status: 'Queued',
        queuedAt: '2026-05-31T00:00:00Z',
        queuePosition: null,
      });
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockToastSuccess).toHaveBeenCalledWith('Slice job queued');
      });
      expect(mockToastSuccess).not.toHaveBeenCalledWith(expect.stringContaining('null'));
    });
  });

  describe('bed type override', () => {
    it('defaults to "Inherit from profile"', async () => {
      renderModal();
      await waitFor(() => {
        const bedTypeSelect = screen.getByLabelText(/bed type/i);
        expect(bedTypeSelect).toHaveValue('');
      });
    });

    it('can select a bed type override', async () => {
      renderModal();
      await waitFor(() => {
        expect(screen.getByLabelText(/bed type/i)).toBeInTheDocument();
      });

      fireEvent.change(screen.getByLabelText(/bed type/i), { target: { value: 'Cool Plate' } });
      expect(screen.getByLabelText(/bed type/i)).toHaveValue('Cool Plate');
    });

    it('includes bed type in overrides when selected', async () => {
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.change(screen.getByLabelText(/bed type/i), { target: { value: 'Textured PEI Plate' } });
      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockSubmitJob).toHaveBeenCalledWith(
          expect.objectContaining({
            slicerProfileJson: expect.stringContaining('"curr_bed_type":"Textured PEI Plate"'),
          })
        );
      });
    });

    it('omits bed type from overrides when inherit is selected', async () => {
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockSubmitJob).toHaveBeenCalled();
        const call = mockSubmitJob.mock.calls[0][0] as { slicerProfileJson: string };
        expect(call.slicerProfileJson).not.toContain('curr_bed_type');
      });
    });
  });

  describe('advanced settings link', () => {
    it('navigates to NewSliceJobPage with model preselected', async () => {
      const { onClose } = renderModal();

      const advancedBtn = screen.getByRole('button', { name: /advanced settings/i });
      fireEvent.click(advancedBtn);

      expect(onClose).toHaveBeenCalled();
      expect(mockNavigate).toHaveBeenCalledWith('/slicer?modelId=model-abc');
    });
  });
});
