import { AxiosError, AxiosHeaders, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RuntimeFactorControls } from '@/features/printers/components/RuntimeFactorControls';
import { bumpAuthEpoch } from '@/common/auth/authEpoch';
import { AUTH_REFRESH_TOKEN_KEY } from '@/common/auth/sessionTokens';
import { client } from '@/services/api/httpClient';
import { PrinterBackend, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';

const mocks = vi.hoisted(() => ({
  toast: vi.fn(),
  resetSession: vi.fn().mockResolvedValue(undefined),
}));
vi.mock('sonner', () => ({ toast: { error: mocks.toast } }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  resetAuthenticatedSignalRSession: mocks.resetSession,
}));
vi.mock('@/services/printer-signalr', () => ({ printerSignalRService: {
  isConnected: true,
  connect: vi.fn().mockResolvedValue(undefined),
  subscribeToPrinter: vi.fn().mockResolvedValue(undefined),
  onPrinterStatusUpdate: () => () => {},
  onConnectionStateChange: () => () => {},
} }));

const printer: Printer = { id: 'p1', name: 'Printer', backend: PrinterBackend.Moonraker, isOnline: true, state: 'Printing' };
const capabilities: PrinterBackendCapabilitiesDto = {
  printerId: printer.id, printerName: printer.name, backend: printer.backend,
  supportsCamera: false, supportsFileDownload: false, supportsFileList: false, supportsFileUpload: false,
  supportsStartPrint: true, supportsControlOperations: true, supportsFileMetadata: false,
  supportsMovement: true, supportsTemperatureControl: true, supportsPrinterInformation: true,
  supportsHistory: true, supportsFilamentControl: false, supportsObjectExclusion: false,
  supportsSpeedFactorControl: true, supportsFlowFactorControl: true,
};
const status = { id: 'p1', isOnline: true, state: 'Printing', speedFactorPercent: 125, flowFactorPercent: 87.5 };
const originalAdapter = client.defaults.adapter;
const originalPath = window.location.pathname;
const expired = vi.fn();
const clients: QueryClient[] = [];
let requests: { config: InternalAxiosRequestConfig; body: unknown; authorization: unknown }[];
let refreshCount: number;
let refreshFails: boolean;
let rejectCommand: (error: AxiosError) => void;

function response(config: InternalAxiosRequestConfig, data: unknown, status = 200): AxiosResponse {
  return { config, data, status, statusText: String(status), headers: new AxiosHeaders() };
}
function failure(config: InternalAxiosRequestConfig, status: number, message: string) {
  return new AxiosError(message, 'ERR_BAD_REQUEST', config, undefined,
    response(config, { success: false, error: message }, status));
}
function mount() {
  const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: 3, retryDelay: 1 } } });
  clients.push(queryClient);
  const tree = () => <QueryClientProvider client={queryClient}>
    <RuntimeFactorControls printer={printer} backendCapabilities={capabilities} />
  </QueryClientProvider>;
  const view = render(tree());
  return { ...view, updateIdentity: () => { bumpAuthEpoch(); view.rerender(tree()); } };
}
const input = (kind: 'speed' | 'flow') => screen.getByRole('slider', { name: kind === 'speed' ? 'Speed %' : 'Flow %' });
function apply(kind: 'speed' | 'flow', percent: string) {
  fireEvent.pointerDown(input(kind));
  fireEvent.change(input(kind), { target: { value: percent } });
  fireEvent.pointerUp(input(kind));
}
function expectNoFabricatedReadback() {
  expect(screen.getByLabelText('Observed speed')).toHaveTextContent('125%');
  expect(screen.getByLabelText('Observed flow')).toHaveTextContent('87.5%');
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  localStorage.setItem('auth-token', 'operator-a-access');
  requests = [];
  refreshCount = 0;
  refreshFails = false;
  window.history.replaceState({}, '', '/login');
  window.addEventListener('printfarmer:authentication-expired', expired);
  const firstCommand = new Promise<AxiosResponse>((_resolve, reject) => { rejectCommand = reject; });
  // Do not replace post/request or either interceptor: this adapter is the HTTP boundary.
  client.defaults.adapter = async config => {
    if (config.url === '/printers/p1/status') return response(config, status);
    if (config.url === '/auth/refresh') {
      refreshCount++;
      if (refreshFails) throw failure(config, 401, 'Refresh expired');
      return response(config, {
        success: true, token: 'operator-a-renewed', refreshToken: 'operator-a-rotated-refresh',
        user: { id: 'operator-a' },
      });
    }
    if (config.method === 'post' && /^\/printers\/p1\/(?:speed|flow)-factor$/.test(config.url ?? '')) {
      requests.push({ config, body: JSON.parse(config.data), authorization: config.headers.get('Authorization') });
      if (requests.length === 1) return firstCommand;
      return response(config, { success: true });
    }
    throw new Error(`Unexpected transport request: ${config.method} ${config.url}`);
  };
});
afterEach(() => {
  cleanup();
  clients.splice(0).forEach(queryClient => queryClient.clear());
  client.defaults.adapter = originalAdapter;
  localStorage.clear();
  window.removeEventListener('printfarmer:authentication-expired', expired);
  window.history.replaceState({}, '', originalPath);
});

