import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const { mockToast, mockDeleteModel3dFile, mockGet3DModelsQuery } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
  mockDeleteModel3dFile: vi.fn(),
  mockGet3DModelsQuery: vi.fn(),
}));

vi.mock('sonner', () => ({ toast: mockToast }));

vi.mock('@/services/api', () => ({
  apiClient: {
    deleteModel3dFile: (...args: unknown[]) => mockDeleteModel3dFile(...args),
    get3DModelsQuery: (...args: unknown[]) => mockGet3DModelsQuery(...args),
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    hasPermission: () => true,
    user: { id: '1', username: 'test' },
    isAuthenticated: true,
  }),
}));

// Capture the refetch mock via the FileBrowser ref
const mockRefetch = vi.fn().mockResolvedValue(undefined);

vi.mock('@/features/fileBrowser/components/FileBrowser', () => ({
  FileBrowser: React.forwardRef(function MockFileBrowser(
    props: {
      renderItemActions?: (item: { id: string; fileName: string; isDirectory: boolean; path: string; meta?: Record<string, unknown> }) => React.ReactNode;
    },
    ref: React.Ref<{ refetch: () => Promise<void> }>
  ) {
    React.useImperativeHandle(ref, () => ({ refetch: mockRefetch }));

    const testItem = {
      id: 'model-123',
      fileName: 'test-model.stl',
      isDirectory: false,
      path: '/test-model.stl',
      meta: { model3d: { id: 'model-123', name: 'test-model.stl' } },
    };

    return (
      <div data-testid="file-browser">
        <div data-testid="file-actions">
          {props.renderItemActions?.(testItem)}
        </div>
      </div>
    );
  }),
}));

vi.mock('@/common/components/modals/ModelUploadModal', () => ({
  ModelUploadModal: () => null,
}));

vi.mock('@/common/components/modals/ConfirmationModal', () => ({
  ConfirmationModal: ({
    isOpen,
    message,
    onConfirm,
    onCancel,
  }: {
    isOpen: boolean;
    title: string;
    message: string;
    confirmButtonText?: string;
    cancelButtonText?: string;
    isDangerous?: boolean;
    onConfirm: () => void;
    onCancel: () => void;
  }) =>
    isOpen ? (
      <div data-testid="confirm-modal">
        <p>{message}</p>
        <button onClick={onConfirm}>Delete</button>
        <button onClick={onCancel}>Cancel</button>
      </div>
    ) : null,
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({
    children,
    onClick,
    title,
    ...rest
  }: React.ButtonHTMLAttributes<HTMLButtonElement> & {
    variant?: string;
    size?: string;
    iconCenter?: React.ReactNode;
  }) => (
    <button onClick={onClick} title={title} {...rest}>
      {children}
    </button>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  TagIcon: () => <span>TagIcon</span>,
  UploadIcon: () => <span>UploadIcon</span>,
  EyeIcon: () => <span>EyeIcon</span>,
  LayersTripleOutlineIcon: () => <span>SliceIcon</span>,
  FilterIcon: () => <span>FilterIcon</span>,
  DownloadIcon: () => <span>DownloadIcon</span>,
  DeleteIcon: () => <span>DeleteIcon</span>,
}));

import { ModelsFileBrowser } from '../ModelsFileBrowser';

function renderWithProviders(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

describe('ModelsFileBrowser delete flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('refetches file browser after successful delete', async () => {
    mockDeleteModel3dFile.mockResolvedValue(undefined);
    renderWithProviders(<ModelsFileBrowser />);

    // Click the delete button for the test item
    const deleteButton = screen.getByTitle('Delete file');
    fireEvent.click(deleteButton);

    // Confirmation modal should appear
    const confirmModal = await screen.findByTestId('confirm-modal');
    expect(confirmModal).toBeInTheDocument();
    expect(screen.getByText(/test-model\.stl/)).toBeInTheDocument();

    // Confirm the delete
    fireEvent.click(screen.getByText('Delete'));

    await waitFor(() => {
      expect(mockDeleteModel3dFile).toHaveBeenCalledWith('model-123');
    });

    await waitFor(() => {
      expect(mockToast.success).toHaveBeenCalledWith('Model deleted successfully');
    });

    await waitFor(() => {
      expect(mockRefetch).toHaveBeenCalledTimes(1);
    });
  });

  it('does not refetch when delete fails', async () => {
    mockDeleteModel3dFile.mockRejectedValue(new Error('Server error'));
    renderWithProviders(<ModelsFileBrowser />);

    fireEvent.click(screen.getByTitle('Delete file'));
    fireEvent.click(await screen.findByText('Delete'));

    await waitFor(() => {
      expect(mockToast.error).toHaveBeenCalledWith('Failed to delete model');
    });

    expect(mockRefetch).not.toHaveBeenCalled();
  });

  it('does not delete when cancel is clicked', async () => {
    renderWithProviders(<ModelsFileBrowser />);

    fireEvent.click(screen.getByTitle('Delete file'));

    const confirmModal = await screen.findByTestId('confirm-modal');
    expect(confirmModal).toBeInTheDocument();

    fireEvent.click(screen.getByText('Cancel'));

    await waitFor(() => {
      expect(screen.queryByTestId('confirm-modal')).not.toBeInTheDocument();
    });

    expect(mockDeleteModel3dFile).not.toHaveBeenCalled();
    expect(mockRefetch).not.toHaveBeenCalled();
  });
});
