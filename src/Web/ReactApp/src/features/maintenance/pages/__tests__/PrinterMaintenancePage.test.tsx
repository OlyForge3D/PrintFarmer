import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router';
import { PrinterMaintenancePage } from '../PrinterMaintenancePage';
import type { MaintenanceAlert, MaintenanceLog, PrinterMaintenanceScheduleDto } from '@/types/maintenance';
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
    getPrinterStatistics: vi.fn(),
    getPrinterMaintenanceLogs: vi.fn(),
    getPrinterAlerts: vi.fn(),
    createMaintenanceLog: vi.fn(),
    getUpcomingMaintenance: vi.fn(),
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
  cumulativePrintHours: 10,
};
const physicalT1: ToolheadDto = {
  id: 'th-1',
  index: 1,
  name: 'T1',
  isPrimary: false,
  toolheadType: ToolheadType.Physical,
  cumulativePrintHours: 200,
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
  supportsPerToolAttribution: true,
} as PrinterDetails;

const printerDetailsMultiUnattributed: PrinterDetails = {
  id: printerId,
  name: 'Voron 2.4',
  serverUrl: 'http://x',
  toolheads: [physicalT0, physicalT1],
  supportsPerToolAttribution: false,
} as PrinterDetails;

const printerDetailsSingle: PrinterDetails = {
  id: printerId,
  name: 'Voron 2.4',
  serverUrl: 'http://x',
  toolheads: [physicalT0],
  supportsPerToolAttribution: true,
} as PrinterDetails;