describe.each(['speed', 'flow'] as const)('%s physical command authentication boundary', kind => {
  it('never replays A intent under B after a delayed 401; only a fresh B slider gesture may dispatch', async () => {
    const view = mount();
    await waitFor(() => expect(input(kind)).toBeEnabled());
    apply(kind, '140');
    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0]).toMatchObject({
      authorization: 'Bearer operator-a-access', body: { percent: 140 },
      config: { url: `/printers/p1/${kind}-factor`, allowAuthReplay: false },
    });
    localStorage.setItem('auth-token', 'operator-b-access');
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'operator-b-refresh');
    view.updateIdentity();
    await act(async () => rejectCommand(failure(requests[0].config, 401, 'Authentication required')));
    await waitFor(() => expect(input(kind)).toBeEnabled());
    expect(requests).toHaveLength(1);
    expect(refreshCount).toBe(0);
    expect(expired).not.toHaveBeenCalled();
    expect(mocks.resetSession).not.toHaveBeenCalled();
    expect(localStorage.getItem('auth-token')).toBe('operator-b-access');
    expect(mocks.toast).not.toHaveBeenCalled();
    expect(screen.queryByText(/Acknowledged|Authentication required/)).not.toBeInTheDocument();
    expectNoFabricatedReadback();

    apply(kind, '110');
    await screen.findByText(/Acknowledged 110%/);
    expect(requests).toHaveLength(2);
    expect(requests[1]).toMatchObject({ authorization: 'Bearer operator-b-access', body: { percent: 110 } });
    expectNoFabricatedReadback();
  });

  it('renews the same identity but rejects the old physical intent until a fresh slider gesture', async () => {
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'operator-a-refresh');
    mount();
    await waitFor(() => expect(input(kind)).toBeEnabled());
    apply(kind, '140');
    await waitFor(() => expect(requests).toHaveLength(1));
    await act(async () => rejectCommand(failure(requests[0].config, 401, 'Authentication required')));
    expect(await screen.findByRole('alert')).toHaveTextContent('Authentication required');
    expect(requests).toHaveLength(1);
    expect(refreshCount).toBe(1);
    expect(localStorage.getItem('auth-token')).toBe('operator-a-renewed');
    expect(expired).not.toHaveBeenCalled();
    expect(mocks.resetSession).not.toHaveBeenCalled();
    expect(screen.queryByText(/Acknowledged/)).not.toBeInTheDocument();
    expectNoFabricatedReadback();

    apply(kind, '115');
    await screen.findByText(/Acknowledged 115%/);
    expect(requests).toHaveLength(2);
    expect(requests[1]).toMatchObject({ authorization: 'Bearer operator-a-renewed', body: { percent: 115 } });
    expectNoFabricatedReadback();
  });

  it('still expires an invalid same-identity session when renewal fails, without replaying the command', async () => {
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'operator-a-refresh');
    refreshFails = true;
    mount();
    await waitFor(() => expect(input(kind)).toBeEnabled());
    apply(kind, '140');
    await waitFor(() => expect(requests).toHaveLength(1));
    await act(async () => rejectCommand(failure(requests[0].config, 401, 'Sign in again')));
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign in again');
    expect(requests).toHaveLength(1);
    expect(refreshCount).toBe(1);
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(expired).toHaveBeenCalledExactlyOnceWith(expect.any(Event));
    expect(mocks.resetSession).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Acknowledged/)).not.toBeInTheDocument();
    expectNoFabricatedReadback();
  });

  it.each(['503', 'timeout'] as const)('does not replay %s at the real transport, and permits deliberate manual recovery', async outcome => {
    mount();
    await waitFor(() => expect(input(kind)).toBeEnabled());
    apply(kind, '140');
    await waitFor(() => expect(requests).toHaveLength(1));
    const error = outcome === '503'
      ? failure(requests[0].config, 503, 'Backend outcome unconfirmed')
      : new AxiosError('Command timed out', 'ECONNABORTED', requests[0].config);
    await act(async () => rejectCommand(error));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Outcome may be unknown/);
    expect(requests).toHaveLength(1);
    expect(refreshCount).toBe(0);
    expect(screen.queryByText(/Acknowledged/)).not.toBeInTheDocument();
    expectNoFabricatedReadback();
    apply(kind, '120');
    await screen.findByText(/Acknowledged 120%/);
    expect(requests).toHaveLength(2);
    expect(requests[1]).toMatchObject({ authorization: 'Bearer operator-a-access', body: { percent: 120 } });
    expectNoFabricatedReadback();
  });
});
