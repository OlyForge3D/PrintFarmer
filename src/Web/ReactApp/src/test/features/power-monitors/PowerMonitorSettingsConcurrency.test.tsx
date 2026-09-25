import '@testing-library/jest-dom';
import type { ReactNode } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PowerMonitorSettingsPage } from '@/features/power-monitors/components/PowerMonitorSettingsPage';

const { getSettings, saveSettings } = vi.hoisted(() => ({
  getSettings: vi.fn(),
  saveSettings: vi.fn(),
}));
vi.mock('@/services/api', () => ({
  apiClient: { getCostTrackingSettings: getSettings, updateCostTrackingSettings: saveSettings },
}));
vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children }: { children: ReactNode }) => <main>{children}</main>,
}));
vi.mock('@/features/admin/components/AdminNavPinButton', () => ({ AdminNavPinButton: () => null }));
vi.mock('@/common/hooks/useApi', () => ({ usePrintersFast: () => ({ data: [] }) }));
vi.mock('@/features/power-monitors/hooks/usePowerMonitors', () => ({
  usePowerMonitors: () => ({ data: [], isLoading: false, isError: false }),
  useCreatePowerMonitor: () => ({}),
  useUpdatePowerMonitor: () => ({}),
  useDeletePowerMonitor: () => ({}),
  useTestPowerMonitorConnection: () => ({}),
}));

describe('Power monitor fallback settings concurrency', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    getSettings.mockResolvedValue({ electricityRatePerKwh: 0.12, averagePrinterWattage: 200, rowVersion: 'v1' });
  });

  it('sends the draft revision and renews it for the next save', async () => {
    saveSettings.mockResolvedValueOnce({ electricityRatePerKwh: 0.2, averagePrinterWattage: 200, rowVersion: 'v2' });
    render(<PowerMonitorSettingsPage />);
    const input = screen.getByRole('spinbutton');
    await waitFor(() => expect(input).toHaveValue(0.12));
    fireEvent.change(input, { target: { value: '0.2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveSettings).toHaveBeenCalledWith({
      electricityRatePerKwh: 0.2, averagePrinterWattage: 200, rowVersion: 'v1',
    }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    fireEvent.change(input, { target: { value: '0.3' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveSettings).toHaveBeenLastCalledWith({
      electricityRatePerKwh: 0.3, averagePrinterWattage: 200, rowVersion: 'v2',
    }));
  });

  it('retains conflicts and drafts until a successful explicit reload', async () => {
    saveSettings.mockRejectedValue({ statusCode: 409 });
    render(<PowerMonitorSettingsPage />);
    const input = screen.getByRole('spinbutton');
    await waitFor(() => expect(input).toHaveValue(0.12));
    fireEvent.change(input, { target: { value: '0.2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('changed elsewhere');
    expect(input).toHaveValue(0.2);
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    getSettings.mockRejectedValueOnce(new Error('offline'));
    fireEvent.click(screen.getByRole('button', { name: 'Reload farm-wide settings' }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Could not load'));
    expect(input).toHaveValue(0.2);
    getSettings.mockResolvedValueOnce({ electricityRatePerKwh: 0.4, rowVersion: 'v3' });
    fireEvent.click(screen.getByRole('button', { name: 'Reload farm-wide settings' }));
    await waitFor(() => expect(input).toHaveValue(0.4));
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
    expect(saveSettings).toHaveBeenCalledTimes(1);
  });
});