const printerDetailsMmuOnly: PrinterDetails = {
  id: printerId,
  name: 'Voron 2.4',
  serverUrl: 'http://x',
  toolheads: [physicalT0, mmuGate],
  supportsPerToolAttribution: true,
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
  detailsError?: Error;
  alerts?: MaintenanceAlert[];
  logs?: MaintenanceLog[];
  deployments?: PrinterMaintenanceScheduleDto[];
  capabilities?: unknown;
  upcoming?: unknown[];
} = {}) {
  (apiClient.getPrinters as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([printer]);
  if (overrides.detailsError) {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(
      overrides.detailsError
    );
  } else {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
      overrides.details === undefined ? printerDetailsMulti : overrides.details
    );
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
  (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(
    overrides.upcoming ?? []
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
    const alertsSection = screen.getByRole('region', { name: /active alerts/i });
    expect(within(alertsSection).queryByText('Alert for T1')).not.toBeInTheDocument();
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
      const alertsSection = screen.getByRole('region', { name: /active alerts/i });
      expect(within(alertsSection).getByText('Alert for T1')).toBeInTheDocument();
    });
    const alertsSection = screen.getByRole('region', { name: /active alerts/i });
    expect(within(alertsSection).queryByText('Legacy alert (printer-wide)')).not.toBeInTheDocument();
    expect(screen.getByText('Deployment for T1')).toBeInTheDocument();
    expect(screen.queryByText('Deployment (printer-wide)')).not.toBeInTheDocument();
    expect(screen.getByText('Log for T1')).toBeInTheDocument();
    expect(screen.queryByText('Legacy log')).not.toBeInTheDocument();
  });

  it('single-toolhead printer does not show the scope picker or odometer row', async () => {
    seedDefaults({ details: printerDetailsSingle });
    renderPage();

    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
    // One eligible toolhead still gets an odometer card, but no picker.
    expect(screen.getByText('Legacy log')).toBeInTheDocument();
    expect(screen.getByText('Log for T1')).toBeInTheDocument();
  });

  it('MMU-only printer excludes gate toolheads from the picker', async () => {
    seedDefaults({ details: printerDetailsMmuOnly });
    renderPage();

    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    // Only one eligible physical toolhead (T0) → picker hidden.
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/AMS 1/)).not.toBeInTheDocument();
  });

  it('hides per-tool UI when printer reports supportsPerToolAttribution=false (#711)', async () => {
    seedDefaults({ details: printerDetailsMultiUnattributed });
    renderPage();

    // Legacy printer-wide alert is still shown regardless of gate.
    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    // Per-tool surface must be hidden because the printer projection says
    // the backend cannot attribute usage to specific toolheads.
    expect(screen.queryByRole('region', { name: /per-toolhead odometers/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
  });

  it('trusts the server-composed supportsPerToolAttribution bool: shows UI when true even if client capabilities cache says otherwise', async () => {
    // #711 stable contract at 0428c66a6: server composes
    // `multiSlotFallbackEnabled AND persisted capability`; when it
    // returns true the client MUST NOT double-gate on its own stale
    // capability flag.
    seedDefaults({
      capabilities: {
        architecture: 'x64',
        slicingEnabled: true,
        modelFilesEnabled: true,
        thumbnailGenerationEnabled: true,
        gcodeUploadEnabled: true,
        operatorFeatures: { multiSlotFallbackEnabled: false },
      },
      // Printer projection still reports the server-composed bool true.
    });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );
    expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument();
  });

  it('collapses per-tool UI when server-composed supportsPerToolAttribution is false even if client cache says enabled', async () => {
    seedDefaults({
      details: printerDetailsMultiUnattributed,
      capabilities: {
        architecture: 'x64',
        slicingEnabled: true,
        modelFilesEnabled: true,
        thumbnailGenerationEnabled: true,
        gcodeUploadEnabled: true,
        operatorFeatures: { multiSlotFallbackEnabled: true },
      },
    });
    renderPage();

    await waitFor(() => expect(screen.getByText('Legacy alert (printer-wide)')).toBeInTheDocument());
    expect(screen.queryByRole('region', { name: /per-toolhead odometers/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('printer-maintenance-scope')).not.toBeInTheDocument();
  });

  it('distinguishes cumulativePrintHours=null (dash) from cumulativePrintHours=0 (renders "0.0 h")', async () => {
    // #711 semantics: null = supported-but-unknown ⇒ dash; 0 = supported
    // with zero accrued hours ⇒ must render as zero, not missing.
    const t0Zero: ToolheadDto = { ...physicalT0, cumulativePrintHours: 0 };
    const t1Null: ToolheadDto = { ...physicalT1, cumulativePrintHours: null };
    seedDefaults({
      details: {
        ...printerDetailsMulti,
        toolheads: [t0Zero, t1Null],
      } as PrinterDetails,
    });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );
    const zeroCard = screen.getByTestId('toolhead-odometer-th-0');
    const nullCard = screen.getByTestId('toolhead-odometer-th-1');
    // Zero-hours toolhead: exact "0.0 h", NOT the dash placeholder.
    expect(zeroCard).toHaveTextContent(/0\.0 h/);
    expect(zeroCard).not.toHaveTextContent(/—/);
    // Null-hours toolhead: dash placeholder, NOT "0.0 h".
    expect(nullCard).toHaveTextContent(/—/);
    expect(nullCard).not.toHaveTextContent(/0\.0 h/);
  });

  // ---------------------------------------------------------------------------
  // Remediation coverage for split-verdict findings (Hicks #3/#4/#7, Vasquez #1)
  // ---------------------------------------------------------------------------

  it('does NOT mark a toolhead odometer as "Overdue" just because it has a high-severity alert (Hicks #3)', async () => {
    // Alert severity == priority, not timing. The odometer must reflect the
    // schedule engine's own overdue/dueToday verdict from the upcoming-
    // maintenance feed, not a heuristic over alert severity.
    const highSeverityAlertOnT0: MaintenanceAlert = {
      ...legacyAlert,
      id: 'a-t0-critical',
      title: 'Critical alert for T0',
      severity: 4, // "Critical" — highest
      toolheadId: 'th-0',
    };
    seedDefaults({
      alerts: [highSeverityAlertOnT0],
      upcoming: [], // No overdue/dueToday tasks from the schedule engine.
    });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );
    const t0Card = screen.getByTestId('toolhead-odometer-th-0');
    expect(t0Card).not.toHaveTextContent(/overdue/i);
    expect(t0Card).not.toHaveTextContent(/due today/i);
  });

  it('marks a toolhead odometer as "Overdue" when the upcoming feed reports an overdue task for that toolhead (Hicks #3)', async () => {
    seedDefaults({
      alerts: [],
      upcoming: [
        {
          id: 'u-1',
          scheduleId: 's-1',
          printerId,
          printerName: 'Voron 2.4',
          toolheadId: 'th-1',
          taskName: 'Nozzle change',
          priority: 1,
          intervalType: 'days',
          intervalValue: 30,
          isOverdue: true,
          isDueToday: false,
          daysUntilDue: -3,
        },
      ],
    });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );
    const t1Card = screen.getByTestId('toolhead-odometer-th-1');
    expect(t1Card).toHaveTextContent(/overdue/i);
    // The unrelated toolhead does not inherit the overdue state.
    const t0Card = screen.getByTestId('toolhead-odometer-th-0');
    expect(t0Card).not.toHaveTextContent(/overdue/i);
  });

  it('surfaces a visible alert when printerDetails fails to load instead of silently hiding per-tool UI (Hicks #7)', async () => {
    seedDefaults({ detailsError: new Error('boom') });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/could not load printer details/i)
    );
  });

  // ---------------------------------------------------------------------------
  // Odometer must never render "OK" while the schedule feed is unavailable
  // (Hicks rejection: loading/errored upcoming feed must NOT imply all-clear).
  // ---------------------------------------------------------------------------

  it('renders every odometer chip as "unknown" (never "OK") while the upcoming-maintenance feed is loading', async () => {
    // The upcoming feed never resolves during the assertion window. All
    // cards MUST render the unknown chip and none may render OK — even the
    // cards whose toolheads have a valid numeric `cumulativePrintHours`
    // count (the pre-fix heuristic would have collapsed those to "OK" on
    // hours alone).
    seedDefaults();
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      () => new Promise(() => { /* never resolves */ })
    );
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );

    const t0Card = screen.getByTestId('toolhead-odometer-th-0');
    const t1Card = screen.getByTestId('toolhead-odometer-th-1');
    expect(t0Card.querySelector('[data-testid="due-state-unknown"]')).not.toBeNull();
    expect(t1Card.querySelector('[data-testid="due-state-unknown"]')).not.toBeNull();
    expect(t0Card.querySelector('[data-testid="due-state-ok"]')).toBeNull();
    expect(t1Card.querySelector('[data-testid="due-state-ok"]')).toBeNull();
  });

  it('renders unknown chips and a role="alert" banner when the upcoming-maintenance feed errors', async () => {
    seedDefaults();
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('feed offline')
    );
    renderPage();

    // The page-level banner distinguishes "printer is fine" from "schedule
    // feed broken" — without it the operator would see only "No data"
    // chips with no explanation.
    await waitFor(() =>
      expect(screen.getByTestId('upcoming-maintenance-error')).toBeInTheDocument()
    );
    expect(screen.getByTestId('upcoming-maintenance-error')).toHaveAttribute('role', 'alert');

    // Cards themselves are stamped unknown, never ok.
    const t0Card = screen.getByTestId('toolhead-odometer-th-0');
    expect(t0Card.querySelector('[data-testid="due-state-unknown"]')).not.toBeNull();
    expect(t0Card.querySelector('[data-testid="due-state-ok"]')).toBeNull();
  });

  it('marks the odometer as "ok" only when the schedule feed loads successfully and reports no due tasks', async () => {
    // Positive control for the two failure-mode tests above: an empty but
    // successful upcoming feed IS the OK case. Without this coverage the
    // unknown-vs-ok distinction could silently regress into
    // "always unknown".
    seedDefaults({ upcoming: [] });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );

    const t0Card = screen.getByTestId('toolhead-odometer-th-0');
    expect(t0Card.querySelector('[data-testid="due-state-ok"]')).not.toBeNull();
    expect(t0Card.querySelector('[data-testid="due-state-unknown"]')).toBeNull();
  });
});
