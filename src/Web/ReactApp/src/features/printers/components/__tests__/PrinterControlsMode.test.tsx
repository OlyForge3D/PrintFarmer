import { useEffect, type ReactElement } from 'react';
import { act, cleanup, render as renderTree, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { USER_SETTINGS_KEY, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';
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
function MotionControls() {
  return <MovementControlSection
    moveX="" moveY="" moveZ="" step={1} extrudeStep={5} extrudeSpeed={5} extrudeMinTemp={170}
    movementActionPending={false} canMove canDisableMotors canSetStep canManualMove canExtrude
    onMoveXChange={vi.fn()} onMoveYChange={vi.fn()} onMoveZChange={vi.fn()} onStepChange={vi.fn()}
    onExtrudeStepChange={vi.fn()} onExtrudeSpeedChange={vi.fn()} onMove={vi.fn()}
    onHome={vi.fn()} onDisableMotors={vi.fn()} onExtrude={vi.fn()}
  />;
}
const settingsSaver: { save?: (body: UpdateUserSettingsRequest) => void } = {};
function SettingsSaver() {
  const update = useUpdateUserSettings();
  useEffect(() => { settingsSaver.save = body => update.mutate(body); });
  return null;
}
function BothSurfaces() {
  return <>
    <SettingsSaver />
    <section aria-label="detail"><MotionControls /></section>
    <section aria-label="sidebar"><PrinterMotionHelp absolute /></section>
  </>;
}
const noLocalSelector = () => {
  expect(screen.queryByRole('group', { name: 'Printer controls mode' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Guided' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Expert' })).not.toBeInTheDocument();
  expect(screen.queryByText(/Motion help/i)).not.toBeInTheDocument();
};

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
  settingsSaver.save = undefined;
  vi.restoreAllMocks();
});

describe('User Settings printer control mode on printer surfaces', () => {
  it('shows Guided help on both surfaces with no local mode selector', async () => {
    render(<BothSurfaces />);
    const detail = within(screen.getByRole('region', { name: 'detail' }));
    const sidebar = within(screen.getByRole('region', { name: 'sidebar' }));
    await waitFor(() => expect(mockGet).toHaveBeenCalledExactlyOnceWith('/settings/user'));
    expect(detail.getByText(/Jog moves by/)).toBeVisible();
    expect(sidebar.getByText(/Jog moves by/)).toBeVisible();
    noLocalSelector();
  });

  it('renders no motion help on either surface in Expert mode', async () => {
    serverSettings = { ...serverSettings, printerControlMode: 'Expert' };
    render(<BothSurfaces />);
    await waitFor(() => expect(mockGet).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText(/Jog moves by/)).not.toBeInTheDocument());
    noLocalSelector();
    expect(screen.getByRole('button', { name: 'Home all axes' })).toBeEnabled();
  });

  it('follows a User Settings save on both surfaces without writing browser storage', async () => {
    const storageWrite = vi.spyOn(window.localStorage, 'setItem');
    render(<BothSurfaces />);
    await waitFor(() => expect(screen.getAllByText(/Jog moves by/)).toHaveLength(2));
    act(() => settingsSaver.save!({ printerControlMode: 'Expert', rowVersion: 'v1' }));
    await waitFor(() => expect(screen.queryByText(/Jog moves by/)).not.toBeInTheDocument());
    await waitFor(() => expect(serverSettings.printerControlMode).toBe('Expert'));
    expect(screen.queryByText(/Jog moves by/)).not.toBeInTheDocument();
    expect(storageWrite).not.toHaveBeenCalled();
  });

  it('defaults to Guided while loading and on load failure without locking motion', async () => {
    mockGet.mockRejectedValueOnce(new Error('Offline'));
    render(<MotionControls />);
    expect(screen.getByText(/Jog moves by/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Home all axes' })).toBeEnabled();
    await waitFor(() => expect(mockGet).toHaveBeenCalled());
    expect(screen.getByText(/Jog moves by/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Jog Y positive' })).toBeEnabled();
    noLocalSelector();
  });

  it('does not carry a pending old-account mode across account changes', async () => {
    let finish!: () => void;
    mockPut.mockImplementation(() => new Promise(resolve => {
      finish = () => resolve({ data: { ...serverSettings, printerControlMode: 'Expert' } });
    }));
    const { client } = render(<BothSurfaces />);
    await waitFor(() => expect(screen.getAllByText(/Jog moves by/)).toHaveLength(2));
    act(() => settingsSaver.save!({ printerControlMode: 'Expert', rowVersion: 'v1' }));
    await waitFor(() => expect(screen.queryByText(/Jog moves by/)).not.toBeInTheDocument());
    act(() => {
      bumpAuthEpoch();
      client.setQueryData(USER_SETTINGS_KEY, { ...serverSettings, userId: 'other-user', printerControlMode: 'Guided' });
    });
    await waitFor(() => expect(screen.getAllByText(/Jog moves by/)).toHaveLength(2));
    await act(async () => finish());
    expect(client.getQueryData<UserSettingsResponse>(USER_SETTINGS_KEY)?.userId).toBe('other-user');
    expect(screen.getAllByText(/Jog moves by/)).toHaveLength(2);
  });
});
