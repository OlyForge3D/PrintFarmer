import type { ReactElement } from 'react';
import { act, cleanup, fireEvent, render as renderTree, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterControlsMode, PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { USER_SETTINGS_KEY } from '@/features/settings/hooks/useUserSettings';
import { bumpAuthEpoch } from '@/common/auth/authEpoch';
import type { UpdateUserSettingsRequest, UserSettingsResponse } from '@/features/settings/types';

const mockGet = vi.fn();
const mockPut = vi.fn();
vi.mock('@/services/api', () => ({ apiClient: {
  get: (...args: unknown[]) => mockGet(...args), put: (...args: unknown[]) => mockPut(...args),
} }));
vi.mock('sonner', () => ({ toast: { error: vi.fn() } }));

let serverSettings: UserSettingsResponse;
const clients: QueryClient[] = [];
function render(element: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  clients.push(client);
  return { client, ...renderTree(<QueryClientProvider client={client}>{element}</QueryClientProvider>) };
}
function BothSurfaces() {
  return <><section aria-label="detail"><PrinterControlsMode /><PrinterMotionHelp absolute /></section><section aria-label="sidebar"><PrinterControlsMode /><PrinterMotionHelp absolute /></section></>;
}
function MotionControls() {
  return <MovementControlSection
    moveX="" moveY="" moveZ="" step={1} extrudeStep={5} extrudeSpeed={5} extrudeMinTemp={170}
    movementActionPending={false} canMove canDisableMotors canSetStep canManualMove canExtrude
    onMoveXChange={vi.fn()} onMoveYChange={vi.fn()} onMoveZChange={vi.fn()} onStepChange={vi.fn()}
    onExtrudeStepChange={vi.fn()} onExtrudeSpeedChange={vi.fn()} onMove={vi.fn()}
    onHome={vi.fn()} onDisableMotors={vi.fn()} onExtrude={vi.fn()}
  />;
}

beforeEach(() => {
  mockGet.mockReset(); mockPut.mockReset();
  serverSettings = {
    userId: 'mode-user', theme: 'dark', locale: 'en', itemsPerPage: 25,
    defaultSlicerPreset: 'preset', printablesUsername: 'maker', rowVersion: 'v1', printerControlMode: 'Guided',
  };
  mockGet.mockImplementation(async () => ({ data: serverSettings }));
  mockPut.mockImplementation(async (_url: string, body: UpdateUserSettingsRequest) => {
    serverSettings = { ...serverSettings, ...body, rowVersion: 'v2' };
    return { data: serverSettings };
  });
});
afterEach(() => {
  cleanup();
  clients.splice(0).forEach(client => client.clear());
  vi.restoreAllMocks();
});

describe('account-backed printer controls mode', () => {
  it('defaults to Guided while loading without locking normal motion controls', async () => {
    let finish!: () => void;
    mockGet.mockImplementation(() => new Promise(resolve => { finish = () => resolve({ data: serverSettings }); }));
    render(<MotionControls />);
    expect(screen.getByRole('button', { name: 'Guided' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('status')).toHaveTextContent(/Loading account preference/);
    expect(screen.getByRole('button', { name: 'Expert' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Home all axes' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Jog Y positive' })).toBeEnabled();
    await act(async () => finish());
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
  });

  it('loads once, synchronizes simultaneous mounts, and saves only mode plus the reviewed revision', async () => {
    render(<BothSurfaces />);
    const detail = within(screen.getByRole('region', { name: 'detail' }));
    const sidebar = within(screen.getByRole('region', { name: 'sidebar' }));
    await waitFor(() => expect(detail.getByRole('button', { name: 'Expert' })).toBeEnabled());
    expect(mockGet).toHaveBeenCalledExactlyOnceWith('/settings/user');
    expect(sidebar.getByText(/Jog moves by/)).toBeVisible();
    fireEvent.click(detail.getByRole('button', { name: 'Expert' }));
    await waitFor(() => expect(mockPut).toHaveBeenCalledExactlyOnceWith('/settings/user', { printerControlMode: 'Expert', rowVersion: 'v1' }));
    await waitFor(() => expect(sidebar.getByRole('button', { name: 'Expert' })).toBeEnabled());
    expect(sidebar.getByRole('button', { name: 'Expert' })).toHaveAttribute('aria-pressed', 'true');
    expect(sidebar.getByText(/Jog moves by/)).not.toBeVisible();
    const summary = sidebar.getByText('Motion help');
    expect(summary.tagName).toBe('SUMMARY');
    fireEvent.click(summary);
    expect(sidebar.getByText(/Jog moves by/)).toBeVisible();
    expect(serverSettings.theme).toBe('dark');
    expect(serverSettings.printablesUsername).toBe('maker');
  });

  it('restores the saved account preference in a fresh device query cache, ignoring browser preferences', async () => {
    localStorage.setItem('pf.printer-controls.mode', 'expert');
    const storageWrite = vi.spyOn(window.localStorage, 'setItem');
    const first = render(<PrinterControlsMode />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
    expect(screen.getByRole('button', { name: 'Guided' })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Expert' }));
    await waitFor(() => expect(serverSettings.printerControlMode).toBe('Expert'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
    first.unmount();
    render(<PrinterControlsMode />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toHaveAttribute('aria-pressed', 'true'));
    expect(mockGet).toHaveBeenCalledTimes(2);
    expect(storageWrite).not.toHaveBeenCalled();
    localStorage.removeItem('pf.printer-controls.mode');
  });

  it('uses Guided for an omitted preference without treating it as a motion prerequisite', async () => {
    delete serverSettings.printerControlMode;
    render(<MotionControls />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
    expect(screen.getByRole('button', { name: 'Guided' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Home all axes' })).toBeEnabled();
  });

  it('shares pending selection and prevents overlapping same-tab saves', async () => {
    let finish!: () => void;
    mockPut.mockImplementation((_url: string, body: UpdateUserSettingsRequest) => new Promise(resolve => {
      finish = () => resolve({ data: { ...serverSettings, ...body, rowVersion: 'v2' } });
    }));
    render(<BothSurfaces />);
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Expert' })[0]).toBeEnabled());
    fireEvent.click(screen.getAllByRole('button', { name: 'Expert' })[0]);
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Expert' })[1]).toHaveAttribute('aria-pressed', 'true'));
    for (const button of screen.getAllByRole('button', { name: 'Expert' })) expect(button).toBeDisabled();
    fireEvent.click(screen.getAllByRole('button', { name: 'Guided' })[1]);
    expect(mockPut).toHaveBeenCalledTimes(1);
    await act(async () => finish());
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Expert' })[0]).toBeEnabled());
  });

  it('reports load failure, keeps motion available, and supports reloading preferences', async () => {
    mockGet.mockRejectedValueOnce(new Error('Offline'));
    render(<MotionControls />);
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/Could not load your control mode/));
    expect(screen.getByRole('button', { name: 'Home all axes' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Jog Y positive' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Expert' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Reload preferences' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('shares save errors, returns to the saved mode, and allows an explicit retry', async () => {
    mockPut.mockRejectedValueOnce(new Error('Offline'));
    render(<BothSurfaces />);
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Expert' })[0]).toBeEnabled());
    fireEvent.click(screen.getAllByRole('button', { name: 'Expert' })[0]);
    await waitFor(() => expect(screen.getAllByRole('status')[0]).toHaveTextContent(/Could not save your control mode/));
    expect(screen.getAllByRole('status')).toHaveLength(2);
    for (const button of screen.getAllByRole('button', { name: 'Guided' })) expect(button).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getAllByRole('button', { name: 'Expert' })[1]);
    await waitFor(() => expect(serverSettings.printerControlMode).toBe('Expert'));
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument());
  });

  it('surfaces revision conflicts without silently retrying a write', async () => {
    mockPut.mockRejectedValueOnce({ statusCode: 409, message: 'Conflict' });
    render(<PrinterControlsMode />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Expert' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/preferences changed elsewhere/));
    expect(mockPut).toHaveBeenCalledTimes(1);
  });

  it('does not carry a pending selection or late save response across account changes', async () => {
    let finish!: () => void;
    mockPut.mockImplementation(() => new Promise(resolve => {
      finish = () => resolve({ data: { ...serverSettings, printerControlMode: 'Expert' } });
    }));
    const { client } = render(<PrinterControlsMode />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Expert' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Expert' })).toHaveAttribute('aria-pressed', 'true'));
    act(() => {
      bumpAuthEpoch();
      client.setQueryData(USER_SETTINGS_KEY, { ...serverSettings, userId: 'other-user', printerControlMode: 'Guided' });
    });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guided' })).toHaveAttribute('aria-pressed', 'true'));
    await act(async () => finish());
    expect(client.getQueryData<UserSettingsResponse>(USER_SETTINGS_KEY)?.userId).toBe('other-user');
    expect(screen.getByRole('button', { name: 'Guided' })).toHaveAttribute('aria-pressed', 'true');
  });
});
