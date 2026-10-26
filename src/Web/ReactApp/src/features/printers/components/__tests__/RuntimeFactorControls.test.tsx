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
const speedInput = () => screen.getByRole('slider', { name: 'Speed %' });
const flowInput = () => screen.getByRole('slider', { name: 'Flow %' });
const observed = (kind: 'speed' | 'flow') => screen.getByLabelText(`Observed ${kind}`);
async function ready() { await waitFor(() => expect(speedInput()).toBeEnabled()); }
function draft(value: string, kind: 'speed' | 'flow' = 'speed') {
  const slider = kind === 'speed' ? speedInput() : flowInput();
  fireEvent.pointerDown(slider);
  fireEvent.change(slider, { target: { value } });
}
async function apply(value: string, kind: 'speed' | 'flow' = 'speed') {
  draft(value, kind);
  fireEvent.pointerUp(kind === 'speed' ? speedInput() : flowInput());
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
    expect(!!screen.queryByRole('slider', { name: 'Speed %' })).toBe(showSpeed);
    expect(!!screen.queryByRole('slider', { name: 'Flow %' })).toBe(showFlow);
    if (!showSpeed && !showFlow) expect(mocks.status).not.toHaveBeenCalled();
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(mocks.flow).not.toHaveBeenCalled();
  });

  it('hides controls for missing capabilities and for another printer capability response', () => {
    const view = mount(printer, { ...capabilities, supportsSpeedFactorControl: undefined, supportsFlowFactorControl: undefined });
    view.rerender(<QueryClientProvider client={view.client}><RuntimeFactorControls printer={printer} /></QueryClientProvider>);
    expect(screen.queryByRole('region', { name: 'Tuning' })).not.toBeInTheDocument();
    view.update(printer, { ...capabilities, printerId: 'other' });
    expect(screen.queryByRole('slider')).not.toBeInTheDocument();
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each([
    { isOnline: false }, { isEnabled: false }, { inMaintenance: true },
    { state: 'Shutdown' },
  ])('prevents writes in a blocked printer state %j', async patch => {
    mount({ ...printer, ...patch });
    await act(async () => {});
    expect(speedInput()).toBeDisabled();
    expect(flowInput()).toBeDisabled();
    draft('150');
    fireEvent.pointerUp(speedInput());
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each(['Printing', 'printing', 'PRINTING', 'Idle', 'Ready', 'Paused', 'Completed'])('allows native tuning in the %s state', async state => {
    mocks.status.mockResolvedValue(live({ state }));
    mount({ ...printer, state });
    await ready();
    await apply('130');
    expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 130);
  });

  it('requires Queue.Start but not a live readback connection before submission', async () => {
    mocks.allowed = false;
    const view = mount();
    await act(async () => {});
    expect(speedInput()).toBeDisabled();
    mocks.allowed = true;
    view.update();
    await ready();
    connection(false);
    expect(speedInput()).toBeEnabled();
    expect(observed('speed')).toHaveTextContent('Unknown');
    await apply('130');
    expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 130);
  });

  it('keeps missing values unknown, shows percentages without multiplying or clamping, and separates the draft', async () => {
    mocks.status.mockResolvedValue({ id: 'p1', isOnline: true, state: 'Printing' });
    mount();
    await ready();
    expect(observed('speed')).toHaveTextContent('Unknown');
    expect(observed('flow')).toHaveTextContent('Unknown');
    expect(speedInput()).toHaveAttribute('aria-valuetext', 'Unknown');
    emit(live({ speedFactorPercent: 250.25, flowFactorPercent: 87.5 }));
    draft('120');
    expect(observed('speed')).toHaveTextContent('250.25%');
    expect(observed('flow')).toHaveTextContent('87.5%');
    expect(speedInput()).toHaveValue('120');
    expect(screen.getByLabelText('Selected speed')).toHaveTextContent('120%');
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it('uses labelled bounded sliders without numeric inputs, Apply buttons or redundant explanations', async () => {
    mount();
    await ready();
    expect(screen.getByRole('button', { name: /Collapse Tuning/ })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Apply/ })).not.toBeInTheDocument();
    for (const slider of [speedInput(), flowInput()]) {
      expect(slider).toHaveAttribute('min', '1');
      expect(slider).toHaveAttribute('max', '200');
      expect(slider).toHaveAttribute('step', '0.01');
    }
    expect(screen.getAllByText('1%', { exact: true })).toHaveLength(2);
    expect(screen.getAllByText('200%', { exact: true })).toHaveLength(2);
    expect(screen.queryByText(/New value:|Submit access|Observed values are live readback|100% is normal/)).not.toBeInTheDocument();
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(mocks.flow).not.toHaveBeenCalled();
  });

  it.each(['1', '200', '87.5', '125.01', '100'])('sends exact percentage %s and never calls the other factor', async value => {
    mocks.status.mockResolvedValue(live({ flowFactorPercent: 90 }));
    mount();
    await ready();
    await apply(value, 'flow');
    expect(mocks.flow).toHaveBeenCalledExactlyOnceWith('p1', Number(value));
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(observed('flow')).toHaveTextContent('90%');
  });

  it.each(['mouse', 'touch', 'pen'])('sends only the final %s drag value once on release, never on intermediate movement', async pointerType => {
    mount();
    await ready();
    const slider = speedInput();
    fireEvent.pointerDown(slider, { pointerId: 1, pointerType });
    for (const value of ['130', '145.25', '160']) {
      fireEvent.change(slider, { target: { value } });
    }
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Selected speed')).toHaveTextContent('160%');
    expect(observed('speed')).toHaveTextContent('125%');
    fireEvent.pointerUp(slider, { pointerId: 1, pointerType });
    fireEvent.lostPointerCapture(slider, { pointerId: 1, pointerType });
    fireEvent.blur(slider);
    await waitFor(() => expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 160));
    expect(mocks.flow).not.toHaveBeenCalled();
  });

  it.each(['pointercancel', 'lostpointercapture', 'escape'])('discards a %s gesture without sending it on the subsequent release or blur', async cancel => {
    mount();
    await ready();
    draft('150');
    if (cancel === 'pointercancel') fireEvent.pointerCancel(speedInput());
    if (cancel === 'lostpointercapture') fireEvent.lostPointerCapture(speedInput());
    if (cancel === 'escape') fireEvent.keyDown(speedInput(), { key: 'Escape' });
    fireEvent.pointerUp(speedInput());
    fireEvent.blur(speedInput());
    expect(speedInput()).toHaveValue('125');
    expect(screen.queryByLabelText('Selected speed')).not.toBeInTheDocument();
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it('does not send a click, focus change or key release without an adjustment', async () => {
    mount();
    await ready();
    fireEvent.pointerDown(speedInput());
    fireEvent.pointerUp(speedInput());
    fireEvent.keyUp(speedInput(), { key: 'ArrowRight' });
    fireEvent.blur(speedInput());
    expect(mocks.speed).not.toHaveBeenCalled();
    expect(mocks.flow).not.toHaveBeenCalled();
  });

  it('commits an assistive keyboard change on blur once without needing an Apply button', async () => {
    mount();
    await ready();
    fireEvent.change(flowInput(), { target: { value: '95.5' } });
    expect(mocks.flow).not.toHaveBeenCalled();
    fireEvent.blur(flowInput());
    fireEvent.blur(flowInput());
    await waitFor(() => expect(mocks.flow).toHaveBeenCalledExactlyOnceWith('p1', 95.5));
  });

  it('commits once when a keyboard adjustment stops, not for intermediate repeats or the following blur', async () => {
    const user = userEvent.setup();
    mount();
    await ready();
    await user.tab();
    expect(screen.getByRole('button', { name: /Collapse Tuning/ })).toHaveFocus();
    await user.tab();
    expect(speedInput()).toHaveFocus();
    fireEvent.keyDown(speedInput(), { key: 'ArrowRight' });
    fireEvent.change(speedInput(), { target: { value: '125.01' } });
    fireEvent.keyDown(speedInput(), { key: 'ArrowRight', repeat: true });
    fireEvent.change(speedInput(), { target: { value: '125.25' } });
    expect(mocks.speed).not.toHaveBeenCalled();
    fireEvent.keyUp(speedInput(), { key: 'ArrowRight' });
    fireEvent.blur(speedInput());
    await waitFor(() => expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 125.25));
  });

  it('prevents duplicates and cross-factor writes while pending; ACK does not replace observation', async () => {
    const command = deferred<CommandResult>();
    mocks.speed.mockReturnValue(command.promise);
    mount();
    await ready();
    draft('140');
    fireEvent.pointerUp(speedInput());
    fireEvent.pointerUp(speedInput());
    fireEvent.blur(speedInput());
    await waitFor(() => expect(speedInput()).toBeDisabled());
    expect(flowInput()).toBeDisabled();
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
    await waitFor(() => screen.getAllByRole('slider', { name: 'Speed %' }).forEach(input => expect(input).toBeEnabled()));
    const inputs = screen.getAllByRole('slider', { name: 'Speed %' });
    inputs.forEach(input => {
      fireEvent.pointerDown(input);
      fireEvent.change(input, { target: { value: '150' } });
    });
    // Release before React can publish the shared pending count.
    act(() => inputs.forEach(input => fireEvent.pointerUp(input)));
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
    expect(screen.getByRole('alert')).toHaveTextContent(/check live readback.*before adjusting again/i);
    await new Promise(resolve => setTimeout(resolve, 25));
    expect(mocks.speed).toHaveBeenCalledTimes(1);
    expect(observed('speed')).toHaveTextContent('125%');
    expect(observed('flow')).toHaveTextContent('87.5%');
    expect(mocks.toast).toHaveBeenCalledWith(message);
    mocks.speed.mockResolvedValue({ success: true });
    await apply('145');
    await screen.findByText(/Acknowledged 145%/);
    expect(mocks.speed).toHaveBeenCalledTimes(2);
    expect(mocks.speed).toHaveBeenLastCalledWith('p1', 145);
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
    expect(speedInput()).toHaveValue('150');
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
    expect(speedInput()).toHaveAttribute('aria-valuetext', 'Unknown');
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
    expect(speedInput()).toBeDisabled();
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

  it('does not lock native writes on a synthetic Starting state', async () => {
    mount();
    await ready();
    emit(live({ state: 'Starting' }));
    expect(speedInput()).toBeEnabled();
    expect(flowInput()).toBeEnabled();
    expect(mocks.speed).not.toHaveBeenCalled();
  });

  it.each(['Idle', 'Paused', 'Complete', 'Error'])('preserves a deliberate gesture when live state changes to %s', async state => {
    mount();
    await ready();
    const oldSlider = speedInput();
    draft('160');
    emit(live({ state }));
    expect(speedInput()).toBeEnabled();
    fireEvent.pointerUp(oldSlider);
    fireEvent.pointerUp(speedInput());
    fireEvent.blur(speedInput());
    await waitFor(() => expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 160));
    emit(live({ state: 'Printing' }));
    await ready();
    fireEvent.pointerUp(speedInput());
    fireEvent.blur(speedInput());
    expect(mocks.speed).toHaveBeenCalledTimes(1);
    expect(screen.queryByLabelText('Selected speed')).not.toBeInTheDocument();
  });

  it.each(['Idle', 'Paused', 'Complete', 'Error', undefined])('allows native writes without fresh Printing telemetry (%s)', async state => {
    mocks.status.mockResolvedValue(live({ state }));
    mount();
    await act(async () => {});
    expect(speedInput()).toBeEnabled();
    expect(flowInput()).toBeEnabled();
    draft('160');
    fireEvent.pointerUp(speedInput());
    await waitFor(() => expect(mocks.speed).toHaveBeenCalledExactlyOnceWith('p1', 160));
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
    expect(speedInput()).toHaveAttribute('aria-valuetext', 'Unknown');
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
    expect(speedInput()).toBeEnabled();
    expect(mocks.speed).not.toHaveBeenCalled();
  });
});
