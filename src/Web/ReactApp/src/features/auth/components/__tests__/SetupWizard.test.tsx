import '@testing-library/jest-dom';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SetupWizard } from '../SetupWizard';

const {
  mockAuthState,
  mockCreateInitialAdmin,
  mockGetSettings,
  mockGetSetupBootstrap,
  mockGetSetupStatus,
  mockLogin,
  mockNetworkScanState,
  mockScanNetwork,
  mockTestSpoolmanConnection,
} = vi.hoisted(() => ({
  mockAuthState: { isAuthenticated: false },
  mockCreateInitialAdmin: vi.fn(),
  mockGetSettings: vi.fn(),
  mockGetSetupBootstrap: vi.fn(),
  mockGetSetupStatus: vi.fn(),
  mockLogin: vi.fn(),
  mockNetworkScanState: { availableInstances: [] as Array<{ url: string; version?: string }> },
  mockScanNetwork: vi.fn(),
  mockTestSpoolmanConnection: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ isAuthenticated: mockAuthState.isAuthenticated, login: mockLogin }),
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
    availableInstances: mockNetworkScanState.availableInstances,
  }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    createInitialAdmin: (...args: unknown[]) => mockCreateInitialAdmin(...args),
    getSettings: (...args: unknown[]) => mockGetSettings(...args),
    getSetupBootstrap: (...args: unknown[]) => mockGetSetupBootstrap(...args),
    getSetupStatus: (...args: unknown[]) => mockGetSetupStatus(...args),
    testSpoolmanConnection: (...args: unknown[]) => mockTestSpoolmanConnection(...args),
  },
}));

