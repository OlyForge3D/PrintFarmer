import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { TasksWidget } from '../components/TasksWidget';
import {
  ShiftPlanResult,
  TaskPriority,
  TaskStatus,
  TaskType,
  UserTask,
  UserTaskAnchorKind,
  UserTaskSourceKind,
  tasksApi,
} from '@/services/tasksApi';

vi.mock('@/services/tasksApi', async () => {
  const actual = await vi.importActual<typeof import('@/services/tasksApi')>('@/services/tasksApi');
  return {
    ...actual,
    tasksApi: {
      getPendingTasks: vi.fn(),
      getShiftPlan: vi.fn(),
      getPendingCount: vi.fn(),
      completeTask: vi.fn(),
      skipTask: vi.fn(),
      dismissTask: vi.fn(),
      createTask: vi.fn(),
      getTask: vi.fn(),
    },
  };
});

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
  },
}));

const mockNavigate = vi.fn();
vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

function baseTask(overrides: Partial<UserTask> = {}): UserTask {
  return {
    id: `task-${Math.random().toString(36).slice(2, 9)}`,
    taskType: TaskType.FailureClear,
    entityType: 'Printer',
    entityId: 'printer-1',
    title: 'A task',
    status: TaskStatus.Pending,
    priority: TaskPriority.Normal,
    createdAt: '2026-07-13T09:00:00Z',
    relatedEntityCount: 0,
    anchorKind: UserTaskAnchorKind.Now,
    sourceKind: UserTaskSourceKind.FailureIncident,
    ...overrides,
  };
}

function shiftPlanResult(groups: { anchorKind: UserTaskAnchorKind; tasks: UserTask[] }[]): ShiftPlanResult {
  return {
    mode: 'shift',
    plan: {
      generatedAt: '2026-07-13T12:00:00Z',
      groups,
    },
  };
}

function renderWidget() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <TasksWidget />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

