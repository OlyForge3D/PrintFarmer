import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router';
import { PrinterMaintenancePage } from '../PrinterMaintenancePage';
import type { PrinterToolheadOdometer, MaintenanceAlert, MaintenanceLog, PrinterMaintenanceScheduleDto } from '@/types/maintenance';
import { MaintenanceAlertStatus } from '@/types/maintenance';
import type { PrinterDetails, ToolheadDto } from '@/types/api';
import { ToolheadType } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinters: vi.fn(),
    getPrinterDetails: vi.fn(),
    getSystemCapabilities: vi.fn(),
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
}));

vi.mock('@/services/maintenanceService', () => ({
  maintenanceService: {
    getPrinterToolheadOdometers: vi.fn(),
    getPrinterStatistics: vi.fn(),
    getPrinterMaintenanceLogs: vi.fn(),
    getPrinterAlerts: vi.fn(),
    createMaintenanceLog: vi.fn(),
  },
}));

vi.mock('@/services/maintenancePlanService', () => ({
  maintenancePlanService: {
    getScheduleDeployments: vi.fn(),
    getCatalogTasks: vi.fn().mockResolvedValue([]),
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: { username: 'ripley' } }),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));

import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';
import { maintenancePlanService } from '@/services/maintenancePlanService';

const printerId = 'printer-multi';
const printer = { id: printerId, name: 'Voron 2.4' };

const physicalT0: ToolheadDto = {
  id: 'th-0',
  index: 0,
  name: 'T0',
  isPrimary: true,
  toolheadType: ToolheadType.Physical,
};
const physicalT1: ToolheadDto = {
  id: 'th-1',
  index: 1,
  name: 'T1',
  isPrimary: false,
  toolheadType: ToolheadType.Physical,
};
const mmuGate: ToolheadDto = {
  id: 'th-mmu',
  index: 2,
  name: 'AMS 1',
  isPrimary: false,
  toolheadType: ToolheadType.MmuGate,
};

const printerDetailsMulti: PrinterDetails = {
  id: printerId,
  name: 'Voron 2.4',
  serverUrl: 'http://x',
  toolheads: [physicalT0, physicalT1],
} as PrinterDetails;

const printerDetailsSingle: PrinterDetails = {
  id: printerId,
  name: 'Voron 2.4',
  serverUrl: 'http://x',
  toolheads: [physicalT0],
} as PrinterDetails;

const printerDetailsMmuOnly: PrinterDetails = {
  id: printerId,
  name: 'Voron 2.4',
  serverUrl: 'http://x',
  toolheads: [physicalT0, mmuGate],
} as PrinterDetails;

const legacyAlert: MaintenanceAlert = {
  id: 'a-legacy',
  printerId,
  scheduleId: 's-1',
  planName: 'Legacy plan',
  taskName: 'Legacy task',
  title: 'Legacy alert (printer-wide)',
  message: 'Legacy alert body',
  severity: 2,
  status: MaintenanceAlertStatus.Active,
  createdAt: new Date().toISOString(),
  toolheadId: null,
} as MaintenanceAlert;

const scopedAlert: MaintenanceAlert = {
  ...legacyAlert,
  id: 'a-t1',
  title: 'Alert for T1',
  toolheadId: 'th-1',
} as MaintenanceAlert;

const legacyLog: MaintenanceLog = {
  id: 'l-legacy',
  printerId,
  taskName: 'Legacy log',
  performedAt: new Date().toISOString(),
  performedBy: 'ripley',
  toolheadId: null,
} as MaintenanceLog;

const scopedLog: MaintenanceLog = {
  id: 'l-t1',
  printerId,
  taskName: 'Log for T1',
  performedAt: new Date().toISOString(),
  performedBy: 'ripley',
  toolheadId: 'th-1',
} as MaintenanceLog;

const legacyDeployment: PrinterMaintenanceScheduleDto = {
  id: 'd-legacy',
  printerId,
  maintenancePlanId: 'plan-legacy',
  planName: 'Deployment (printer-wide)',
  deployedAt: new Date().toISOString(),
  isActive: true,
  toolheadId: null,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
};

const scopedDeployment: PrinterMaintenanceScheduleDto = {
  ...legacyDeployment,
  id: 'd-t1',
  maintenancePlanId: 'plan-t1',
  planName: 'Deployment for T1',
  toolheadId: 'th-1',
};

const odometers: PrinterToolheadOdometer[] = [
  { toolheadId: 'th-0', toolheadName: 'T0', nozzleHours: 10, hotendHours: 10, dueState: 'ok' },
  { toolheadId: 'th-1', toolheadName: 'T1', nozzleHours: 200, hotendHours: 200, dueState: 'overdue', nextDueLabel: 'Nozzle' },
];

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 0 } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[`/printers/${printerId}/maintenance`]}>
        <Routes>
          <Route
            path="/printers/:printerId/maintenance"
            element={<PrinterMaintenancePage />}
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

