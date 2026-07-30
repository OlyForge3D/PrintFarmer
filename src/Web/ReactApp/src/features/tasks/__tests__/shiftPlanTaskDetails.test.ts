import { describe, expect, it } from 'vitest';
import {
  describeTaskSource,
  formatTaskAnchorHint,
  getAnchorGroupLabel,
  getShiftTaskDetails,
} from '../shiftPlanTaskDetails';
import {
  TaskPriority,
  TaskStatus,
  TaskType,
  UserTask,
  UserTaskAnchorKind,
  UserTaskSourceKind,
} from '@/services/tasksApi';

function makeTask(overrides: Partial<UserTask> = {}): UserTask {
  return {
    id: 't1',
    taskType: TaskType.FailureClear,
    entityType: 'Printer',
    entityId: 'printer-1',
    title: 'Cleared',
    status: TaskStatus.Pending,
    priority: TaskPriority.Normal,
    createdAt: '2026-07-13T09:00:00Z',
    relatedEntityCount: 0,
    anchorKind: UserTaskAnchorKind.Now,
    sourceKind: UserTaskSourceKind.FailureIncident,
    ...overrides,
  };
}

describe('getShiftTaskDetails', () => {
  it('maps FailureClear to the printer detail page', () => {
    const details = getShiftTaskDetails(makeTask({ taskType: TaskType.FailureClear, entityId: 'p1' }));
    expect(details.href).toBe('/printers/p1');
    expect(details.categoryLabel).toBe('Failure');
    expect(details.isUnknownKind).toBe(false);
  });

  it('maps HarvestReady to the printer detail page with a Harvest label', () => {
    const details = getShiftTaskDetails(
      makeTask({ taskType: TaskType.HarvestReady, entityId: 'p2' }),
    );
    expect(details.href).toBe('/printers/p2');
    expect(details.categoryLabel).toBe('Harvest');
  });

  it('maps FilamentRunout (filament swap) to the printer detail page', () => {
    const details = getShiftTaskDetails(
      makeTask({ taskType: TaskType.FilamentRunout, entityId: 'p3' }),
    );
    expect(details.href).toBe('/printers/p3');
    expect(details.categoryLabel).toBe('Filament');
  });

  it('maps MaintenanceInIdleWindow to the printer maintenance page', () => {
    const details = getShiftTaskDetails(
      makeTask({ taskType: TaskType.MaintenanceInIdleWindow, entityType: 'Printer', entityId: 'p4' }),
    );
    expect(details.href).toBe('/printers/p4/maintenance');
    expect(details.categoryLabel).toBe('Maintenance');
  });

  it('falls back to the fleet maintenance dashboard when no printer entity id is present', () => {
    const details = getShiftTaskDetails(
      makeTask({
        taskType: TaskType.MaintenanceDue,
        entityType: 'Fleet',
        entityId: '',
      }),
    );
    expect(details.href).toBe('/maintenance');
  });

  it('maps SpoolRestock to the spools page', () => {
    const details = getShiftTaskDetails(makeTask({ taskType: TaskType.SpoolRestock }));
    expect(details.href).toBe('/spools');
    expect(details.categoryLabel).toBe('Restock');
  });

  it('maps PrintedPartRestock to the catalog page', () => {
    const details = getShiftTaskDetails(makeTask({ taskType: TaskType.PrintedPartRestock }));
    expect(details.href).toBe('/catalog');
    expect(details.categoryLabel).toBe('Restock');
  });

  it('preserves the legacy profile-import wizard deep-link with metadata printerModelId', () => {
    const details = getShiftTaskDetails(
      makeTask({
        id: 'task-42',
        taskType: TaskType.ProfileImport,
        entityId: 'legacy-model',
        metadataJson: JSON.stringify({ printerModelId: 'metadata-model' }),
      }),
    );
    expect(details.href).toBe('/profiles/import?modelId=metadata-model&taskId=task-42');
  });

  it('recovers gracefully when metadataJson is malformed', () => {
    const details = getShiftTaskDetails(
      makeTask({
        id: 'task-42',
        taskType: TaskType.ProfileImport,
        entityId: 'fallback-model',
        metadataJson: '{not valid json',
      }),
    );
    expect(details.href).toBe('/profiles/import?modelId=fallback-model&taskId=task-42');
  });

  it('renders a safe generic row for unknown / future task kinds', () => {
    const details = getShiftTaskDetails(
      makeTask({ taskType: 'FutureUnknownKind' as TaskType }),
    );
    expect(details.href).toBeNull();
    expect(details.isUnknownKind).toBe(true);
    expect(details.categoryLabel).toBe('Task');
  });

  it('returns null href for manual (Custom) tasks so the widget only toasts', () => {
    const details = getShiftTaskDetails(makeTask({ taskType: TaskType.Custom }));
    expect(details.href).toBeNull();
    expect(details.isUnknownKind).toBe(false);
  });
});

