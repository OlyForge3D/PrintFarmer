import '@testing-library/jest-dom';
import type { ReactNode } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PowerMonitorSettingsPage } from '@/features/power-monitors/components/PowerMonitorSettingsPage';

const { getSettings, saveSettings, hasPermission, monitors } = vi.hoisted(() => ({
  getSettings: vi.fn(),
  saveSettings: vi.fn(),
  hasPermission: vi.fn(),
  monitors: [] as Array<{
    id: string;
    printerId: string;
    printerName: string;
    provider: 'Kasa';
    deviceAddress: string;
    electricityRatePerKwh?: number;
    enabled: boolean;
  }>,
}));

vi.mock('@/services/api', () => ({
  apiClient: { getCostTrackingSettings: getSettings, updateCostTrackingSettings: saveSettings },
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission }),
}));
vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children }: { children: ReactNode }) => <main>{children}</main>,
}));
vi.mock('@/features/admin/components/AdminNavPinButton', () => ({ AdminNavPinButton: () => null }));
vi.mock('@/common/hooks/useApi', () => ({
  usePrintersFast: () => ({ data: [{ id: 'printer-1', name: 'Printer One' }] }),
}));
vi.mock('@/features/power-monitors/hooks/usePowerMonitors', () => ({
  usePowerMonitors: () => ({ data: monitors, isLoading: false, isError: false }),
  useCreatePowerMonitor: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdatePowerMonitor: () => ({ mutate: vi.fn(), isPending: false }),
  useDeletePowerMonitor: () => ({ mutate: vi.fn() }),
  useTestPowerMonitorConnection: () => ({ mutate: vi.fn() }),
}));

function renderPage() {
  return render(
    <MemoryRouter>
      <PowerMonitorSettingsPage />
    </MemoryRouter>,
  );
}

describe('Power monitor settings page', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    monitors.splice(0, monitors.length);
    getSettings.mockResolvedValue({
      electricityRatePerKwh: 0.12,
      averagePrinterWattage: 200,
      rowVersion: 'v1',
    });
    hasPermission.mockReturnValue(true);
  });

  it('shows the farm fallback as read-only and links admins to Costs & Energy', async () => {
    renderPage();

    expect(await screen.findByText('$0.12/kWh')).toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'Manage in Costs & Energy settings' });
    expect(link).toHaveAttribute('href', '/admin/settings?scope=system&tab=costs');
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
    expect(saveSettings).not.toHaveBeenCalled();
  });

  it('hides the Costs & Energy link when the user lacks system settings permission', async () => {
    hasPermission.mockReturnValue(false);
    renderPage();

    expect(await screen.findByText('$0.12/kWh')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Manage in Costs & Energy settings' })).not.toBeInTheDocument();
  });

  it('retains the per-monitor rate override form', async () => {
    monitors.push({
      id: 'monitor-1',
      printerId: 'printer-1',
      printerName: 'Printer One',
      provider: 'Kasa',
      deviceAddress: '192.0.2.10',
      electricityRatePerKwh: 0.2,
      enabled: true,
    });
    renderPage();

    expect(screen.getByText('Printer One').parentElement).toHaveTextContent(/\$\s*0\.2\s*\/kWh/);
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    await waitFor(() => expect(screen.getByRole('spinbutton')).toHaveValue(0.2));
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
  });
});
