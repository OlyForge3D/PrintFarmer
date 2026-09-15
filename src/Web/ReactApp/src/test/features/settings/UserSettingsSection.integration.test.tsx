import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserSettingsSection } from '@/features/settings/components/UserSettingsSection';
import { PrinterControlsMode, PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import { USER_SETTINGS_KEY } from '@/features/settings/hooks/useUserSettings';
import type { UpdateUserSettingsRequest, UserSettingsResponse } from '@/features/settings/types';

const mockGet = vi.fn();
const mockPut = vi.fn();
vi.mock('@/services/api', () => ({ apiClient: {
  get: (...args: unknown[]) => mockGet(...args), put: (...args: unknown[]) => mockPut(...args),
} }));
vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

let server: UserSettingsResponse;
let client: QueryClient;
function mount() {
  return render(<QueryClientProvider client={client}>
    <UserSettingsSection />
    <section aria-label="Printer sidebar"><PrinterControlsMode /><PrinterMotionHelp absolute /></section>
  </QueryClientProvider>);
}
const sidebar = () => within(screen.getByRole('region', { name: 'Printer sidebar' }));
const save = () => screen.getByRole('button', { name: 'Save Preferences' });

beforeEach(() => {
  mockGet.mockReset(); mockPut.mockReset();
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  server = { userId: 'user', theme: 'matrix', locale: 'en', itemsPerPage: 25,
    printerControlMode: 'Guided', defaultSlicerPreset: 'quality', printablesUsername: 'maker', rowVersion: 'v1' };
  mockGet.mockImplementation(async () => ({ data: server }));
  mockPut.mockImplementation(async (_url: string, body: UpdateUserSettingsRequest) => {
    server = { ...server, ...body, rowVersion: 'v2' };
    return { data: server };
  });
});
afterEach(() => { cleanup(); client.clear(); });

describe('Preferences shared account persistence', () => {
  it('stages mode until save, updates printer hints, then follows shortcut changes without stale form values', async () => {
    mount();
    await screen.findByRole('radio', { name: 'Guided' });
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    expect(mockPut).not.toHaveBeenCalled();
    expect(sidebar().getByText(/Jog moves by/)).toBeVisible();
    fireEvent.click(save());
    await waitFor(() => expect(server.printerControlMode).toBe('Expert'));
    await waitFor(() => expect(save()).toBeEnabled());
    expect(sidebar().getByText(/Jog moves by/)).not.toBeVisible();
    expect(mockPut).toHaveBeenCalledWith('/settings/user', {
      theme: 'matrix', printerControlMode: 'Expert', locale: 'en', itemsPerPage: 25,
      defaultSlicerPreset: 'quality', printablesUsername: 'maker', rowVersion: 'v1',
    });
    fireEvent.click(sidebar().getByRole('button', { name: 'Guided' }));
    await waitFor(() => expect(screen.getByRole('radio', { name: 'Guided' })).toBeChecked());
    expect(sidebar().getByText(/Jog moves by/)).toBeVisible();
    fireEvent.change(screen.getByLabelText('Items per page'), { target: { value: '50' } });
    await waitFor(() => expect(save()).toBeEnabled());
    fireEvent.click(save());
    await waitFor(() => expect(server.itemsPerPage).toBe(50));
    expect(server.printerControlMode).toBe('Guided');
    expect(server.theme).toBe('matrix');
    expect(server.defaultSlicerPreset).toBe('quality');
  });

  it.each(['preferences', 'shortcut'])('blocks overlapping writes started from %s', async (origin) => {
    let finish!: () => void;
    mockPut.mockImplementation((_url: string, body: UpdateUserSettingsRequest) => new Promise(resolve => {
      finish = () => resolve({ data: { ...server, ...body, rowVersion: 'v2' } });
    }));
    mount();
    await screen.findByRole('radio', { name: 'Guided' });
    if (origin === 'preferences') {
      fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
      fireEvent.click(save());
    } else {
      fireEvent.click(sidebar().getByRole('button', { name: 'Expert' }));
    }
    await waitFor(() => expect(mockPut).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('button', { name: /Saving\.\.\.|Save Preferences/ })).toBeDisabled();
    expect(screen.getByRole('radio', { name: 'Guided' })).toBeDisabled();
    expect(sidebar().getByRole('button', { name: 'Guided' })).toBeDisabled();
    fireEvent.click(sidebar().getByRole('button', { name: 'Guided' }));
    fireEvent.submit(screen.getByRole('form', { name: 'User preferences' }));
    expect(mockPut).toHaveBeenCalledTimes(1);
    await act(async () => finish());
    await waitFor(() => expect(save()).toBeEnabled());
  });

  it('retains drafts after failure and retries explicitly', async () => {
    mockPut.mockRejectedValueOnce(new Error('Offline'));
    mount();
    await screen.findByRole('radio', { name: 'Guided' });
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    fireEvent.click(save());
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Offline'));
    expect(screen.getByRole('radio', { name: 'Expert' })).toBeChecked();
    expect(client.getQueryData<UserSettingsResponse>(USER_SETTINGS_KEY)?.printerControlMode).toBe('Guided');
    fireEvent.click(save());
    await waitFor(() => expect(server.printerControlMode).toBe('Expert'));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('handles a conflict without replay, preserves edits through a failed reload, and reloads explicitly', async () => {
    mockPut.mockImplementationOnce(async () => {
      server = { ...server, locale: 'fr', printablesUsername: 'updated', rowVersion: 'remote-v2' };
      throw { statusCode: 409, message: 'Conflict' };
    });
    mount();
    await screen.findByRole('radio', { name: 'Guided' });
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    fireEvent.change(screen.getByLabelText('Printables username'), { target: { value: 'local' } });
    fireEvent.click(save());
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Preferences need review'));
    await waitFor(() => expect(screen.getByLabelText('Locale')).toHaveValue('fr'));
    expect(screen.getByLabelText('Printables username')).toHaveValue('local');
    expect(screen.getByRole('radio', { name: 'Expert' })).toBeChecked();
    expect(save()).toBeDisabled();
    expect(mockPut).toHaveBeenCalledTimes(1);
    mockGet.mockRejectedValueOnce(new Error('Offline'));
    fireEvent.click(screen.getByRole('button', { name: /Reload latest preferences/ }));
    await screen.findByText(/Could not reload preferences/);
    expect(screen.getByLabelText('Printables username')).toHaveValue('local');
    fireEvent.click(screen.getByRole('button', { name: /Reload latest preferences/ }));
    await waitFor(() => expect(save()).toBeEnabled());
    expect(screen.getByLabelText('Printables username')).toHaveValue('updated');
    expect(screen.getByRole('radio', { name: 'Guided' })).toBeChecked();
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    fireEvent.click(save());
    await waitFor(() => expect(mockPut).toHaveBeenCalledTimes(2));
    expect(mockPut).toHaveBeenLastCalledWith('/settings/user', expect.objectContaining({
      printerControlMode: 'Expert', locale: 'fr', printablesUsername: 'updated', rowVersion: 'remote-v2',
    }));
  });

  it('reports initial load failure and recovers without inventing a revision', async () => {
    mockGet.mockRejectedValueOnce(new Error('Offline'));
    mount();
    await screen.findByText('Unable to load user preferences');
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(mockPut).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByRole('radio', { name: 'Guided' });
  });
});
