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
      { name: 'PLA Generic', material: 'PLA', nozzleTemperature: 210, bedTemperature: 60, printSpeed: 60, compatiblePrinters: ['Machine A'] },
    ])),
    getProcessProfilesForMachines: vi.fn(() => Promise.resolve([
      { name: '0.20mm Standard', quality: 'standard', layerHeight: 0.2, infillPercentage: 15, printSpeed: 60, supports: false, compatiblePrinters: ['Machine A'] },
    ])),
  },
}));

// Mock sliceJobService
const mockSubmitJob = vi.fn(() => Promise.resolve({ jobId: 'job-123', status: 'Queued', queuedAt: '2026-05-31T00:00:00Z', queuePosition: 1 }));
vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    submitJob: (...args: unknown[]) => mockSubmitJob(...args),
  },
}));

// Mock apiUrlHelpers
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
  getHubUrl: () => 'http://localhost:5245/hubs',
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

    it('navigates to jobs page on success', async () => {
      renderModal();

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));

      await waitFor(() => {
        expect(mockNavigate).toHaveBeenCalledWith('/slicer/jobs');
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
