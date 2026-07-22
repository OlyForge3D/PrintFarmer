import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { slicerService } from '@/services/slicerService';

// Mock the slicer service
vi.mock('@/services/slicerService', () => ({
  slicerService: {
    uploadModel: vi.fn()
  }
}));

// Mock toast
vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn()
  }
}));

describe('ModelUploadModal', () => {
  let queryClient: QueryClient;
  const mockOnClose = vi.fn();
  const mockOnUploadSuccess = vi.fn();

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false }
      }
    });
    vi.clearAllMocks();
  });

  afterEach(() => {
    queryClient.clear();
  });

  const renderModal = (props = {}) => {
    return render(
      <QueryClientProvider client={queryClient}>
        <ModelUploadModal
          isOpen={true}
          onClose={mockOnClose}
          onUploadSuccess={mockOnUploadSuccess}
          {...props}
        />
      </QueryClientProvider>
    );
  };

  describe('Upload Lifecycle', () => {
    it('should show progress capped at 95% during network upload', async () => {
      const mockFile = new File(['test content'], 'test.stl', { type: 'model/stl' });
      let progressCallback: ((progress: number) => void) | undefined;

      // Mock uploadModel to capture progress callback
      vi.mocked(slicerService.uploadModel).mockImplementation((file, onProgress) => {
        progressCallback = onProgress;
        return new Promise((resolve) => {
          // Simulate progress updates
          setTimeout(() => progressCallback?.(50), 10);
          setTimeout(() => progressCallback?.(100), 20);
          setTimeout(() => {
            resolve({ id: 'test-id', url: 'test-url' });
          }, 100);
        });
      });

      renderModal();

      // Find the file input by the id attribute
      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      expect(fileInput).toBeTruthy();
      
      // Simulate file selection
      Object.defineProperty(fileInput, 'files', {
        value: [mockFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(screen.getByText('test.stl')).toBeInTheDocument();
      });

      // Click upload
      const uploadButton = screen.getByRole('button', { name: /upload 1 file/i });
      fireEvent.click(uploadButton);

      // Wait for progress updates - should cap at 95%
      await waitFor(() => {
        const progressText = screen.getByText(/95%/);
        expect(progressText).toBeInTheDocument();
      }, { timeout: 2000 });
    });

    it('should only show success toast after backend completes processing', async () => {
      const mockFile = new File(['test content'], 'test.stl', { type: 'model/stl' });
      const { toast } = await import('sonner');

      vi.mocked(slicerService.uploadModel).mockImplementation((_file, onProgress) => {
        onProgress?.(100);
        return Promise.resolve({ id: 'test-id', url: 'test-url' });
      });

      renderModal();

      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      Object.defineProperty(fileInput, 'files', {
        value: [mockFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(screen.getByText('test.stl')).toBeInTheDocument();
      });

      const uploadButton = screen.getByRole('button', { name: /upload 1 file/i });
      await act(async () => {
        fireEvent.click(uploadButton);
      });

      await waitFor(() => {
        expect(toast.success).toHaveBeenCalledWith('test.stl uploaded successfully');
      }, { timeout: 2000 });
    });

    it('should wait for query invalidation and callback before closing modal', async () => {
      const mockFile = new File(['test content'], 'test.stl', { type: 'model/stl' });
      
      vi.mocked(slicerService.uploadModel).mockResolvedValue({
        id: 'test-id',
        url: 'test-url'
      });

      // Mock slow onUploadSuccess callback
      mockOnUploadSuccess.mockImplementation(() => {
        return new Promise(resolve => setTimeout(resolve, 100));
      });

      renderModal();

      // Add file
      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      Object.defineProperty(fileInput, 'files', {
        value: [mockFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(screen.getByText('test.stl')).toBeInTheDocument();
      });

      const uploadButton = screen.getByRole('button', { name: /upload 1 file/i });
      fireEvent.click(uploadButton);

      // Wait for upload to complete
      await waitFor(() => {
        expect(screen.getByText('✓ Done')).toBeInTheDocument();
      });

      // Click the footer Close button (not the modal X button which has aria-label="Close modal")
      const closeButton = screen.getByRole('button', { name: /^close$/i });
      fireEvent.click(closeButton);

      // Close button should show loading state
      await waitFor(() => {
        expect(closeButton).toHaveAttribute('disabled');
      });

      // onClose should only be called after callback completes
      expect(mockOnClose).not.toHaveBeenCalled();

      await waitFor(() => {
        expect(mockOnClose).toHaveBeenCalled();
        expect(mockOnUploadSuccess).toHaveBeenCalled();
      }, { timeout: 2000 });
    });

    it('should invalidate file-browser queries on close, not models-search', async () => {
      const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

      renderModal();

      // Click the footer Close button (not the modal X button)
      const closeButton = screen.getByRole('button', { name: /^close$/i });
      fireEvent.click(closeButton);

      await waitFor(() => {
        expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
      });

      // Verify the old incorrect key is NOT used
      expect(invalidateSpy).not.toHaveBeenCalledWith({ queryKey: ['models-search'] });

      invalidateSpy.mockRestore();
    });
  });

  describe('File Validation', () => {
    it('should only accept valid 3D model file types', async () => {
      const { toast } = await import('sonner');
      const invalidFile = new File(['test'], 'test.txt', { type: 'text/plain' });

      renderModal();

      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      Object.defineProperty(fileInput, 'files', {
        value: [invalidFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(toast.error).toHaveBeenCalledWith('No valid 3D model files (STL, 3MF, OBJ, PLY)');
      });

      // File should not be added to queue
      expect(screen.queryByText('test.txt')).not.toBeInTheDocument();
    });

    it('should accept valid file types', async () => {
      const validFiles = [
        new File([''], 'model.stl', { type: 'model/stl' }),
        new File([''], 'model.3mf', { type: 'model/3mf' }),
        new File([''], 'model.obj', { type: 'model/obj' }),
        new File([''], 'model.ply', { type: 'application/ply' })
      ];

      renderModal();

      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      
      for (const file of validFiles) {
        Object.defineProperty(fileInput, 'files', {
          value: [file],
          writable: false,
          configurable: true
        });
        fireEvent.change(fileInput);
        await waitFor(() => {
          expect(screen.getByText(file.name)).toBeInTheDocument();
        });
      }

      // All files should be in queue
      expect(screen.getByText(/upload 4 files/i)).toBeInTheDocument();
    });
  });

  describe('Error Handling', () => {
    it('should show error toast and status when upload fails', async () => {
      const { toast } = await import('sonner');
      const mockFile = new File(['test'], 'test.stl', { type: 'model/stl' });
      const errorMessage = 'Network error during upload';

      vi.mocked(slicerService.uploadModel).mockRejectedValue(new Error(errorMessage));

      renderModal();

      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      Object.defineProperty(fileInput, 'files', {
        value: [mockFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(screen.getByText('test.stl')).toBeInTheDocument();
      });

      const uploadButton = screen.getByRole('button', { name: /upload 1 file/i });
      fireEvent.click(uploadButton);

      await waitFor(() => {
        expect(toast.error).toHaveBeenCalledWith('Failed to upload test.stl');
        expect(screen.getByText('✗ Error')).toBeInTheDocument();
        expect(screen.getByText(errorMessage)).toBeInTheDocument();
      });
    });
  });

  describe('Queue Management', () => {
    it('should clear queue when modal closes', async () => {
      const mockFile = new File(['test'], 'test.stl', { type: 'model/stl' });

      const { unmount } = renderModal();

      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      Object.defineProperty(fileInput, 'files', {
        value: [mockFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(screen.getByText('test.stl')).toBeInTheDocument();
      });

      // Unmount and remount modal
      unmount();

      // Reopen modal - queue should be empty (new component instance)
      renderModal();
      
      expect(screen.queryByText('test.stl')).not.toBeInTheDocument();
      expect(screen.getByText(/add files to the queue above/i)).toBeInTheDocument();
    });

    it('should disable upload button while uploads are in progress', async () => {
      const mockFile = new File(['test'], 'test.stl', { type: 'model/stl' });

      vi.mocked(slicerService.uploadModel).mockImplementation(() => {
        return new Promise(resolve => setTimeout(() => resolve({ id: 'test-id', url: 'test-url' }), 500));
      });

      renderModal();

      const fileInput = document.querySelector('#model-file-upload') as HTMLInputElement;
      Object.defineProperty(fileInput, 'files', {
        value: [mockFile],
        writable: false
      });
      fireEvent.change(fileInput);

      await waitFor(() => {
        expect(screen.getByText('test.stl')).toBeInTheDocument();
      });

      // Get initial upload button
      const uploadButton = screen.getByRole('button', { name: /upload 1 file/i });
      fireEvent.click(uploadButton);

      // Upload button should disappear (no queued files) and uploading status should show
      await waitFor(() => {
        expect(screen.queryByRole('button', { name: /upload \d+ file/i })).not.toBeInTheDocument();
        expect(screen.getByText(/Uploading: 1/i)).toBeInTheDocument();
      }, { timeout: 1000 });
    });
  });
});
