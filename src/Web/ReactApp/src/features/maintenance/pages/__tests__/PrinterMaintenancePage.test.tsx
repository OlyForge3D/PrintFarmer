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
  PageTemplate: ({
    children,
    actions,
  }: {
    children: React.ReactNode;
    actions?: React.ReactNode;
  }) => (
    <div>
      {actions ? <div data-testid="page-template-actions">{actions}</div> : null}
      {children}
    </div>
  ),
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
          // Wire-boundary alignment: backend `UpcomingMaintenanceTaskDto`
          // exposes the global maintenance-task catalog id as `taskId`
          // (never `scheduleId`). See `useUpcomingMaintenance.ts` and
          // `src/api/Controllers/Responses/UpcomingMaintenanceTaskDto.cs`.
          taskId: 't-1',
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

  // ---------------------------------------------------------------------------
  // Aggregate due-state live region (Hicks #4, Bishop non-blocking).
  //
  // Per-card `role="status"` inside a native <button> is flattened into the
  // button's accessible name and never fires a live-region announcement.
  // Additionally, N cards × N live regions would announce N times on every
  // feed refresh. The remediation moves the announcement to a single stable
  // aggregate node OUTSIDE every button, and the DueStateChip inside each
  // button is a plain visual element (no live-region role).
  // ---------------------------------------------------------------------------

  it('renders a single aggregate live region OUTSIDE every button and summarises per-toolhead state (Hicks #4)', async () => {
    // Two toolheads, both OK, but with a specific mix in later assertions
    // to prove the aggregate content is meaningful.
    seedDefaults({
      upcoming: [
        {
          id: 'u-1',
          taskId: 't-1',
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

    // Exactly one aggregate live region; not one per card.
    const summaries = screen.getAllByTestId('toolhead-due-state-summary');
    expect(summaries).toHaveLength(1);
    const summary = summaries[0];
    expect(summary).toHaveAttribute('role', 'status');
    // Live region must never be a descendant of any interactive control;
    // that is the primary a11y regression this test locks in.
    expect(summary.closest('button')).toBeNull();
    // Meaningful aggregate content — not "OK" per card, not empty.
    expect(summary).toHaveTextContent(/1 toolhead overdue/i);
    expect(summary).toHaveTextContent(/1 OK/i);

    // Individual chips must NOT carry role="status" any more — nested
    // live regions inside <button> ancestors are flattened. Anchor the
    // regex so it matches ONLY the per-state chips
    // (`due-state-ok|overdue|due-today|unknown`), NOT the aggregate
    // summary node whose testid also contains "due-state-".
    const chips = screen.getAllByTestId(/^due-state-(ok|overdue|due-today|unknown)$/);
    expect(chips.length).toBeGreaterThan(0);
    for (const chip of chips) {
      expect(chip).not.toHaveAttribute('role', 'status');
      // Sanity check: the chip is inside a card button (rendered
      // interactive when `onActivate` is wired by the page).
      expect(chip.closest('button')).not.toBeNull();
    }
  });

  it('announces "unknown state" in the aggregate summary while the upcoming feed is loading, never "OK"', async () => {
    seedDefaults();
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      () => new Promise(() => { /* never resolves */ })
    );
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );

    const summary = screen.getByTestId('toolhead-due-state-summary');
    expect(summary).toHaveTextContent(/unavailable/i);
    expect(summary).not.toHaveTextContent(/all toolheads ok/i);
  });

  // ---------------------------------------------------------------------------
  // Scope reset on route-param change (Bishop non-blocking).
  //
  // The component is kept mounted across `/printers/:id/maintenance` route
  // matches, so a scope like `th-1` selected for printer A would otherwise
  // leak into printer B — silently filtering B's records by a foreign
  // toolhead id and preseeding B's log modal with a toolhead that does
  // not exist on B. The fix resets scope to printer-wide whenever the
  // route-param `printerId` changes.
  // ---------------------------------------------------------------------------

  it('resets scope to printer-wide when the :printerId route-param changes on the same mounted component (Bishop)', async () => {
    // Distinct toolhead sets so a leaked selection would be observable.
    const p1Details = {
      id: 'printer-1',
      name: 'Printer One',
      serverUrl: 'http://p1',
      toolheads: [physicalT0, physicalT1],
      supportsPerToolAttribution: true,
    } as PrinterDetails;
    const p2Details = {
      id: 'printer-2',
      name: 'Printer Two',
      serverUrl: 'http://p2',
      toolheads: [
        { id: 'th-2a', index: 0, name: 'T0-P2', isPrimary: true, toolheadType: ToolheadType.Physical, cumulativePrintHours: 5 } as ToolheadDto,
        { id: 'th-2b', index: 1, name: 'T1-P2', isPrimary: false, toolheadType: ToolheadType.Physical, cumulativePrintHours: 6 } as ToolheadDto,
      ],
      supportsPerToolAttribution: true,
    } as PrinterDetails;

    (apiClient.getPrinters as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: 'printer-1', name: 'Printer One' },
      { id: 'printer-2', name: 'Printer Two' },
    ]);
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      async (id: string) => (id === 'printer-1' ? p1Details : p2Details)
    );
    (maintenanceService.getPrinterStatistics as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      printerId: 'x',
      totalMaintenanceCount: 0,
      totalMaintenanceCost: 0,
      averageMaintenanceHours: 0,
    });
    (maintenanceService.getPrinterMaintenanceLogs as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenanceService.getPrinterAlerts as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenancePlanService.getScheduleDeployments as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);

    // Deliberately use a MemoryRouter with a starting entry and change
    // the URL via history.pushState-equivalent navigation — we want the
    // same component instance to survive the transition. That is the
    // exact production scenario: React Router v7 keeps the element
    // stable across route matches when the element type is identical.
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: 0 } },
    });
    const { MemoryRouter, Routes, Route, useNavigate } = await import('react-router');

    function Harness() {
      const nav = useNavigate();
      return (
        <>
          <button data-testid="go-p2" onClick={() => nav('/printers/printer-2/maintenance')}>
            go P2
          </button>
          <Routes>
            <Route
              path="/printers/:printerId/maintenance"
              element={<PrinterMaintenancePage />}
            />
          </Routes>
        </>
      );
    }

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/printers/printer-1/maintenance']}>
          <Harness />
        </MemoryRouter>
      </QueryClientProvider>
    );

    const user = userEvent.setup();

    // On printer-1, select the T1 scope.
    await waitFor(() =>
      expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument()
    );
    await user.click(screen.getByLabelText(/T1 · T1/));
    // Sanity: the T1 radio should be selected.
    expect(screen.getByLabelText(/T1 · T1/)).toBeChecked();

    // Now navigate to printer-2 without unmounting the outer tree. The
    // fix's useEffect on [printerId] must reset scope back to
    // printer-wide on the next render — otherwise the T1 selection
    // (a printer-1 toolhead id) would leak into the printer-2 view.
    await user.click(screen.getByTestId('go-p2'));

    await waitFor(() =>
      expect(screen.getByLabelText(/T0-P2/)).toBeInTheDocument()
    );
    // Scope is back to Printer-wide, not the stale foreign T1 id. The
    // P2 picker exposes "T0 · T0-P2" and "T1 · T1-P2", both of which
    // contain "T1"; anchoring the assertion on the exact P1 label
    // (`^T1 · T1$`) proves the leaked selection is gone without a
    // false positive from P2's own T1-P2 radio.
    expect(screen.getByLabelText('Printer-wide')).toBeChecked();
    expect(screen.queryByLabelText(/^T1 · T1$/)).not.toBeInTheDocument();
  });

  // ---------------------------------------------------------------------------
  // Hicks #2: route change with a Log Maintenance modal open.
  //
  // The prior fix reset scope on printerId change but LEFT the log
  // modal open with the previous printer's initial toolhead
  // preselected. If the operator navigated to a different printer
  // (e.g. via the notifications tray) while the modal was open, they
  // could submit a maintenance log against P2 with a P1 toolhead id
  // pre-filled in the internal state (the modal owns its own scope
  // state after mount). The fix synchronously closes the modal AND
  // remounts LogMaintenanceModal via `key={printerId}` so its
  // internal state is fully reset.
  // ---------------------------------------------------------------------------

  it('closes any open Log Maintenance modal and remounts it when the :printerId route-param changes, so a P1 toolhead cannot submit for P2 (Hicks #2)', async () => {
    // Two printers with disjoint toolhead sets.
    const p1Details = {
      id: 'printer-1',
      name: 'Printer One',
      serverUrl: 'http://p1',
      toolheads: [physicalT0, physicalT1],
      supportsPerToolAttribution: true,
    } as PrinterDetails;
    const p2Details = {
      id: 'printer-2',
      name: 'Printer Two',
      serverUrl: 'http://p2',
      toolheads: [
        { id: 'th-2a', index: 0, name: 'T0-P2', isPrimary: true, toolheadType: ToolheadType.Physical, cumulativePrintHours: 5 } as ToolheadDto,
        { id: 'th-2b', index: 1, name: 'T1-P2', isPrimary: false, toolheadType: ToolheadType.Physical, cumulativePrintHours: 6 } as ToolheadDto,
      ],
      supportsPerToolAttribution: true,
    } as PrinterDetails;

    (apiClient.getPrinters as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: 'printer-1', name: 'Printer One' },
      { id: 'printer-2', name: 'Printer Two' },
    ]);
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      async (id: string) => (id === 'printer-1' ? p1Details : p2Details)
    );
    (maintenanceService.getPrinterStatistics as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      printerId: 'x',
      totalMaintenanceCount: 0,
      totalMaintenanceCost: 0,
      averageMaintenanceHours: 0,
    });
    (maintenanceService.getPrinterMaintenanceLogs as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenanceService.getPrinterAlerts as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenancePlanService.getScheduleDeployments as unknown as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (maintenanceService.createMaintenanceLog as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({ id: 'log-created' });

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: 0 } },
    });
    const { MemoryRouter, Routes, Route, useNavigate } = await import('react-router');

    function Harness() {
      const nav = useNavigate();
      return (
        <>
          <button data-testid="go-p2" onClick={() => nav('/printers/printer-2/maintenance')}>
            go P2
          </button>
          <Routes>
            <Route
              path="/printers/:printerId/maintenance"
              element={<PrinterMaintenancePage />}
            />
          </Routes>
        </>
      );
    }

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/printers/printer-1/maintenance']}>
          <Harness />
        </MemoryRouter>
      </QueryClientProvider>
    );

    const user = userEvent.setup();

    // Wait for the P1 page to be ready, then pick T1 scope and open
    // the Log Maintenance modal — the picker inside the modal will
    // reflect the passed `initialToolheadId` and remount when the
    // key changes.
    await waitFor(() =>
      expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument()
    );
    await user.click(screen.getByLabelText(/T1 · T1/));

    // The top-level "Log Maintenance" button on the page (not the
    // submit button in the modal, which reads "Log Maintenance" too;
    // we anchor by role and name and pick the ONE that opens the
    // dialog).
    await user.click(screen.getAllByRole('button', { name: /^Log Maintenance$/i })[0]);
    // Modal is open — its heading reads "Log Maintenance" (Modal
    // sets the accessible name via the `title` prop).
    expect(await screen.findByRole('heading', { name: /log maintenance/i })).toBeInTheDocument();

    // Now navigate to P2 while the modal is still visible.
    await user.click(screen.getByTestId('go-p2'));

    // The modal MUST NOT remain visible against a foreign printer.
    // Wait for the P2 body to render, then confirm the modal heading
    // is gone. If the fix regresses (modal stays open with P1's
    // toolhead pre-selected), the heading remains and this fails.
    await waitFor(() =>
      expect(screen.getByLabelText(/T0-P2/)).toBeInTheDocument()
    );
    expect(screen.queryByRole('heading', { name: /log maintenance/i })).not.toBeInTheDocument();

    // Reopen the modal on P2 — the scope picker inside must expose
    // ONLY P2 toolheads. A leaked "T1 · T1" would be a P1 selection.
    await user.click(screen.getAllByRole('button', { name: /^Log Maintenance$/i })[0]);
    await waitFor(() =>
      expect(screen.getByRole('heading', { name: /log maintenance/i })).toBeInTheDocument()
    );
    // Modal-internal scope picker shows P2 toolheads, not P1's.
    // Because the picker also carries "Printer-wide", we scope by
    // the modal's own test id.
    const modalScope = screen.getByTestId('log-maintenance-scope');
    expect(within(modalScope).queryByLabelText(/^T1 · T1$/)).not.toBeInTheDocument();
    expect(within(modalScope).getByLabelText(/T0-P2/)).toBeInTheDocument();
    expect(within(modalScope).getByLabelText(/T1-P2/)).toBeInTheDocument();
    // And Printer-wide is checked (fresh mount default), NOT the
    // leaked P1 T1.
    expect(within(modalScope).getByLabelText('Printer-wide')).toBeChecked();
  });

  // ---------------------------------------------------------------------------
  // Hicks #6: due-state announcement stability.
  //
  // Two properties matter for correct assistive-tech behaviour:
  //   (a) The live region node must be MOUNTED persistently — outside
  //       every conditional branch — so screen readers subscribe to
  //       it once and observe every text change. A live region that
  //       toggles between mounted and unmounted misses announcements.
  //   (b) When the upcoming-maintenance banner is visible with
  //       `role="alert"`, the sibling `role="status"` text must be
  //       empty so the same failure is not announced twice.
  //   (c) An "all OK" state must be reachable (not just the "N OK
  //       cheek-by-jowl with N unknown" mixed summary).
  // ---------------------------------------------------------------------------

  it('mounts the aggregate live region persistently, even when the printer has no eligible toolheads (Hicks #6)', async () => {
    // Single-toolhead printer with per-tool disabled → no odometer
    // grid renders, but the live region must still exist so a
    // future arrival of data (e.g. after a refetch) is announced.
    seedDefaults({ details: printerDetailsMultiUnattributed });
    renderPage();

    // Wait until the page has finished loading its main data.
    await waitFor(() =>
      expect(screen.getByTestId('toolhead-due-state-summary')).toBeInTheDocument()
    );

    // Region is mounted; text may be empty (nothing to summarise
    // yet). The critical property is that the node is IN the DOM.
    const summary = screen.getByTestId('toolhead-due-state-summary');
    expect(summary).toHaveAttribute('role', 'status');
    // Text is either empty (initial) or a valid summary — but the
    // node itself is present.
    expect(summary).toBeInTheDocument();

    // No odometer region — that branch is genuinely hidden.
    expect(
      screen.queryByRole('region', { name: /per-toolhead odometers/i })
    ).not.toBeInTheDocument();
  });

  it('empties the live-region text when the upcoming-maintenance banner is visible, so screen readers do not announce the same failure twice (Hicks #6 no double-announce)', async () => {
    seedDefaults();
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('feed broken')
    );
    renderPage();

    // The upcoming banner is what carries `role="alert"` and reads
    // out the failure — assertively, so screen readers hear it.
    const alert = await screen.findByTestId('upcoming-maintenance-error');
    expect(alert).toHaveAttribute('role', 'alert');

    // The sibling `role="status"` region must be empty text so the
    // polite announcement does not double up on the assertive one.
    const summary = screen.getByTestId('toolhead-due-state-summary');
    expect(summary).toHaveAttribute('role', 'status');
    expect(summary.textContent).toBe('');
  });

  it('announces "All toolheads OK." when every toolhead resolves clear (Hicks #6 all-OK reachable)', async () => {
    // Feed succeeds with an empty task list — every toolhead
    // resolves to `dueState: 'ok'`. The summary must short-circuit
    // to "All toolheads OK." rather than "Maintenance status: N OK."
    // to give an unambiguous all-clear announcement.
    seedDefaults({ upcoming: [] });
    renderPage();

    await waitFor(() =>
      expect(screen.getByRole('region', { name: /per-toolhead odometers/i })).toBeInTheDocument()
    );

    const summary = screen.getByTestId('toolhead-due-state-summary');
    await waitFor(() => {
      expect(summary.textContent).toBe('All toolheads OK.');
    });
    // Sanity: not the mixed-state phrasing.
    expect(summary).not.toHaveTextContent(/maintenance status/i);
  });

  // ---------------------------------------------------------------------------
  // Hicks #3: explicit-null preservation on handleLogMaintenance.
  //
  // The prior fallback used `??`, which coerces both `undefined` AND
  // an explicit `null` into the current page scope. That converts a
  // deliberate "log this printer-wide" request from a caller into a
  // scoped log against the currently viewed toolhead — silently
  // misattributing the record. The fix falls back ONLY on `undefined`.
  //
  // The `undefined` path (top action button) is exercised here; the
  // `null` path is defence-in-depth for callers that pass an explicit
  // printer-wide id (e.g. a future per-tool card whose owner is null,
  // or an alert/deployment "Log" button crossing scope) — those
  // callers are filtered by `scopeMatches` in the current UI, so the
  // observable delta between `??` and `=== undefined ?` is latent
  // today. We keep the stricter check to prevent a silent-misattribution
  // regression the moment a new caller is introduced that does NOT
  // pass through the same scope filter.
  // ---------------------------------------------------------------------------

  it('forwards the current scope as initialToolheadId when Log Maintenance is invoked without an argument (undefined → fallback) (Hicks #3 undefined path)', async () => {
    seedDefaults();
    renderPage();

    await waitFor(() =>
      expect(screen.getByTestId('printer-maintenance-scope')).toBeInTheDocument()
    );

    const user = userEvent.setup();
    // Scope := T1
    await user.click(screen.getByLabelText(/T1 · T1/));

    // Open the log modal via the top action button (undefined arg).
    // The mocked PageTemplate surfaces the actions block so the top
    // "Log Maintenance" button is queryable in the render tree.
    await user.click(screen.getAllByRole('button', { name: /^Log Maintenance$/i })[0]);
    await waitFor(() =>
      expect(screen.getByRole('heading', { name: /log maintenance/i })).toBeInTheDocument()
    );

    // The modal's own scope picker must reflect the T1 fallback.
    // Anchored to the modal by its data-testid so we do not read the
    // page-level scope picker.
    const modalScope = screen.getByTestId('log-maintenance-scope');
    // The T1 radio inside the modal must be selected.
    expect(within(modalScope).getByLabelText(/T1 · T1/)).toBeChecked();
    expect(within(modalScope).getByLabelText('Printer-wide')).not.toBeChecked();
  });

  it('opens the log modal with Printer-wide preselected when clicking the "Log" action on a printer-wide deployment (Hicks #3 explicit-null path stays printer-wide)', async () => {
    // The Deployed Plans "Log" button passes `deployment.toolheadId ?? null`.
    // For a printer-wide deployment that is an explicit `null`. Even
    // when the caller is filtered by `scopeMatches` at T1 today, the
    // baseline printer-wide-scope flow must open the modal with
    // Printer-wide selected — this guards the observable half of the
    // fix (undefined-null semantic distinction) and locks in the
    // trivial-but-critical baseline that a printer-wide "Log" click
    // does not silently promote to a toolhead scope.
    seedDefaults();
    renderPage();

    await waitFor(() =>
      expect(screen.getByText('Deployment (printer-wide)')).toBeInTheDocument()
    );

    const user = userEvent.setup();
    const printerWideRow = screen.getByText('Deployment (printer-wide)').closest('div.p-4');
    expect(printerWideRow).not.toBeNull();
    const logBtn = within(printerWideRow as HTMLElement).getByRole('button', { name: /^Log$/ });
    await user.click(logBtn);

    await waitFor(() =>
      expect(screen.getByRole('heading', { name: /log maintenance/i })).toBeInTheDocument()
    );

    // Modal's internal scope must be Printer-wide (not silently
    // promoted to a toolhead scope by the fallback).
    const modalScope = screen.getByTestId('log-maintenance-scope');
    expect(within(modalScope).getByLabelText('Printer-wide')).toBeChecked();
    // T1 radio is present in the picker but not selected.
    expect(within(modalScope).getByLabelText(/T1 · T1/)).not.toBeChecked();
  });
});