describe('getAnchorGroupLabel', () => {
  it('labels each canonical anchor group', () => {
    expect(getAnchorGroupLabel(UserTaskAnchorKind.Now)).toBe('Now');
    expect(getAnchorGroupLabel(UserTaskAnchorKind.Timeline)).toBe('On the timeline');
    expect(getAnchorGroupLabel(UserTaskAnchorKind.AnytimeToday)).toBe('Anytime today');
  });

  it('does not crash for Unspecified / unrecognized values', () => {
    expect(getAnchorGroupLabel(UserTaskAnchorKind.Unspecified)).toBe('Other');
    expect(getAnchorGroupLabel('futureAnchor' as UserTaskAnchorKind)).toBe('Other');
  });
});

describe('formatTaskAnchorHint', () => {
  it('renders "by <time>" for an At anchor', () => {
    const hint = formatTaskAnchorHint(
      makeTask({ anchorKind: UserTaskAnchorKind.At, anchorAtUtc: '2026-07-13T13:30:00Z' }),
    );
    expect(hint).not.toBeNull();
    expect(hint!.startsWith('by ')).toBe(true);
  });

  it('renders a start–end range for a Window anchor', () => {
    const hint = formatTaskAnchorHint(
      makeTask({
        anchorKind: UserTaskAnchorKind.Window,
        windowStartUtc: '2026-07-13T13:00:00Z',
        windowEndUtc: '2026-07-13T14:30:00Z',
      }),
    );
    expect(hint).not.toBeNull();
    expect(hint).toContain('–');
  });

  it('returns null for anchors without a time', () => {
    expect(
      formatTaskAnchorHint(makeTask({ anchorKind: UserTaskAnchorKind.Now, anchorAtUtc: undefined })),
    ).toBeNull();
    expect(
      formatTaskAnchorHint(
        makeTask({ anchorKind: UserTaskAnchorKind.AnytimeToday, anchorAtUtc: undefined }),
      ),
    ).toBeNull();
  });

  it('returns null when anchorAtUtc is unparseable', () => {
    expect(
      formatTaskAnchorHint(
        makeTask({ anchorKind: UserTaskAnchorKind.At, anchorAtUtc: 'not-a-date' }),
      ),
    ).toBeNull();
  });
});

describe('describeTaskSource', () => {
  it('describes each canonical source kind', () => {
    expect(describeTaskSource(UserTaskSourceKind.Attention)).toContain('attention');
    expect(describeTaskSource(UserTaskSourceKind.FailureIncident)).toContain('Failure');
    expect(describeTaskSource(UserTaskSourceKind.Harvest)).toBe('Harvest');
    expect(describeTaskSource(UserTaskSourceKind.Maintenance)).toBe('Maintenance');
    expect(describeTaskSource(UserTaskSourceKind.SpoolReorder)).toContain('Spool');
    expect(describeTaskSource(UserTaskSourceKind.PrintedPartStock)).toContain('Printed');
  });

  it('describes Unspecified / undefined as a manual task', () => {
    expect(describeTaskSource(UserTaskSourceKind.Unspecified)).toBe('Manual task');
    expect(describeTaskSource(undefined)).toBe('Manual task');
  });
});
