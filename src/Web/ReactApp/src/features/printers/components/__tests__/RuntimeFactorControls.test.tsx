import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RuntimeFactorControls } from '@/features/printers/components/RuntimeFactorControls';
import { bumpAuthEpoch } from '@/common/auth/authEpoch';
import { PrinterBackend, type CommandResult, type Printer, type PrinterBackendCapabilitiesDto, type PrinterStatusUpdate } from '@/types/api';

const mocks = vi.hoisted(() => ({
  allowed: true, connected: true, server: '/api',
  status: vi.fn(), speed: vi.fn(), flow: vi.fn(), toast: vi.fn(), subscribe: vi.fn(),
  statuses: new Set<(status: PrinterStatusUpdate) => void>(),
  connections: new Set<(connected: boolean) => void>(),
}));
vi.mock('@/services/api', () => ({ apiClient: {
  getPrinterStatus: mocks.status, setPrinterSpeedFactor: mocks.speed, setPrinterFlowFactor: mocks.flow,
} }));
vi.mock('@/services/printer-signalr', () => ({ printerSignalRService: {
  get isConnected() { return mocks.connected; },
  connect: vi.fn().mockResolvedValue(undefined),
  subscribeToPrinter: mocks.subscribe,
  onPrinterStatusUpdate: (callback: (status: PrinterStatusUpdate) => void) => {
    mocks.statuses.add(callback);
    return () => mocks.statuses.delete(callback);
  },
  onConnectionStateChange: (callback: (connected: boolean) => void) => {
    mocks.connections.add(callback);
    return () => mocks.connections.delete(callback);
  },
} }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => mocks.allowed }) }));
vi.mock('@/common/utils/apiUrlHelpers', () => ({ getApiBaseUrl: () => mocks.server }));
vi.mock('sonner', () => ({ toast: { error: mocks.toast } }));

const printer: Printer = { id: 'p1', name: 'Printer', backend: PrinterBackend.Moonraker, isOnline: true, state: 'Printing', isEnabled: true };
const capabilities: PrinterBackendCapabilitiesDto = {
  printerId: 'p1', printerName: 'Printer', backend: PrinterBackend.Moonraker,
  supportsCamera: false, supportsFileDownload: false, supportsFileList: false, supportsFileUpload: false,
  supportsStartPrint: true, supportsControlOperations: true, supportsFileMetadata: false,
  supportsMovement: true, supportsTemperatureControl: true, supportsPrinterInformation: true,
  supportsHistory: true, supportsFilamentControl: false, supportsObjectExclusion: false,
  supportsSpeedFactorControl: true, supportsFlowFactorControl: true,
};
const live = (overrides: Partial<PrinterStatusUpdate> = {}): PrinterStatusUpdate => ({
  id: 'p1', isOnline: true, state: 'Printing', speedFactorPercent: 125, flowFactorPercent: 87.5, ...overrides,
});
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function emit(status: PrinterStatusUpdate) { act(() => mocks.statuses.forEach(callback => callback(status))); }
function connection(connected: boolean) {
  act(() => { mocks.connected = connected; mocks.connections.forEach(callback => callback(connected)); });
}
const clients: QueryClient[] = [];
function mount(p = printer, caps: PrinterBackendCapabilitiesDto | undefined = capabilities) {
  // Deliberately enable retries globally: these physical writes must opt out.
  const client = new QueryClient({ defaultOptions: { mutations: { retry: 3, retryDelay: 1 }, queries: { retry: false } } });
  clients.push(client);
  const tree = (nextPrinter: Printer, nextCaps?: PrinterBackendCapabilitiesDto) =>
    <QueryClientProvider client={client}><RuntimeFactorControls printer={nextPrinter} backendCapabilities={nextCaps} /></QueryClientProvider>;
  const result = render(tree(p, caps));
  return { ...result, client, update: (nextPrinter = p, nextCaps = caps) => result.rerender(tree(nextPrinter, nextCaps)) };
}
const speedInput = () => screen.getByRole('spinbutton', { name: 'Speed %' });
const flowInput = () => screen.getByRole('spinbutton', { name: 'Flow %' });
const speedApply = () => screen.getByRole('button', { name: 'Apply speed percentage' });
const flowApply = () => screen.getByRole('button', { name: 'Apply flow percentage' });
const observed = (kind: 'speed' | 'flow') => screen.getByLabelText(`Observed ${kind}`);
async function ready() { await waitFor(() => expect(speedApply()).toBeEnabled()); }
function draft(value: string, kind: 'speed' | 'flow' = 'speed') {
  fireEvent.change(kind === 'speed' ? speedInput() : flowInput(), { target: { value } });
}
async function apply(value: string, kind: 'speed' | 'flow' = 'speed') {
  draft(value, kind);
  fireEvent.click(kind === 'speed' ? speedApply() : flowApply());
  await waitFor(() => expect(kind === 'speed' ? mocks.speed : mocks.flow).toHaveBeenCalled());
}
beforeEach(() => {
  vi.clearAllMocks();
  mocks.allowed = true; mocks.connected = true; mocks.server = '/api';
  mocks.statuses.clear(); mocks.connections.clear();
  mocks.status.mockResolvedValue(live());
  mocks.speed.mockResolvedValue({ success: true });
  mocks.flow.mockResolvedValue({ success: true });
});
afterEach(() => { cleanup(); clients.splice(0).forEach(client => client.clear()); });

