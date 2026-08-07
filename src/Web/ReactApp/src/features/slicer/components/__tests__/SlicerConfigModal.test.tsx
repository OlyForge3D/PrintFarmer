import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, fireEvent, waitFor } from '@testing-library/react';
import type { ComponentProps } from 'react';
import { SlicerConfigModal } from '@/features/slicer/components/SlicerConfigModal';
import { slicerService, type SlicingProgress } from '@/services/slicerService';
import { toast } from 'sonner';

vi.mock('sonner', () => ({
  toast: {
    error: vi.fn(),
  },
}));

// Mock the slicerService
vi.mock('@/services/slicerService', () => ({
  slicerService: {
    validateModel: vi.fn(() => Promise.resolve({ valid: true })),
    sliceModel: vi.fn(),
    sliceUploadedModel: vi.fn(),
    subscribeToSlicingProgress: vi.fn(),
    getAvailableProfiles: vi.fn(),
  },
  // Re-export types as empty for imports
}));

const defaultProps = {
  isOpen: true,
  onClose: vi.fn(),
  modelFile: new File(['test'], 'model.stl', { type: 'model/stl' }),
  availablePrinters: [
    { id: '1', name: 'Printer One', backend: 'moonraker', isReachable: true },
    { id: '2', name: 'Printer Two', backend: 'prusalink', isReachable: true },
  ],
  onSliceComplete: vi.fn(),
};

type SlicerConfigModalProps = ComponentProps<typeof SlicerConfigModal>;
type ProgressCallback = (progress: SlicingProgress) => void;

const sliceResult = {
  jobId: 'slice-job-123',
  gcodeUrl: '/api/slicer/jobs/slice-job-123/gcode',
  printTime: 3600,
  filamentUsed: 12,
  layerCount: 100,
  metadata: {
    slicerVersion: '1.0.0',
    profileUsed: 'standard',
    estimatedCost: 1.25,
  },
};

const renderSlicerConfigModal = async (props: Partial<SlicerConfigModalProps> = {}) => {
  const mergedProps = { ...defaultProps, ...props };
  const result = render(<SlicerConfigModal {...mergedProps} />);

  if (mergedProps.isOpen && mergedProps.modelFile) {
    await screen.findByText('Model validation passed');
  }

  return result;
};