function seedDefaults(overrides: {
  details?: PrinterDetails | null;
  odometersResult?: PrinterToolheadOdometer[] | Error;
  alerts?: MaintenanceAlert[];
  logs?: MaintenanceLog[];
  deployments?: PrinterMaintenanceScheduleDto[];
  capabilities?: unknown;
} = {}) {
  (apiClient.getPrinters as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([printer]);
  (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
    overrides.details === undefined ? printerDetailsMulti : overrides.details
  );

  const odom = overrides.odometersResult ?? odometers;
  if (odom instanceof Error) {
    (maintenanceService.getPrinterToolheadOdometers as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(odom);
  } else {
    (maintenanceService.getPrinterToolheadOdometers as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(odom);
  }

  (maintenanceService.getPrinterStatistics as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
    printerId,
    totalMaintenanceCount: 0,
    totalMaintenanceCost: 0,
    averageMaintenanceHours: 0,
  });
  (maintenanceService.getPrinterMaintenanceLogs as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
    overrides.logs ?? [legacyLog, scopedLog]
  );
  (maintenanceService.getPrinterAlerts as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
    overrides.alerts ?? [legacyAlert, scopedAlert]
  );
  (maintenancePlanService.getScheduleDeployments as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
    overrides.deployments ?? [legacyDeployment, scopedDeployment]
  );

  (apiClient.getSystemCapabilities as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
    overrides.capabilities ?? {
      architecture: 'x64',
      slicingEnabled: true,
      modelFilesEnabled: true,
      thumbnailGenerationEnabled: true,
      gcodeUploadEnabled: true,
      operatorFeatures: { multiSlotFallbackEnabled: true },
    }
  );
}

describe('PrinterMaintenancePage — per-toolhead scope', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders odometer cards for each physical toolhead when data is present', async () => {
    seedDefaults();
    renderPage();

    await waitFor(() => {
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument();
    });
  });

  it('printer-wide scope only shows legacy null-toolhead records for a multi-toolhead printer', async () => {
    seedDefaults();
    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument();
    });
    expect(screen.queryByText('Alert for T1')).not.toBeInTheDocument();
    expect(screen.getByText('Deployment (printer-wide)')).toBeInTheDocument();
    expect(screen.queryByText('Deployment for T1')).not.toBeInTheDocument();
    expect(screen.getByText('Legacy log')).toBeInTheDocument();
    expect(screen.queryByText('Log for T1')).not.toBeInTheDocument();
  });

  it('switching scope to a toolhead filters alerts, deployments and logs', async () => {
    seedDefaults();
    renderPage();

    const user = userEvent.setup();
    await waitFor(() => {
      expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText(/T1 · T1/));

    await waitFor(() => {
      expect(screen.getByText('Alert for T1')).toBeInTheDocument();
    });
    expect(screen.queryByText('Legacy alert (printer-wide)')).not.toBeInTheDocument();
    expect(screen.getByText('Deployment for T1')).toBeInTheDocument();
    expect(screen.queryByText('Deployment (printer-wide)')).not.toBeInTheDocument();
    expect(screen.getByText('Log for T1')).toBeInTheDocument();
    expect(screen.queryByText('Legacy log')).not.toBeInTheDocument();
  });

  it('single-toolhead printer does not show the scope picker or odometer row', async () => {
    seedDefaults({ details: printerDetailsSingle, odometersResult: [] });
    renderPage();

    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: /per-toolhead odometers/i })).not.toBeInTheDocument();
    // Legacy printer with no eligible toolheads shows every record.
    expect(screen.getByText('Legacy log')).toBeInTheDocument();
    expect(screen.getByText('Log for T1')).toBeInTheDocument();
  });

  it('MMU-only printer excludes gate toolheads from the picker', async () => {
    seedDefaults({ details: printerDetailsMmuOnly, odometersResult: [] });
    renderPage();

    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    // Only one eligible physical toolhead (T0) → picker hidden.
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/AMS 1/)).not.toBeInTheDocument();
  });

  it('odometer 404 (empty result) still renders the rest of the page unchanged', async () => {
    seedDefaults({ odometersResult: [] });
    renderPage();

    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    expect(screen.queryByRole('region', { name: /per-toolhead odometers/i })).not.toBeInTheDocument();
    // Picker still renders because there are 2 eligible toolheads.
    expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument();
  });

  it('hides odometer row and scope picker when multiSlotFallbackEnabled is false (#711 H5)', async () => {
    seedDefaults({
      capabilities: {
        architecture: 'x64',
        slicingEnabled: true,
        modelFilesEnabled: true,
        thumbnailGenerationEnabled: true,
        gcodeUploadEnabled: true,
        operatorFeatures: { multiSlotFallbackEnabled: false },
      },
    });
    renderPage();

    // Legacy printer-wide alert is still shown.
    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    // If the backend accidentally sends a scoped record, we still render it
    // (defensive), but the per-toolhead UI (picker + odometer row) must not
    // appear. Per the H5 fix, backend actually strips scoped rows server-side
    // when the flag is off, so this is belt-and-suspenders.
    expect(screen.queryByRole('region', { name: /per-toolhead odometers/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
  });

  it('treats missing operatorFeatures block as enabled (older backend fallback)', async () => {
    seedDefaults({
      capabilities: {
        architecture: 'x64',
        slicingEnabled: true,
        modelFilesEnabled: true,
        thumbnailGenerationEnabled: true,
        gcodeUploadEnabled: true,
        // No operatorFeatures — pre-#711 backend.
      },
    });
    renderPage();

    await waitFor(() => expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument());
    expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument();
  });
});
