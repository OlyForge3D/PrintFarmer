import '@testing-library/jest-dom';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SetupWizard } from '../SetupWizard';

const {
  mockCreateInitialAdmin,
  mockGetSettings,
  mockGetSetupStatus,
  mockLogin,
  mockScanNetwork,
  mockTestSpoolmanConnection,
} = vi.hoisted(() => ({
  mockCreateInitialAdmin: vi.fn(),
  mockGetSettings: vi.fn(),
  mockGetSetupStatus: vi.fn(),
  mockLogin: vi.fn(),
  mockScanNetwork: vi.fn(),
  mockTestSpoolmanConnection: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ isAuthenticated: false, login: mockLogin }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  useHealthStatus: () => ({
    data: { kind: 'basic', status: 'Healthy' },
    isLoading: false,
    refetch: vi.fn(),
  }),
}));

vi.mock('@/contexts/SpoolmanHooks', () => ({
  useSpoolman: () => ({
    setEnabled: vi.fn(),
    setBaseUrl: vi.fn(),
    updateProbeSuccess: vi.fn(),
    updateProbeFailure: vi.fn(),
  }),
}));

vi.mock('@/common/hooks/useSpoolmanNetworkScan', () => ({
  useSpoolmanNetworkScan: () => ({
    isScanning: false,
    results: [],
    error: null,
    scanNetwork: mockScanNetwork,
    reset: vi.fn(),
    availableInstances: [],
  }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    createInitialAdmin: (...args: unknown[]) => mockCreateInitialAdmin(...args),
    getSettings: (...args: unknown[]) => mockGetSettings(...args),
    getSetupStatus: (...args: unknown[]) => mockGetSetupStatus(...args),
    testSpoolmanConnection: (...args: unknown[]) => mockTestSpoolmanConnection(...args),
  },
}));

describe('SetupWizard authentication ordering', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetSetupStatus.mockResolvedValue({ needsSetup: true });
    mockGetSettings.mockImplementation((key: string) => Promise.resolve(
      key === 'NetworkDiscovery'
        ? {
            enableDiscovery: true,
            discoverySubnets: [],
            clientTimeoutMs: 200,
            requestDelayMs: 100,
            maxConcurrentRequests: 20,
            maxRetries: 2,
            ports: [80],
          }
        : {}
    ));
    mockCreateInitialAdmin.mockResolvedValue({ success: true, token: 'bootstrap-token' });
    mockLogin.mockResolvedValue(true);
    mockScanNetwork.mockResolvedValue(undefined);
    mockTestSpoolmanConnection.mockResolvedValue({ success: true, version: '0.22.1' });
  });

  it('creates and authenticates the admin before protected Spoolman actions', async () => {
    render(<SetupWizard onComplete={vi.fn()} />);

    await screen.findByText('Initial configuration wizard');
    fireEvent.change(screen.getByLabelText(/First Name/, { selector: 'input' }), { target: { value: 'Ada' } });
    fireEvent.change(screen.getByLabelText(/Last Name/, { selector: 'input' }), { target: { value: 'Lovelace' } });
    fireEvent.change(screen.getByLabelText(/Username/, { selector: 'input' }), { target: { value: 'admin' } });
    fireEvent.change(screen.getByLabelText(/Email/, { selector: 'input' }), { target: { value: 'admin@example.com' } });
    fireEvent.change(screen.getByLabelText(/^Password/, { selector: 'input' }), { target: { value: 'password123' } });
    fireEvent.change(screen.getByLabelText(/Confirm Password/, { selector: 'input' }), { target: { value: 'password123' } });

    fireEvent.click(screen.getByRole('button', { name: 'Create Admin & Continue' }));

    await screen.findByText('Network Discovery', { selector: 'h2' });
    expect(mockCreateInitialAdmin).toHaveBeenCalledTimes(1);
    expect(mockLogin).toHaveBeenCalledWith({ username: 'admin', password: 'password123' });
    expect(mockCreateInitialAdmin.mock.invocationCallOrder[0]).toBeLessThan(mockLogin.mock.invocationCallOrder[0]);

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText('Spoolman Integration', { selector: 'h2' });

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    fireEvent.change(screen.getByPlaceholderText('http://spoolman:7912'), {
      target: { value: 'http://spoolman.local:7912' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Test URL' }));

    await waitFor(() => {
      expect(mockTestSpoolmanConnection).toHaveBeenCalledWith('http://spoolman.local:7912');
    });
    await screen.findByRole('button', { name: 'Test URL' });

    fireEvent.click(screen.getByRole('button', { name: /Scan Network/ }));
    await waitFor(() => expect(mockScanNetwork).toHaveBeenCalledTimes(1));

    expect(mockLogin.mock.invocationCallOrder[0]).toBeLessThan(mockTestSpoolmanConnection.mock.invocationCallOrder[0]);
    expect(mockLogin.mock.invocationCallOrder[0]).toBeLessThan(mockScanNetwork.mock.invocationCallOrder[0]);
  });
});
