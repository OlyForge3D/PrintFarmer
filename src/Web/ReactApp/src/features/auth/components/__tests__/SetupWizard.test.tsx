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
  mockSaveSpoolmanConfig,
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
  mockSaveSpoolmanConfig: vi.fn(),
  mockScanNetwork: vi.fn(),
  mockTestSpoolmanConnection: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ isAuthenticated: mockAuthState.isAuthenticated, login: mockLogin }),
}));

vi.mock('@/common/hooks/useHealthStatus', () => ({
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

vi.mock('@/services/api/setupApi', () => ({
  createInitialAdmin: (...args: unknown[]) => mockCreateInitialAdmin(...args),
  getSetupBootstrap: (...args: unknown[]) => mockGetSetupBootstrap(...args),
  getSetupStatus: (...args: unknown[]) => mockGetSetupStatus(...args),
  saveSpoolmanConfig: (...args: unknown[]) => mockSaveSpoolmanConfig(...args),
  testSpoolmanConnection: (...args: unknown[]) => mockTestSpoolmanConnection(...args),
}));

vi.mock('@/services/settingsApi', () => ({
  fetchSettingsValues: (...args: unknown[]) => mockGetSettings(...args),
  saveSettingsValues: vi.fn(),
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
    mockSaveSpoolmanConfig.mockResolvedValue({});
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

/**
 * #2365 — submitting the initial admin setup form with empty required fields
 * silently did nothing: no field errors were shown, focus never moved, and
 * no request was sent. The account step's validation errors were computed
 * but rendered from a disconnected `useActionState` action that was never
 * dispatched, so the UI always displayed an empty error set.
 */
describe('SetupWizard account step validation (#2365)', () => {
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
  });

  it('shows field-level errors and focuses the first invalid field when all required fields are empty', async () => {
    render(<SetupWizard onComplete={vi.fn()} />);
    await screen.findByText('Initial configuration wizard');

    fireEvent.click(screen.getByRole('button', { name: 'Create Admin & Continue' }));

    const firstNameInput = screen.getByLabelText(/First Name/, { selector: 'input' });
    await waitFor(() => expect(firstNameInput).toHaveFocus());

    expect(firstNameInput).toHaveAttribute('aria-invalid', 'true');
    expect(firstNameInput).toHaveAttribute('aria-describedby', 'firstName-error');
    expect(screen.getByText('First name is required')).toHaveAttribute('id', 'firstName-error');
    expect(screen.getByText('Last name is required')).toBeInTheDocument();
    expect(screen.getByText('Username is required')).toBeInTheDocument();
    expect(screen.getByText('Email is required')).toBeInTheDocument();
    expect(screen.getByText('Password is required')).toBeInTheDocument();
    expect(screen.getByText('Please confirm your password')).toBeInTheDocument();
    expect(screen.getAllByRole('alert').length).toBe(6);
    expect(mockCreateInitialAdmin).not.toHaveBeenCalled();
  });

  it('moves focus to the first field still invalid when only some fields are filled in', async () => {
    render(<SetupWizard onComplete={vi.fn()} />);
    await screen.findByText('Initial configuration wizard');

    fireEvent.change(screen.getByLabelText(/First Name/, { selector: 'input' }), { target: { value: 'Ada' } });
    fireEvent.change(screen.getByLabelText(/Last Name/, { selector: 'input' }), { target: { value: 'Lovelace' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Admin & Continue' }));

    const usernameInput = screen.getByLabelText(/Username/, { selector: 'input' });
    await waitFor(() => expect(usernameInput).toHaveFocus());
    expect(screen.queryByText('First name is required')).not.toBeInTheDocument();
    expect(screen.queryByText('Last name is required')).not.toBeInTheDocument();
    expect(mockCreateInitialAdmin).not.toHaveBeenCalled();
  });

  it('clears a field error as soon as the user corrects that field', async () => {
    render(<SetupWizard onComplete={vi.fn()} />);
    await screen.findByText('Initial configuration wizard');

    fireEvent.click(screen.getByRole('button', { name: 'Create Admin & Continue' }));
    const firstNameInput = screen.getByLabelText(/First Name/, { selector: 'input' });
    await waitFor(() => expect(firstNameInput).toHaveFocus());
    expect(firstNameInput).toHaveAttribute('aria-invalid', 'true');

    fireEvent.change(firstNameInput, { target: { value: 'Ada' } });
    expect(firstNameInput).not.toHaveAttribute('aria-invalid');
    expect(firstNameInput).not.toHaveAttribute('aria-describedby');
    expect(screen.queryByText('First name is required')).not.toBeInTheDocument();
  });
});

/**
 * #1753 — at a 320x568 viewport, the wizard was centered with
 * `min-h-screen flex items-center justify-center` and no scroll container.
 * Its header began off-screen (y=-138) and the page could not scroll to
 * reveal the form or the submit button, because #root itself is fixed at
 * `height: 100vh; overflow: hidden` (see App.css) for the authenticated app
 * shell's internal scroll panes, and the wizard inherited that clipping
 * before any internal scroll region existed.
 *
 * jsdom does not perform real CSS layout, so this test locks in the
 * behavioural/structural contract instead of pixel measurements: at a
 * 320x568 viewport, a dedicated ancestor provides `overflow-y-auto` (so the
 * browser will let the page scroll), that scrollable ancestor is a distinct
 * element from the flex container that centers the card (avoiding the
 * classic bug where centering and scrolling on the same flex element only
 * lets you scroll to one side of the overflow), and the heading plus the
 * final submit button remain reachable in the DOM.
 */
describe('SetupWizard narrow mobile viewport scrolling (#1753)', () => {
  const originalInnerWidth = window.innerWidth;
  const originalInnerHeight = window.innerHeight;

  function setViewportSize(width: number, height: number) {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: width });
    Object.defineProperty(window, 'innerHeight', { writable: true, configurable: true, value: height });
    window.dispatchEvent(new Event('resize'));
  }

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
  });

  afterEach(() => {
    setViewportSize(originalInnerWidth, originalInnerHeight);
  });

  it('provides a scrollable ancestor separate from the centering container, reaching the submit button at 320x568', async () => {
    setViewportSize(320, 568);
    render(<SetupWizard onComplete={vi.fn()} />);

    await screen.findByText('Initial configuration wizard');
    expect(screen.getByRole('heading', { level: 1, name: 'Welcome to PrintFarmer' })).toBeInTheDocument();

    const submitButton = screen.getByRole('button', { name: 'Create Admin & Continue' });

    const scrollContainer = document.querySelector('.overflow-y-auto');
    expect(scrollContainer).not.toBeNull();
    // The scrollable element must not itself be the flex-centering element:
    // centering and scrolling on the same flex container clips content above
    // the fold and cannot be scrolled back into view (the root cause of #1753).
    expect(scrollContainer?.className).not.toContain('items-center');
    expect(scrollContainer?.contains(submitButton)).toBe(true);
  });

  it('keeps the same scroll structure at desktop widths (unchanged, not narrower-only)', async () => {
    setViewportSize(1280, 800);
    render(<SetupWizard onComplete={vi.fn()} />);

    await screen.findByText('Initial configuration wizard');

    const submitButton = screen.getByRole('button', { name: 'Create Admin & Continue' });
    const scrollContainer = document.querySelector('.overflow-y-auto');
    expect(scrollContainer).not.toBeNull();
    expect(scrollContainer?.contains(submitButton)).toBe(true);
  });
});
