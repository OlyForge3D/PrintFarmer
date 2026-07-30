import {
  TaskType,
  UserTask,
  UserTaskAnchorKind,
  UserTaskSourceKind,
  isKnownTaskType,
} from '@/services/tasksApi';

/**
 * Metadata derived from a single {@link UserTask} that the widget needs in
 * order to render the row and act on it. Keeping this concentrated in one
 * pure module means the widget stays presentation-focused and each mapping
 * gets a small, deterministic unit test.
 */
export interface ShiftTaskDetails {
  /** Deep-link target for the task's "primary action" (row click). */
  href: string | null;
  /** Short one-word category label used as a chip on the row. */
  categoryLabel: string;
  /**
   * True when the taskType is not recognized by this client. Callers should
   * render a safe generic row (no crash, no navigation) and log/telemetry.
   */
  isUnknownKind: boolean;
}

const LOCATION_PRINTERS_PAGE = '/printers';
const LOCATION_MAINTENANCE_PAGE = '/maintenance';
const LOCATION_SPOOLS_PAGE = '/spools';
const LOCATION_CATALOG_PAGE = '/catalog';
const LOCATION_PROFILE_IMPORT_PAGE = '/profiles/import';

/**
 * Determine a safe deep-link target and label for a shift-plan or legacy task.
 *
 * The mapping only uses existing routes (see `App.tsx`) — we do not introduce
 * a new Tasks page. When a task's kind is unrecognized we return
 * `isUnknownKind: true` so the widget can render a safe generic row instead
 * of failing or navigating to a broken URL.
 */
export function getShiftTaskDetails(task: UserTask): ShiftTaskDetails {
  const taskType = task.taskType;

  if (!isKnownTaskType(taskType)) {
    return { href: null, categoryLabel: 'Task', isUnknownKind: true };
  }

  switch (taskType) {
    case TaskType.ProfileImport: {
      // Preserve the legacy profile-import wizard deep-link. The task's
      // metadataJson may carry a `printerModelId` that scopes the wizard.
      let modelId = task.entityId;
      if (task.metadataJson) {
        try {
          const metadata = JSON.parse(task.metadataJson) as { printerModelId?: string };
          if (metadata.printerModelId) {
            modelId = metadata.printerModelId;
          }
        } catch {
          // Fall through to entityId when metadata isn't valid JSON.
        }
      }
      const params = new URLSearchParams({ modelId, taskId: task.id });
      return {
        href: `${LOCATION_PROFILE_IMPORT_PAGE}?${params.toString()}`,
        categoryLabel: 'Profiles',
        isUnknownKind: false,
      };
    }

    case TaskType.MaintenanceDue:
    case TaskType.MaintenanceInIdleWindow:
    case TaskType.CalibrationNeeded: {
      // Prefer the printer-scoped maintenance page when we have a printer id;
      // fall back to the fleet-wide maintenance dashboard otherwise.
      if (task.entityId && task.entityType === 'Printer') {
        return {
          href: `${LOCATION_PRINTERS_PAGE}/${task.entityId}/maintenance`,
          categoryLabel: taskType === TaskType.CalibrationNeeded ? 'Calibration' : 'Maintenance',
          isUnknownKind: false,
        };
      }
      return {
        href: LOCATION_MAINTENANCE_PAGE,
        categoryLabel: taskType === TaskType.CalibrationNeeded ? 'Calibration' : 'Maintenance',
        isUnknownKind: false,
      };
    }

    case TaskType.FailureClear:
    case TaskType.HarvestReady:
    case TaskType.FilamentRunout:
    case TaskType.FirmwareUpdate: {
      // These are printer-scoped operational tasks — deep-link into the
      // existing printer detail page so the operator can act on the exact
      // machine that triggered the task.
      const label =
        taskType === TaskType.HarvestReady
          ? 'Harvest'
          : taskType === TaskType.FilamentRunout
            ? 'Filament'
            : taskType === TaskType.FirmwareUpdate
              ? 'Firmware'
              : 'Failure';
      if (task.entityId) {
        return {
          href: `${LOCATION_PRINTERS_PAGE}/${task.entityId}`,
          categoryLabel: label,
          isUnknownKind: false,
        };
      }
      return { href: LOCATION_PRINTERS_PAGE, categoryLabel: label, isUnknownKind: false };
    }

    case TaskType.SpoolRestock:
      return { href: LOCATION_SPOOLS_PAGE, categoryLabel: 'Restock', isUnknownKind: false };

    case TaskType.PrintedPartRestock:
      return { href: LOCATION_CATALOG_PAGE, categoryLabel: 'Restock', isUnknownKind: false };

    case TaskType.Custom:
      // Manual tasks have no canonical destination — the widget keeps them
      // in place and shows the description on hover.
      return { href: null, categoryLabel: 'Task', isUnknownKind: false };
  }
}

/**
 * Display label for a shift-plan group header. The server groups timeline
 * tasks (At + Window) under a single {@link UserTaskAnchorKind.Timeline}
 * heading; the tasks inside retain their individual anchor kind.
 */
export function getAnchorGroupLabel(anchorKind: UserTaskAnchorKind): string {
  switch (anchorKind) {
    case UserTaskAnchorKind.Now:
      return 'Now';
    case UserTaskAnchorKind.Timeline:
      return 'On the timeline';
    case UserTaskAnchorKind.At:
      return 'Scheduled';
    case UserTaskAnchorKind.Window:
      return 'Window';
    case UserTaskAnchorKind.AnytimeToday:
      return 'Anytime today';
    case UserTaskAnchorKind.Unspecified:
    default:
      return 'Other';
  }
}

/**
 * Format the anchor time hint (e.g. "9:15 AM" or "9:00–10:30 AM") displayed
 * inline on a task row. Returns `null` for anchor kinds without a boundary.
 */
export function formatTaskAnchorHint(task: UserTask, now: Date = new Date()): string | null {
  const anchor = task.anchorKind ?? UserTaskAnchorKind.Unspecified;
  const formatTime = (iso: string) => {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return null;
    return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  };

  if (anchor === UserTaskAnchorKind.At && task.anchorAtUtc) {
    const t = formatTime(task.anchorAtUtc);
    return t ? `by ${t}` : null;
  }
  if (anchor === UserTaskAnchorKind.Window && task.windowStartUtc && task.windowEndUtc) {
    const start = formatTime(task.windowStartUtc);
    const end = formatTime(task.windowEndUtc);
    if (start && end) return `${start} – ${end}`;
    return null;
  }
  // Reserved for future "overdue by X" hints; currently returns null.
  void now;
  return null;
}

/**
 * Short accessible description of the task's source; used for aria-label on
 * the row so screen-reader users hear provenance without a visual chip.
 */
export function describeTaskSource(sourceKind: UserTaskSourceKind | undefined): string {
  switch (sourceKind) {
    case UserTaskSourceKind.Attention:
      return 'Printer attention';
    case UserTaskSourceKind.FailureIncident:
      return 'Failure incident';
    case UserTaskSourceKind.Harvest:
      return 'Harvest';
    case UserTaskSourceKind.FilamentCoverage:
      return 'Filament coverage';
    case UserTaskSourceKind.Maintenance:
      return 'Maintenance';
    case UserTaskSourceKind.SpoolReorder:
      return 'Spool reorder';
    case UserTaskSourceKind.PrintedPartStock:
      return 'Printed-part stock';
    case UserTaskSourceKind.Unspecified:
    case undefined:
    default:
      return 'Manual task';
  }
}