async function advanceToSpoolmanStep() {
  await screen.findByText('Initial configuration wizard');
  fireEvent.change(screen.getByLabelText(/First Name/, { selector: 'input' }), { target: { value: 'Ada' } });
  fireEvent.change(screen.getByLabelText(/Last Name/, { selector: 'input' }), { target: { value: 'Lovelace' } });
  fireEvent.change(screen.getByLabelText(/Username/, { selector: 'input' }), { target: { value: 'admin' } });
  fireEvent.change(screen.getByLabelText(/Email/, { selector: 'input' }), { target: { value: 'admin@example.com' } });
  fireEvent.change(screen.getByLabelText(/^Password/, { selector: 'input' }), { target: { value: 'password123' } });
  fireEvent.change(screen.getByLabelText(/Confirm Password/, { selector: 'input' }), { target: { value: 'password123' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create Admin & Continue' }));

  await screen.findByText('Network Discovery', { selector: 'h2' });
  fireEvent.click(screen.getByRole('button', { name: 'Next' }));
  await screen.findByText('Spoolman Integration', { selector: 'h2' });
}

describe('SetupWizard first-run Spoolman bootstrap', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockAuthState.isAuthenticated = false;
    mockNetworkScanState.availableInstances = [];
    mockGetSetupStatus.mockResolvedValue({ needsSetup: true });
    mockGetSetupBootstrap.mockResolvedValue({ baseUrl: '' });
    mockGetSettings.mockResolvedValue({
      enableDiscovery: true,
      discoverySubnets: [],
      clientTimeoutMs: 200,
      requestDelayMs: 100,
      maxConcurrentRequests: 20,
      maxRetries: 2,
      ports: [80],
    });
    mockCreateInitialAdmin.mockResolvedValue({ success: true, token: 'bootstrap-token' });
    mockLogin.mockImplementation(async () => {
      mockAuthState.isAuthenticated = true;
      return true;
    });
    mockScanNetwork.mockResolvedValue(undefined);
    mockTestSpoolmanConnection.mockResolvedValue({ success: true, version: '0.22.1' });
  });

  it('pre-populates the deployment URL without reading protected settings', async () => {
    mockGetSetupBootstrap.mockResolvedValue({ baseUrl: 'http://deployment-spoolman:7912' });
    render(<SetupWizard onComplete={vi.fn()} />);

    await advanceToSpoolmanStep();

    expect(mockGetSetupBootstrap).toHaveBeenCalledTimes(1);
    expect(mockGetSetupBootstrap).toHaveBeenCalledWith(expect.any(AbortSignal));
    expect(mockGetSettings).not.toHaveBeenCalledWith('Spoolman');
    expect(screen.getByLabelText('Enable Spoolman')).toBeChecked();
    expect(screen.getByPlaceholderText('http://spoolman:7912')).toHaveValue('http://deployment-spoolman:7912');
  });

  it('surfaces an unavailable bootstrap response and keeps manual setup available', async () => {
    mockGetSetupBootstrap.mockRejectedValue(new Error('Service unavailable'));
    render(<SetupWizard onComplete={vi.fn()} />);

    await advanceToSpoolmanStep();

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Could not load the deployment Spoolman URL. Enter it manually or scan the network.',
    );
    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    fireEvent.click(screen.getByRole('button', { name: /Scan Network/ }));
    await waitFor(() => expect(mockScanNetwork).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/Could not load the deployment Spoolman URL/)).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('http://spoolman:7912'), {
      target: { value: 'http://manual-spoolman:7912' },
    });
    expect(screen.getByPlaceholderText('http://spoolman:7912')).toHaveValue('http://manual-spoolman:7912');
    expect(screen.getByRole('button', { name: /Scan Network/ })).toBeEnabled();
    expect(screen.queryByText(/Could not load the deployment Spoolman URL/)).not.toBeInTheDocument();
  });

  it('does not restore the bootstrap warning when a late failure follows manual input', async () => {
    let rejectBootstrap!: (reason: unknown) => void;
    mockGetSetupBootstrap.mockReturnValue(new Promise((_resolve, reject) => {
      rejectBootstrap = reject;
    }));
    render(<SetupWizard onComplete={vi.fn()} />);
    await advanceToSpoolmanStep();

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    fireEvent.change(screen.getByPlaceholderText('http://spoolman:7912'), {
      target: { value: 'http://manual-spoolman:7912' },
    });
    await act(async () => rejectBootstrap(new Error('Late network failure')));

    expect(screen.queryByText(/Could not load the deployment Spoolman URL/)).not.toBeInTheDocument();
  });

  it('populates a late deployment URL after only enabling Spoolman', async () => {
    let resolveBootstrap!: (value: { baseUrl: string }) => void;
    mockGetSetupBootstrap.mockReturnValue(new Promise(resolve => {
      resolveBootstrap = resolve;
    }));
    render(<SetupWizard onComplete={vi.fn()} />);
    await advanceToSpoolmanStep();

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    await act(async () => resolveBootstrap({ baseUrl: 'http://late-deployment-spoolman:7912' }));

    expect(screen.getByLabelText('Enable Spoolman')).toBeChecked();
    expect(screen.getByPlaceholderText('http://spoolman:7912')).toHaveValue('http://late-deployment-spoolman:7912');
  });

  it('surfaces a late bootstrap failure after only enabling Spoolman', async () => {
    let rejectBootstrap!: (reason: unknown) => void;
    mockGetSetupBootstrap.mockReturnValue(new Promise((_resolve, reject) => {
      rejectBootstrap = reject;
    }));
    render(<SetupWizard onComplete={vi.fn()} />);
    await advanceToSpoolmanStep();

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    await act(async () => rejectBootstrap(new Error('Late network failure')));

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Could not load the deployment Spoolman URL. Enter it manually or scan the network.',
    );
  });

  it('does not overwrite a manual selection when the bootstrap response arrives late', async () => {
    let resolveBootstrap!: (value: { baseUrl: string }) => void;
    mockGetSetupBootstrap.mockReturnValue(new Promise(resolve => {
      resolveBootstrap = resolve;
    }));
    render(<SetupWizard onComplete={vi.fn()} />);
    await advanceToSpoolmanStep();

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    fireEvent.change(screen.getByPlaceholderText('http://spoolman:7912'), {
      target: { value: 'http://manual-spoolman:7912' },
    });
    await act(async () => resolveBootstrap({ baseUrl: 'http://late-deployment-spoolman:7912' }));

    expect(screen.getByLabelText('Enable Spoolman')).toBeChecked();
    expect(screen.getByPlaceholderText('http://spoolman:7912')).toHaveValue('http://manual-spoolman:7912');
  });

  it('does not overwrite a scanned instance when the bootstrap response arrives late', async () => {
    let resolveBootstrap!: (value: { baseUrl: string }) => void;
    mockGetSetupBootstrap.mockReturnValue(new Promise(resolve => {
      resolveBootstrap = resolve;
    }));
    mockNetworkScanState.availableInstances = [{ url: 'http://scanned-spoolman:7912', version: '0.22.1' }];
    render(<SetupWizard onComplete={vi.fn()} />);
    await advanceToSpoolmanStep();

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    fireEvent.click(screen.getByRole('button', { name: /http:\/\/scanned-spoolman:7912/ }));
    await act(async () => resolveBootstrap({ baseUrl: 'http://late-deployment-spoolman:7912' }));

    expect(screen.getByPlaceholderText('http://spoolman:7912')).toHaveValue('http://scanned-spoolman:7912');
  });

  it('does not warn when setup completion makes the bootstrap endpoint unavailable', async () => {
    mockGetSetupBootstrap.mockRejectedValue({ statusCode: 404, message: 'Not Found' });
    render(<SetupWizard onComplete={vi.fn()} />);

    await advanceToSpoolmanStep();

    expect(screen.queryByText(/Could not load the deployment Spoolman URL/)).not.toBeInTheDocument();
  });

  it('aborts the bootstrap request and ignores its completion after unmount', async () => {
    let signal: AbortSignal | undefined;
    let resolveBootstrap!: (value: { baseUrl: string }) => void;
    mockGetSetupBootstrap.mockImplementation((requestSignal: AbortSignal) => {
      signal = requestSignal;
      return new Promise(resolve => {
        resolveBootstrap = resolve;
      });
    });
    const { unmount } = render(<SetupWizard onComplete={vi.fn()} />);

    await waitFor(() => expect(signal).toBeDefined());
    unmount();
    expect(signal?.aborted).toBe(true);
    await act(async () => resolveBootstrap({ baseUrl: 'http://ignored-spoolman:7912' }));
    expect(mockGetSetupBootstrap).toHaveBeenCalledTimes(1);
  });

  it('creates and authenticates the admin before protected Spoolman actions', async () => {
    render(<SetupWizard onComplete={vi.fn()} />);
    await advanceToSpoolmanStep();

    expect(mockCreateInitialAdmin).toHaveBeenCalledTimes(1);
    expect(mockLogin).toHaveBeenCalledWith({ username: 'admin', password: 'password123' });
    expect(mockCreateInitialAdmin.mock.invocationCallOrder[0]).toBeLessThan(mockLogin.mock.invocationCallOrder[0]);

    fireEvent.click(screen.getByLabelText('Enable Spoolman'));
    fireEvent.change(screen.getByPlaceholderText('http://spoolman:7912'), {
      target: { value: 'http://spoolman.local:7912' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Test URL' }));

    await waitFor(() => {
      expect(mockTestSpoolmanConnection).toHaveBeenCalledWith('http://spoolman.local:7912');
    });
    fireEvent.click(screen.getByRole('button', { name: /Scan Network/ }));
    await waitFor(() => expect(mockScanNetwork).toHaveBeenCalledTimes(1));

    expect(mockLogin.mock.invocationCallOrder[0]).toBeLessThan(mockTestSpoolmanConnection.mock.invocationCallOrder[0]);
    expect(mockLogin.mock.invocationCallOrder[0]).toBeLessThan(mockScanNetwork.mock.invocationCallOrder[0]);
  });
});
