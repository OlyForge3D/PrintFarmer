import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { LogMaintenanceModal } from '../LogMaintenanceModal';
import type { PrinterMaintenanceScheduleDto } from '@/types/maintenance';
import type { ToolheadDto } from '@/types/api';
import { ToolheadType } from '@/types/api';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: { username: 'ripley', email: 'ripley@example.com' } }),
}));

vi.mock('@/services/maintenancePlanService', () => ({
  maintenancePlanService: {
    getCatalogTasks: vi.fn().mockResolvedValue([]),
  },
}));

const printerId = 'printer-1';
const printerName = 'Bambu X1C';

const physical: ToolheadDto = {
  id: 'th-1',
  index: 0,
  name: 'T0',
  isPrimary: true,
  toolheadType: ToolheadType.Physical,
};

const physical2: ToolheadDto = {
  id: 'th-2',
  index: 1,
  name: 'T1',
  isPrimary: false,
  toolheadType: ToolheadType.Physical,
};

const mmuGate: ToolheadDto = {
  id: 'th-mmu-1',
  index: 2,
  name: 'AMS Slot 1',
  isPrimary: false,
  toolheadType: ToolheadType.MmuGate,
};

function makeDeployment(
  overrides: Partial<PrinterMaintenanceScheduleDto>
): PrinterMaintenanceScheduleDto {
  return {
    id: 'sched-x',
    printerId,
    maintenancePlanId: 'plan-x',
    planName: 'Plan X',
    deployedAt: new Date().toISOString(),
    isActive: true,
    notes: null,
    toolheadId: null,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function renderModal(props: Partial<Parameters<typeof LogMaintenanceModal>[0]> = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const onSubmit = vi.fn().mockResolvedValue(undefined);
  const onClose = vi.fn();

  const utils = render(
    <QueryClientProvider client={queryClient}>
      <LogMaintenanceModal
        isOpen
        printerId={printerId}
        printerName={printerName}
        deployments={[]}
        onSubmit={onSubmit}
        onClose={onClose}
        {...props}
      />
    </QueryClientProvider>
  );

  return { ...utils, onSubmit, onClose };
}

async function fillRequired() {
  const user = userEvent.setup();
  const taskInput = screen.getByPlaceholderText(/nozzle replacement/i);
  await user.clear(taskInput);
  await user.type(taskInput, 'Nozzle clean');
  return user;
}

describe('LogMaintenanceModal — toolhead scope', () => {
  beforeEach(() => vi.clearAllMocks());

  it('does not render the scope picker when there is only one eligible toolhead', () => {
    renderModal({ toolheads: [physical] });

    expect(screen.queryByTestId('log-maintenance-scope')).not.toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: /maintenance scope/i })).not.toBeInTheDocument();
  });

  it('renders the accessible scope picker when there are multiple eligible toolheads', () => {
    renderModal({ toolheads: [physical, physical2] });

    expect(screen.getByTestId('log-maintenance-scope')).toBeInTheDocument();
    const group = screen.getByRole('radiogroup', { name: /maintenance scope/i });
    expect(group).toBeInTheDocument();
    expect(screen.getByLabelText('Printer-wide')).toBeInTheDocument();
    expect(screen.getByLabelText('T0 · T0')).toBeInTheDocument();
    expect(screen.getByLabelText('T1 · T1')).toBeInTheDocument();
  });

  it('excludes MMU/AMS gates from the picker', () => {
    renderModal({ toolheads: [physical, physical2, mmuGate] });

    expect(screen.getByLabelText('T0 · T0')).toBeInTheDocument();
    expect(screen.getByLabelText('T1 · T1')).toBeInTheDocument();
    expect(screen.queryByLabelText(/AMS Slot 1/)).not.toBeInTheDocument();
  });

  it('submits toolheadId=null for the default printer-wide scope', async () => {
    const { onSubmit } = renderModal({ toolheads: [physical, physical2] });
    const user = await fillRequired();

    await user.click(screen.getByRole('button', { name: /^Log Maintenance$/ }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const request = onSubmit.mock.calls[0][0];
    expect(request.toolheadId).toBeNull();
    expect(request.printerId).toBe(printerId);
    expect(request.taskName).toBe('Nozzle clean');
  });

  it('submits the selected toolheadId when a toolhead is picked', async () => {
    const { onSubmit } = renderModal({ toolheads: [physical, physical2] });
    const user = await fillRequired();

    await user.click(screen.getByLabelText('T1 · T1'));
    await user.click(screen.getByRole('button', { name: /^Log Maintenance$/ }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0].toolheadId).toBe('th-2');
  });

  it('preselects the toolhead scope when initialToolheadId is provided', async () => {
    const { onSubmit } = renderModal({
      toolheads: [physical, physical2],
      initialToolheadId: 'th-2',
    });
    const user = await fillRequired();

    const t1Radio = screen.getByLabelText('T1 · T1') as HTMLInputElement;
    expect(t1Radio.checked).toBe(true);

    await user.click(screen.getByRole('button', { name: /^Log Maintenance$/ }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0].toolheadId).toBe('th-2');
  });

  it('prefers a deployment scoped to the selected toolhead', async () => {
    const printerWide = makeDeployment({ id: 'sched-wide', toolheadId: null });
    const scoped = makeDeployment({ id: 'sched-th2', toolheadId: 'th-2', planName: 'Plan T1' });
    const { onSubmit } = renderModal({
      toolheads: [physical, physical2],
      deployments: [printerWide, scoped],
      initialToolheadId: 'th-2',
    });
    const user = await fillRequired();

    await user.click(screen.getByRole('button', { name: /^Log Maintenance$/ }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0].deploymentId).toBe('sched-th2');
  });

  it('does NOT cross-scope-fall-back: deploymentId stays null when no exact-scope deployment exists (Hicks #2)', async () => {
    // Regression guard: previously the modal would fall back to the printer-wide
    // deployment when the operator chose a specific toolhead but no
    // toolhead-scoped deployment existed. That corrupts attribution — the log
    // would be tied to a deployment whose scope disagrees with the log's own
    // `toolheadId`. The new behavior is exact match only.
    const printerWide = makeDeployment({ id: 'sched-wide', toolheadId: null });
    const otherScoped = makeDeployment({ id: 'sched-th1', toolheadId: 'th-1' });
    const { onSubmit } = renderModal({
      toolheads: [physical, physical2],
      deployments: [otherScoped, printerWide],
      initialToolheadId: 'th-2',
    });
    const user = await fillRequired();

    await user.click(screen.getByRole('button', { name: /^Log Maintenance$/ }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0].toolheadId).toBe('th-2');
    // No exact-scope deployment → deploymentId must not point at the
    // printer-wide fallback (may be undefined or null depending on how the
    // modal represents "no deployment"; both are acceptable — the point is
    // it MUST NOT equal 'sched-wide').
    expect(onSubmit.mock.calls[0][0].deploymentId ?? null).toBeNull();
  });
});