describe('SlicerConfigModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  describe('Modal open/close', () => {
    it('renders modal content when isOpen is true', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByText('Configure Slicing')).toBeInTheDocument();
    });

    it('does not render modal content when isOpen is false', () => {
      render(<SlicerConfigModal {...defaultProps} isOpen={false} />);
      expect(screen.queryByText('Configure Slicing')).not.toBeInTheDocument();
    });

    it('calls onClose when Cancel button is clicked', async () => {
      const onClose = vi.fn();
      await renderSlicerConfigModal({ onClose });
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
      expect(onClose).toHaveBeenCalled();
    });
  });

  describe('Dark theme — no hardcoded colors (PFarm1-5o5)', () => {
    it('does not use hardcoded gray-* Tailwind classes in rendered output', async () => {
      const { container } = await renderSlicerConfigModal();
      const allElements = container.querySelectorAll('*');
      const grayViolations: string[] = [];

      allElements.forEach((el) => {
        const cls = (el as HTMLElement).className;
        if (typeof cls === 'string') {
          // Match bg-gray-*, text-gray-*, border-gray-* patterns
          const matches = cls.match(/\b(bg|text|border)-(gray|slate)-\d+\b/g);
          if (matches) {
            grayViolations.push(`<${el.tagName.toLowerCase()}> has: ${matches.join(', ')}`);
          }
        }
      });

      // If fixes haven't landed yet, this will list the violations
      expect(grayViolations).toEqual([]);
    });

    it('model info section uses pf-* tokens instead of bg-gray-50', async () => {
      await renderSlicerConfigModal();
      // The "Model Information" section
      const modelInfoHeading = screen.getByText('Model Information');
      const modelInfoSection = modelInfoHeading.closest('div');

      if (modelInfoSection) {
        expect(modelInfoSection.className).not.toContain('bg-gray-50');
        // Should use a pf-* token for surface/background
        expect(modelInfoSection.className).toMatch(/bg-pf-/);
      }
    });

    it('form labels use pf-* text tokens instead of text-gray-700', async () => {
      const { container } = await renderSlicerConfigModal();
      const labels = container.querySelectorAll('label');

      labels.forEach((label) => {
        if (label.className.includes('text-gray-700')) {
          // This will fail until fixes land — expected
          expect(label.className).not.toContain('text-gray-700');
        }
      });
    });

    it('select elements use pf-* border tokens instead of border-gray-300', async () => {
      const { container } = await renderSlicerConfigModal();
      const selects = container.querySelectorAll('select');

      selects.forEach((select) => {
        expect(select.className).not.toContain('border-gray-300');
      });
    });

    it('input elements use pf-* border tokens instead of border-gray-300', async () => {
      const { container } = await renderSlicerConfigModal();
      const inputs = container.querySelectorAll('input[type="number"]');

      inputs.forEach((input) => {
        expect(input.className).not.toContain('border-gray-300');
      });
    });

    it('range sliders do not use bg-gray-200', async () => {
      const { container } = await renderSlicerConfigModal();
      const ranges = container.querySelectorAll('input[type="range"]');

      ranges.forEach((range) => {
        expect(range.className).not.toContain('bg-gray-200');
      });
    });

    it('no element uses hardcoded blue-500 for focus rings (should use pf-accent)', async () => {
      const { container } = await renderSlicerConfigModal();
      const focusBlueElements = container.querySelectorAll('[class*="focus:ring-blue-500"]');
      expect(focusBlueElements.length).toBe(0);
    });
  });

  describe('UI library components (PFarm1-5o5)', () => {
    it('renders slicer engine radio buttons', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByText('PrusaSlicer')).toBeInTheDocument();
      expect(screen.getByText('OrcaSlicer')).toBeInTheDocument();
    });

    it('renders quality and material selectors', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByLabelText(/print quality preset/i)).toBeInTheDocument();
      expect(screen.getByLabelText(/material type/i)).toBeInTheDocument();
    });

    it('renders temperature and speed controls', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByLabelText(/nozzle temperature/i)).toBeInTheDocument();
      expect(screen.getByLabelText(/bed temperature/i)).toBeInTheDocument();
      expect(screen.getByLabelText(/infill percentage/i)).toBeInTheDocument();
      expect(screen.getByLabelText(/print speed/i)).toBeInTheDocument();
    });

    it('renders support structure toggle', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByLabelText(/enable support structures/i)).toBeInTheDocument();
    });
  });

  describe('Content rendering', () => {
    it('shows model file name', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByText('model.stl')).toBeInTheDocument();
    });

    it('shows available printers', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByText('Printer One')).toBeInTheDocument();
      expect(screen.getByText('Printer Two')).toBeInTheDocument();
    });

    it('shows Slice & Queue Print button', async () => {
      await renderSlicerConfigModal();
      expect(screen.getByRole('button', { name: /slice.*queue/i })).toBeInTheDocument();
    });

    it('displays model name when modelName prop is provided instead of file', () => {
      render(
        <SlicerConfigModal
          {...defaultProps}
          modelFile={undefined}
          modelId="model-123"
          modelName="My Custom Model"
        />
      );
      expect(screen.getByText('My Custom Model')).toBeInTheDocument();
    });
  });

  describe('Slicing failure feedback', () => {
    const selectPrinterAndSlice = async () => {
      await renderSlicerConfigModal();
      fireEvent.click(screen.getByText('Printer One'));
      const sliceButton = screen.getByRole('button', { name: /slice.*queue/i });
      sliceButton.focus();
      await act(async () => {
        fireEvent.click(sliceButton);
      });
      return sliceButton;
    };

    it('surfaces progress-reported failures without blocking or closing the modal', async () => {
      let reportProgress: ProgressCallback | undefined;
      const closeProgressSource = vi.fn();
      const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => undefined);

      vi.mocked(slicerService.sliceModel).mockResolvedValueOnce(sliceResult);
      vi.mocked(slicerService.subscribeToSlicingProgress).mockImplementationOnce((_jobId, onProgress) => {
        reportProgress = onProgress;
        return { close: closeProgressSource } as EventSource;
      });

      const sliceButton = await selectPrinterAndSlice();
      await waitFor(() => expect(reportProgress).toBeDefined());

      act(() => {
        reportProgress?.({
          jobId: sliceResult.jobId,
          progress: 42,
          status: 'error',
          message: 'Worker exited with code 7',
        });
      });

      expect(toast.error).toHaveBeenCalledWith('Slicing failed: Worker exited with code 7');
      expect(alertSpy).not.toHaveBeenCalled();
      expect(closeProgressSource).toHaveBeenCalledOnce();
      expect(screen.getByText('Configure Slicing')).toBeInTheDocument();
      expect(sliceButton).toBeEnabled();
      expect(screen.getByRole('button', { name: /cancel/i })).toBeEnabled();
      expect(document.activeElement).toBe(sliceButton);
    });

    it.each([
      { rejection: new Error('Slicer service unavailable'), detail: 'Slicer service unavailable' },
      { rejection: { status: 503 }, detail: 'Unknown error' },
    ])('surfaces thrown failure detail "$detail" without blocking retry', async ({ rejection, detail }) => {
      const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => undefined);
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      vi.mocked(slicerService.sliceModel).mockRejectedValueOnce(rejection);

      const sliceButton = await selectPrinterAndSlice();

      await waitFor(() => {
        expect(toast.error).toHaveBeenCalledWith(`Slicing failed: ${detail}`);
      });
      expect(alertSpy).not.toHaveBeenCalled();
      expect(screen.getByText('Configure Slicing')).toBeInTheDocument();
      expect(sliceButton).toBeEnabled();
      expect(screen.getByRole('button', { name: /cancel/i })).toBeEnabled();
      expect(document.activeElement).toBe(sliceButton);
    });
  });
});