describe('runtime percentage controls', () => {
  it.each([
    [undefined, undefined, false, false],
    [false, false, false, false],
    [true, false, true, false],
    [false, true, false, true],
    [true, undefined, true, false],
    [undefined, true, false, true],
    [true, true, true, true],
  ])('gates capabilities independently (%s, %s)', async (speed, flow, showSpeed, showFlow) => {
    mount(printer, { ...capabilities, supportsSpeedFactorControl: speed, supportsFlowFactorControl: flow });
    await act(async () => {});
    expect(!!screen.queryByRole('spinbutton', { name: 'Speed %' })).toBe(showSpeed);
    expect(!!screen.queryByRole('spinbutton', { name: 'Flow %' })).toBe(showFlow);
    if (!showSpeed && !showFlow) expect(mocks.status).not.toHaveBeenCalled();
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(mocks.flow).not.toHaveBeenCalled();
  });

  it('hides controls for missing capabilities and for another printer capability response', () => {
    const view = mount(printer, { ...capabilities, supportsSpeedFactorControl: undefined, supportsFlowFactorControl: undefined });
    view.rerender(<QueryClientProvider client={view.client}><RuntimeFactorControls printer={printer} /></QueryClientProvider>);
    expect(screen.queryByRole('region', { name: 'Runtime overrides' })).not.toBeInTheDocument();
    view.update(printer, { ...capabilities, printerId: 'other' });
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each([
    { isOnline: false }, { isEnabled: false }, { inMaintenance: true },
    { state: undefined }, { state: 'Error' }, { state: 'Unknown' }, { state: 'Starting' }, { state: 'Shutdown' },
  ])('prevents writes in a blocked printer state %j', async patch => {
    mount({ ...printer, ...patch });
    await act(async () => {});
    expect(speedInput()).toBeDisabled();
    expect(flowApply()).toBeDisabled();
    draft('150');
    fireEvent.submit(speedInput().closest('form')!);
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each(['Idle', 'Ready', 'Standby', 'Complete', 'Completed', 'Cancelled', 'Printing', 'Paused'])('allows known %s state', async state => {
    mocks.status.mockResolvedValue(live({ state }));
    mount({ ...printer, state });
    await ready();
    await apply('125');
    expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 125);
  });

  it('requires Queue.Start and a live connection before submission', async () => {
    mocks.allowed = false;
    const view = mount();
    await act(async () => {});
    expect(speedApply()).toBeDisabled();
    mocks.allowed = true;
    view.update();
    await ready();
    connection(false);
    expect(speedInput()).toBeDisabled();
    expect(observed('speed')).toHaveTextContent('Unknown');
    fireEvent.submit(speedInput().closest('form')!);
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it('keeps missing values unknown, shows percentages without multiplying or clamping, and separates the draft', async () => {
    mocks.status.mockResolvedValue({ id: 'p1', isOnline: true, state: 'Printing' });
    mount();
    await ready();
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(observed('flow')).toHaveTextContent('Unknown');
    expect(speedInput()).toHaveValue(null);
    emit(live({ speedFactorPercent: 250.25, flowFactorPercent: 87.5 }));
    draft('120');
    expect(observed('speed')).toHaveTextContent('250.25%');
    expect(observed('flow')).toHaveTextContent('87.5%');
    expect(speedInput()).toHaveValue(120);
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each(['', '0', '0.99', '-1', '200.01', '125.001', 'Infinity', 'NaN'])('rejects invalid draft %s without a write', async value => {
    mount();
    await ready();
    draft(value);
    fireEvent.click(speedApply());
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter 1 to 200%');
    expect(speedInput()).toHaveAttribute('aria-invalid', 'true');
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(mocks.flow).not.toHaveBeenCalled();
  });

  it.each(['1', '200', '87.5', '125.01', '100'])('sends exact percentage %s and never calls the other factor', async value => {
    mount();
    await ready();
    await apply(value, 'flow');
    expect(mocks.flow).toHaveBeenCalledExactlyOnceWith('p1', Number(value));
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(observed('flow')).toHaveTextContent('87.5%');
  });

  it('supports labelled keyboard editing and explicit apply without sending on blur', async () => {
    const user = userEvent.setup();
    mount();
    await ready();
    await user.tab();
    expect(speedInput()).toHaveFocus();
    await user.type(speedInput(), '125.25');
    await user.tab();
    expect(speedApply()).toHaveFocus();
    expect(mocks.speed).not.toHaveBeenCalled();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 125.25));
    expect(speedInput()).toHaveAccessibleDescription(/1-200%/);
  });

  it('prevents duplicates and cross-factor writes while pending; ACK does not replace observation', async () => {
    const command = deferred<CommandResult>();
    mocks.speed.mockReturnValue(command.promise);
    mount();
    await ready();
    draft('140');
    fireEvent.submit(speedInput().closest('form')!);
    fireEvent.submit(speedInput().closest('form')!);
    await waitFor(() => expect(speedApply()).toBeDisabled());
    expect(flowApply()).toBeDisabled();
    expect(screen.getByText('Sending...')).toHaveAttribute('role', 'status');
    expect(mocks.speed).toHaveBeenCalledTimes(1);
    expect(observed('speed')).toHaveTextContent('125%');
    // Fresh GET after ACK still says 125; neither transport success nor draft is readback.
    await act(async () => command.resolve({ success: true }));
    expect(await screen.findByText(/Acknowledged 140%/)).toHaveAttribute('role', 'status');
    expect(observed('speed')).toHaveTextContent('125%');
    emit(live({ speedFactorPercent: 140 }));
    expect(observed('speed')).toHaveTextContent('140%');
    expect(observed('flow')).toHaveTextContent('87.5%');
  });

  it('serializes same-tick commands from two mounted printer surfaces', async () => {
    const command = deferred<CommandResult>();
    mocks.speed.mockReturnValue(command.promise);
    const view = mount();
    view.rerender(<QueryClientProvider client={view.client}>
      <RuntimeFactorControls printer={printer} backendCapabilities={capabilities} />
      <RuntimeFactorControls printer={printer} backendCapabilities={capabilities} />
    </QueryClientProvider>);
    await waitFor(() => screen.getAllByRole('spinbutton', { name: 'Speed %' }).forEach(input => expect(input).toBeEnabled()));
    const inputs = screen.getAllByRole('spinbutton', { name: 'Speed %' });
    inputs.forEach(input => fireEvent.change(input, { target: { value: '150' } }));
    // Submit before React can publish the shared pending count.
    act(() => inputs.forEach(input => fireEvent.submit(input.closest('form')!)));
    await waitFor(() => expect(mocks.speed).toHaveBeenCalledTimes(1));
    expect(mocks.flow).not.toHaveBeenCalled();
    await act(async () => command.resolve({ success: true }));
    expect(screen.getAllByLabelText('Observed speed')).toHaveLength(2);
    screen.getAllByLabelText('Observed speed').forEach(output => expect(output).toHaveTextContent('125%'));
  });

  it.each([
    [400, 'Percent must have at most two decimal places'],
    [401, 'Authentication required'], [403, 'Queue.Start denied'],
    [404, 'Printer not found or inaccessible'], [409, 'Active attempt ownership conflict'],
    [501, 'Flow unsupported'], [503, 'Backend outcome unconfirmed'],
  ])('surfaces HTTP %s and never automatically retries a physical write', async (statusCode, message) => {
    mocks.speed.mockRejectedValue({ statusCode, message });
    mount();
    await ready();
    await apply('140');
    expect(await screen.findByRole('alert')).toHaveTextContent(message);
    expect(screen.getByRole('alert')).toHaveTextContent(/check live readback.*before manually applying again/i);
    await new Promise(resolve => setTimeout(resolve, 25));
    expect(mocks.speed).toHaveBeenCalledTimes(1);
    expect(observed('speed')).toHaveTextContent('125%');
    expect(observed('flow')).toHaveTextContent('87.5%');
    expect(mocks.toast).toHaveBeenCalledWith(message);
    mocks.speed.mockResolvedValue({ success: true });
    fireEvent.click(speedApply());
    await screen.findByText(/Acknowledged 140%/);
    expect(mocks.speed).toHaveBeenCalledTimes(2);
  });

  it('does not treat a failed CommandResult as success', async () => {
    mocks.flow.mockResolvedValue({ success: false, error: 'Ownership fence changed' });
    mount();
    await ready();
    await apply('90', 'flow');
    expect(await screen.findByRole('alert')).toHaveTextContent('Ownership fence changed');
    expect(screen.queryByText(/Acknowledged/)).not.toBeInTheDocument();
    expect(observed('flow')).toHaveTextContent('87.5%');
  });

  it('preserves independent omitted fields, clears explicit null and follows external changes without overwriting drafts', async () => {
    mount();
    await ready();
    draft('150');
    emit({ id: 'p1', isOnline: true, speedFactorPercent: 130 });
    expect(observed('speed')).toHaveTextContent('130%');
    expect(observed('flow')).toHaveTextContent('87.5%');
    expect(speedInput()).toHaveValue(150);
    emit({ id: 'p1', isOnline: true, flowFactorPercent: null });
    expect(observed('flow')).toHaveTextContent('Unknown');
    expect(observed('speed')).toHaveTextContent('130%');
    emit({ id: 'other', isOnline: true, speedFactorPercent: 199 });
    expect(observed('speed')).toHaveTextContent('130%');
    emit({ id: 'p1', isOnline: true, speedFactorPercent: NaN });
    expect(observed('speed')).toHaveTextContent('Unknown');
  });

  it('fences each factor against older HTTP reads without losing an unaffected factor', async () => {
    const read = deferred<PrinterStatusUpdate>();
    mocks.status.mockReturnValueOnce(read.promise);
    mount();
    emit({ id: 'p1', isOnline: true, speedFactorPercent: 160 });
    await act(async () => read.resolve(live({ speedFactorPercent: 100, flowFactorPercent: 90 })));
    await ready();
    expect(observed('speed')).toHaveTextContent('160%');
    expect(observed('flow')).toHaveTextContent('90%');
  });

  it('discards observations, drafts and late ACKs on reconnect until fresh readback', async () => {
    const command = deferred<CommandResult>();
    const read = deferred<PrinterStatusUpdate>();
    mocks.speed.mockReturnValue(command.promise);
    mount();
    await ready();
    await apply('150');
    connection(false);
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(speedInput()).toHaveValue(null);
    mocks.status.mockReturnValueOnce(read.promise);
    connection(true);
    expect(observed('flow')).toHaveTextContent('Unknown');
    await act(async () => command.resolve({ success: true }));
    expect(screen.queryByText(/Acknowledged/)).not.toBeInTheDocument();
    await act(async () => read.resolve(live({ speedFactorPercent: null, flowFactorPercent: 93 })));
    await ready();
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(observed('flow')).toHaveTextContent('93%');
    expect(mocks.subscribe).toHaveBeenCalledWith('p1');
    expect(mocks.speed).toHaveBeenCalledTimes(1);
  });

  it('clears values immediately for printer-offline events and rejects old reads', async () => {
    const read = deferred<PrinterStatusUpdate>();
    mocks.status.mockReturnValue(read.promise);
    mount();
    emit({ id: 'p1', isOnline: false, speedFactorPercent: 120, flowFactorPercent: 90 });
    await act(async () => read.resolve(live()));
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(speedApply()).toBeDisabled();
  });

  it('does not resurrect a newly selected printer from its older pending HTTP snapshot', async () => {
    const oldRead = deferred<PrinterStatusUpdate>();
    const newRead = deferred<PrinterStatusUpdate>();
    mocks.status.mockReturnValueOnce(oldRead.promise).mockReturnValueOnce(newRead.promise);
    const view = mount();
    view.update({ ...printer, id: 'p2' }, { ...capabilities, printerId: 'p2' });
    await act(async () => oldRead.resolve(live({ speedFactorPercent: 199 })));
    expect(observed('speed')).toHaveTextContent('Unknown');
    await act(async () => newRead.resolve(live({ id: 'p2', speedFactorPercent: 108 })));
    await ready();
    expect(observed('speed')).toHaveTextContent('108%');
  });

  it('locks writes when current telemetry reports a disallowed state despite an older printable prop', async () => {
    mount();
    await ready();
    emit(live({ state: 'Starting' }));
    expect(speedInput()).toBeDisabled();
    expect(flowApply()).toBeDisabled();
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each(['printer', 'server', 'identity'] as const)('isolates late completions on %s switch', async target => {
    const command = deferred<CommandResult>();
    const read = deferred<PrinterStatusUpdate>();
    mocks.speed.mockReturnValue(command.promise);
    const view = mount();
    await ready();
    await apply('150');
    mocks.status.mockReturnValueOnce(read.promise);
    const id = target === 'printer' ? 'p2' : 'p1';
    if (target === 'server') mocks.server = '/other-api';
    if (target === 'identity') bumpAuthEpoch();
    view.update({ ...printer, id }, { ...capabilities, printerId: id });
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(speedInput()).toHaveValue(null);
    await act(async () => command.reject({ statusCode: 503, message: 'Old response' }));
    expect(mocks.toast).not.toHaveBeenCalled();
    expect(screen.queryByText(/Old response|Acknowledged/)).not.toBeInTheDocument();
    await act(async () => read.resolve(live({ id, speedFactorPercent: 105 })));
    await ready();
    expect(observed('speed')).toHaveTextContent('105%');
    await apply('110');
    expect(mocks.speed).toHaveBeenLastCalledWith(id, 110);
  });

  it('reports read errors without inventing observations or sending writes', async () => {
    mocks.status.mockRejectedValue({ message: 'Status unavailable' });
    mount();
    expect(await screen.findByRole('alert')).toHaveTextContent('Status unavailable');
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(speedApply()).toBeDisabled();
    expect(mocks.speed).not.toHaveBeenCalled();
  });
});
