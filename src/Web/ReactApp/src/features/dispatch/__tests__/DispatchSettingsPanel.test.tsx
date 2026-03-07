import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

vi.mock('@/services/api', () => ({
  apiClient: {
    get: vi.fn(),
    put: vi.fn(),
  },
}));

vi.mock('@/common/components/ui', () => ({
  Card: Object.assign(
    ({ children }: { children: React.ReactNode }) => <div data-testid="card">{children}</div>,
    {
      Header: ({ children }: { children: React.ReactNode }) => <div data-testid="card-header">{children}</div>,
      Body: ({ children, className }: { children: React.ReactNode; className?: string }) => (
        <div data-testid="card-body" className={className}>{children}</div>
      ),
      Footer: ({ children, className }: { children: React.ReactNode; className?: string }) => (
        <div data-testid="card-footer" className={className}>{children}</div>
      ),
    },
  ),
  Button: ({ children, onClick, disabled, loading, variant }: {
    children: React.ReactNode;
    onClick?: () => void;
    disabled?: boolean;
    loading?: boolean;
    variant?: string;
  }) => (
    <button onClick={onClick} disabled={disabled} data-loading={loading} data-variant={variant}>
      {children}
    </button>
  ),
  Input: (props: React.InputHTMLAttributes<HTMLInputElement>) => <input {...props} />,
  Select: ({ children, ...props }: React.SelectHTMLAttributes<HTMLSelectElement> & { containerClassName?: string }) => (
    <select {...props}>{children}</select>
  ),
  FormField: ({ children, label, htmlFor, helper, error }: {
    children: React.ReactNode;
    label: string;
    htmlFor: string;
    helper?: string;
    error?: string;
  }) => (
    <div data-testid="form-field">
      <label htmlFor={htmlFor}>{label}</label>
      {helper && <span data-testid="helper">{helper}</span>}
      {error && <span data-testid="error">{error}</span>}
      {children}
    </div>
  ),
  Toggle: ({ checked, onChange, id }: {
    checked?: boolean;
    onChange?: (e: React.ChangeEvent<HTMLInputElement>) => void;
    id?: string;
  }) => (
    <input type="checkbox" id={id} checked={checked} onChange={onChange} role="switch" />
  ),
  Spinner: ({ size }: { size?: string }) => <div data-testid="spinner" data-size={size}>Loading...</div>,
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
  },
}));

import { DispatchSettingsPanel } from '../components/DispatchSettingsPanel';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';

const mockSettings = {
  autoDispatchEnabled: true,
  autoDispatchMode: 'Suggest',
  idleThresholdSeconds: 30,
  minimumScoreThreshold: 0.5,
  maxConcurrentDispatches: 3,
};

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

describe('DispatchSettingsPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.get).mockResolvedValue(mockSettings);
    vi.mocked(apiClient.put).mockResolvedValue(mockSettings);
  });

  it('renders loading spinner initially', () => {
    vi.mocked(apiClient.get).mockReturnValue(new Promise(() => {}));
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    expect(screen.getByTestId('spinner')).toBeInTheDocument();
  });

  it('renders settings form after data loads', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Auto Dispatch Enabled')).toBeInTheDocument();
    });
    expect(screen.getByLabelText('Auto Dispatch Mode')).toBeInTheDocument();
    expect(screen.getByLabelText('Idle Threshold (seconds)')).toBeInTheDocument();
    expect(screen.getByLabelText('Minimum Score Threshold')).toBeInTheDocument();
    expect(screen.getByLabelText('Max Concurrent Dispatches')).toBeInTheDocument();
  });

  it('renders title', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByText('Dispatch Settings')).toBeInTheDocument();
    });
  });

  it('populates form with fetched settings', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Auto Dispatch Enabled')).toBeChecked();
    });
    expect(screen.getByLabelText('Auto Dispatch Mode')).toHaveValue('Suggest');
    expect(screen.getByLabelText('Idle Threshold (seconds)')).toHaveValue(30);
  });

  it('save button is disabled when form is clean', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByText('Save Settings')).toBeDisabled();
    });
  });

  it('save button becomes enabled after form change', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Idle Threshold (seconds)')).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText('Idle Threshold (seconds)'), { target: { value: '60' } });
    expect(screen.getByText('Save Settings')).not.toBeDisabled();
  });

  it('calls API on save', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Idle Threshold (seconds)')).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText('Idle Threshold (seconds)'), { target: { value: '60' } });
    fireEvent.click(screen.getByText('Save Settings'));
    await waitFor(() => {
      expect(apiClient.put).toHaveBeenCalledWith('/dispatch-settings', expect.objectContaining({
        idleThresholdSeconds: 60,
      }));
    });
  });

  it('shows success toast on save', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Idle Threshold (seconds)')).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText('Idle Threshold (seconds)'), { target: { value: '60' } });
    fireEvent.click(screen.getByText('Save Settings'));
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Dispatch settings saved');
    });
  });

  it('shows error toast when threshold too low', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Idle Threshold (seconds)')).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText('Idle Threshold (seconds)'), { target: { value: '2' } });
    fireEvent.click(screen.getByText('Save Settings'));
    expect(toast.error).toHaveBeenCalledWith('Idle threshold must be at least 5 seconds');
  });

  it('shows error when API fails to load', async () => {
    vi.mocked(apiClient.get).mockRejectedValue(new Error('Network error'));
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByText(/Failed to load dispatch settings/)).toBeInTheDocument();
    });
  });

  it('toggles dispatch mode selector', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByLabelText('Auto Dispatch Mode')).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText('Auto Dispatch Mode'), { target: { value: 'Auto' } });
    expect(screen.getByLabelText('Auto Dispatch Mode')).toHaveValue('Auto');
  });

  it('renders helper text for fields', async () => {
    render(<DispatchSettingsPanel />, { wrapper: createWrapper() });
    await waitFor(() => {
      expect(screen.getByText(/Minimum dispatch score/)).toBeInTheDocument();
    });
    expect(screen.getByText(/Time a printer must be idle/)).toBeInTheDocument();
  });
});