describe('TasksWidget', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders empty state when the shift plan has no tasks', async () => {
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(shiftPlanResult([]));
    renderWidget();
    await waitFor(() => {
      expect(screen.getByTestId('tasks-widget-empty')).toBeInTheDocument();
    });
  });

  it('renders shift-plan groups Now → Timeline → AnytimeToday preserving server order', async () => {
    const nowTask = baseTask({
      id: 'now-1',
      taskType: TaskType.FailureClear,
      title: 'Clear paused print',
      anchorKind: UserTaskAnchorKind.Now,
    });
    const timelineTask = baseTask({
      id: 'timeline-1',
      taskType: TaskType.FilamentRunout,
      title: 'Filament runout soon',
      priority: TaskPriority.High,
      anchorKind: UserTaskAnchorKind.At,
      anchorAtUtc: '2026-07-13T13:30:00Z',
      sourceKind: UserTaskSourceKind.FilamentCoverage,
    });
    const anytimeTask = baseTask({
      id: 'anytime-1',
      taskType: TaskType.SpoolRestock,
      title: 'Restock PLA black',
      anchorKind: UserTaskAnchorKind.AnytimeToday,
      sourceKind: UserTaskSourceKind.SpoolReorder,
    });

    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        { anchorKind: UserTaskAnchorKind.Now, tasks: [nowTask] },
        { anchorKind: UserTaskAnchorKind.Timeline, tasks: [timelineTask] },
        { anchorKind: UserTaskAnchorKind.AnytimeToday, tasks: [anytimeTask] },
      ]),
    );

    renderWidget();

    await waitFor(() => {
      expect(screen.getByText('Clear paused print')).toBeInTheDocument();
    });

    const groups = screen.getAllByTestId('tasks-widget-group');
    expect(groups.map((g) => g.getAttribute('data-anchor-kind'))).toEqual([
      UserTaskAnchorKind.Now,
      UserTaskAnchorKind.Timeline,
      UserTaskAnchorKind.AnytimeToday,
    ]);
    expect(within(groups[0]).getByText('Now')).toBeInTheDocument();
    expect(within(groups[1]).getByText('On the timeline')).toBeInTheDocument();
    expect(within(groups[2]).getByText('Anytime today')).toBeInTheDocument();

    // Badge total (3)
    expect(screen.getByTestId('tasks-widget-badge')).toHaveTextContent('3');
  });

  it('deep-links each source kind to the correct existing route', async () => {
    const user = userEvent.setup();
    const harvest = baseTask({
      id: 'h',
      title: 'Harvest ready',
      taskType: TaskType.HarvestReady,
      entityId: 'p-h',
      sourceKind: UserTaskSourceKind.Harvest,
    });
    const filament = baseTask({
      id: 'f',
      title: 'Filament runout',
      taskType: TaskType.FilamentRunout,
      entityId: 'p-f',
      sourceKind: UserTaskSourceKind.FilamentCoverage,
    });
    const maintenance = baseTask({
      id: 'm',
      title: 'Maintenance in idle window',
      taskType: TaskType.MaintenanceInIdleWindow,
      entityId: 'p-m',
      sourceKind: UserTaskSourceKind.Maintenance,
    });
    const failure = baseTask({
      id: 'fx',
      title: 'Clear failure',
      taskType: TaskType.FailureClear,
      entityId: 'p-fx',
      sourceKind: UserTaskSourceKind.FailureIncident,
    });
    const spool = baseTask({
      id: 's',
      title: 'Restock PLA',
      taskType: TaskType.SpoolRestock,
      sourceKind: UserTaskSourceKind.SpoolReorder,
    });

    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        {
          anchorKind: UserTaskAnchorKind.Now,
          tasks: [harvest, filament, maintenance, failure, spool],
        },
      ]),
    );

    renderWidget();
    await waitFor(() => expect(screen.getByText('Harvest ready')).toBeInTheDocument());

    await user.click(screen.getByText('Harvest ready').closest('[data-testid="tasks-widget-row"]')!);
    expect(mockNavigate).toHaveBeenLastCalledWith('/printers/p-h');

    await user.click(screen.getByText('Filament runout').closest('[data-testid="tasks-widget-row"]')!);
    expect(mockNavigate).toHaveBeenLastCalledWith('/printers/p-f');

    await user.click(screen.getByText('Maintenance in idle window').closest('[data-testid="tasks-widget-row"]')!);
    expect(mockNavigate).toHaveBeenLastCalledWith('/printers/p-m/maintenance');

    await user.click(screen.getByText('Clear failure').closest('[data-testid="tasks-widget-row"]')!);
    expect(mockNavigate).toHaveBeenLastCalledWith('/printers/p-fx');

    await user.click(screen.getByText('Restock PLA').closest('[data-testid="tasks-widget-row"]')!);
    expect(mockNavigate).toHaveBeenLastCalledWith('/spools');
  });

  it('activates the deep-link via keyboard (Enter)', async () => {
    const user = userEvent.setup();
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        {
          anchorKind: UserTaskAnchorKind.Now,
          tasks: [
            baseTask({
              id: 'kbd',
              title: 'Keyboard row',
              taskType: TaskType.HarvestReady,
              entityId: 'kbd-p',
            }),
          ],
        },
      ]),
    );

    renderWidget();
    const row = await screen.findByRole('button', { name: /^Keyboard row — / });
    row.focus();
    await user.keyboard('{Enter}');
    expect(mockNavigate).toHaveBeenLastCalledWith('/printers/kbd-p');
  });

  it('renders a safe generic row and does not navigate for unknown task kinds', async () => {
    const user = userEvent.setup();
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        {
          anchorKind: UserTaskAnchorKind.Now,
          tasks: [
            baseTask({
              id: 'future',
              taskType: 'FromTheFuture' as TaskType,
              title: 'Unknown-kind row',
            }),
          ],
        },
      ]),
    );

    renderWidget();
    await waitFor(() => expect(screen.getByText('Unknown-kind row')).toBeInTheDocument());
    const row = screen.getByText('Unknown-kind row').closest('[data-testid="tasks-widget-row"]')!;
    expect(row).toHaveAttribute('data-unknown-kind', 'true');
    expect(screen.getByTestId('tasks-widget-unknown-badge')).toBeInTheDocument();
    // Row is not a button (not actionable)
    expect(row.getAttribute('role')).toBe('group');
    await user.click(row);
    expect(mockNavigate).not.toHaveBeenCalled();
  });

  it('preserves the profile-import wizard deep-link with metadata printerModelId', async () => {
    const user = userEvent.setup();
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        {
          anchorKind: UserTaskAnchorKind.AnytimeToday,
          tasks: [
            baseTask({
              id: 'pi',
              title: 'Import profiles',
              taskType: TaskType.ProfileImport,
              entityType: 'PrinterModel',
              entityId: 'legacy-model',
              anchorKind: UserTaskAnchorKind.AnytimeToday,
              sourceKind: UserTaskSourceKind.Unspecified,
              metadataJson: JSON.stringify({ printerModelId: 'metadata-model' }),
            }),
          ],
        },
      ]),
    );
    renderWidget();
    await waitFor(() => expect(screen.getByText('Import profiles')).toBeInTheDocument());
    await user.click(screen.getByText('Import profiles').closest('[data-testid="tasks-widget-row"]')!);
    expect(mockNavigate).toHaveBeenLastCalledWith('/profiles/import?modelId=metadata-model&taskId=pi');
  });

  it('falls back to the flat list when the shift-plan feature is disabled (mode=flat)', async () => {
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue({
      mode: 'flat',
      tasks: [
        baseTask({
          id: 'legacy',
          title: 'Legacy profile task',
          taskType: TaskType.ProfileImport,
          anchorKind: UserTaskAnchorKind.Unspecified,
          sourceKind: UserTaskSourceKind.Unspecified,
        }),
      ],
    });
    renderWidget();
    await waitFor(() => expect(screen.getByText('Legacy profile task')).toBeInTheDocument());
    // Only a single ungrouped section, no group header
    const list = screen.getByTestId('tasks-widget-list');
    expect(list.getAttribute('data-mode')).toBe('flat');
    expect(within(list).getAllByTestId('tasks-widget-group')).toHaveLength(1);
    expect(within(list).queryByText('Now')).not.toBeInTheDocument();
    expect(within(list).queryByText('Anytime today')).not.toBeInTheDocument();
  });

  it('runs complete / skip / dismiss lifecycle actions and refreshes the query cache', async () => {
    const user = userEvent.setup();
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        {
          anchorKind: UserTaskAnchorKind.Now,
          tasks: [baseTask({ id: 'lifecycle', title: 'Lifecycle target' })],
        },
      ]),
    );
    vi.mocked(tasksApi.completeTask).mockResolvedValue(undefined);
    vi.mocked(tasksApi.skipTask).mockResolvedValue(undefined);
    vi.mocked(tasksApi.dismissTask).mockResolvedValue(undefined);

    renderWidget();
    await waitFor(() => expect(screen.getByText('Lifecycle target')).toBeInTheDocument());

    const row = screen.getByText('Lifecycle target').closest('[data-testid="tasks-widget-row"]')!;
    const complete = within(row as HTMLElement).getByTestId('tasks-widget-complete');
    const skip = within(row as HTMLElement).getByTestId('tasks-widget-skip');
    const dismiss = within(row as HTMLElement).getByTestId('tasks-widget-dismiss');

    await user.click(complete);
    await user.click(skip);
    await user.click(dismiss);

    await waitFor(() => {
      expect(tasksApi.completeTask).toHaveBeenCalledWith('lifecycle');
      expect(tasksApi.skipTask).toHaveBeenCalledWith('lifecycle');
      expect(tasksApi.dismissTask).toHaveBeenCalledWith('lifecycle');
    });
    // Lifecycle buttons do not navigate.
    expect(mockNavigate).not.toHaveBeenCalled();
  });

  it('renders a loading state before data arrives', async () => {
    let resolve: (value: ShiftPlanResult) => void = () => {};
    vi.mocked(tasksApi.getShiftPlan).mockReturnValue(
      new Promise<ShiftPlanResult>((r) => {
        resolve = r;
      }),
    );
    renderWidget();
    // Loading state comes from DashboardWidget — heading remains but there is no list yet.
    expect(screen.getByRole('heading', { name: /Pending Tasks/i })).toBeInTheDocument();
    expect(screen.queryByTestId('tasks-widget-list')).not.toBeInTheDocument();
    resolve(shiftPlanResult([]));
    await waitFor(() => expect(screen.getByTestId('tasks-widget-empty')).toBeInTheDocument());
  });

  it('renders an error state when the query fails', async () => {
    vi.mocked(tasksApi.getShiftPlan).mockRejectedValue({ statusCode: 500, message: 'boom' });
    renderWidget();
    await waitFor(() => expect(screen.getByText(/Failed to load tasks/i)).toBeInTheDocument());
  });

  it('gives each row an accessible label combining title, source, and time', async () => {
    vi.mocked(tasksApi.getShiftPlan).mockResolvedValue(
      shiftPlanResult([
        {
          anchorKind: UserTaskAnchorKind.Timeline,
          tasks: [
            baseTask({
              id: 'aria',
              title: 'Runout at 1:30',
              taskType: TaskType.FilamentRunout,
              entityId: 'p-aria',
              anchorKind: UserTaskAnchorKind.At,
              anchorAtUtc: '2026-07-13T13:30:00Z',
              sourceKind: UserTaskSourceKind.FilamentCoverage,
            }),
          ],
        },
      ]),
    );
    renderWidget();
    const row = await screen.findByRole('button', { name: /Runout at 1:30 — Filament coverage/ });
    expect(row).toBeInTheDocument();
  });
});
