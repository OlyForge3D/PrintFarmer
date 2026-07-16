import { NotificationPreferenceEventType } from '@/types/api';

/**
 * Operator alert categories from the #708 shared web preference contract.
 *
 * This set is the "known-to-this-client" list. Membership drives:
 *
 * - `hydratePreferences`: unknown tokens go into an opaque pass-through
 *   bucket; known operator tokens get a default row when the server omits
 *   them.
 * - `buildSavePayload`: outbound rows are filtered against the
 *   capabilities probe, so unknown-to-server tokens are stripped on legacy
 *   servers.
 *
 * PrinterFailure is enumerated here (so its server-returned row is preserved
 * verbatim on hydrate and forwarded back on save) but intentionally omitted
 * from OPERATOR_EVENT_ROWS below — issue #716 AC does not include a
 * PrinterFailure toggle in the visible matrix.
 */
export const OPERATOR_EVENT_TYPES: readonly NotificationPreferenceEventType[] = [
  NotificationPreferenceEventType.PrinterFailure,
  NotificationPreferenceEventType.FilamentRunout,
  NotificationPreferenceEventType.HarvestReady,
  NotificationPreferenceEventType.MaintenanceDue,
  NotificationPreferenceEventType.PrinterOffline,
] as const;

const OPERATOR_EVENT_TYPE_SET: ReadonlySet<NotificationPreferenceEventType> = new Set(OPERATOR_EVENT_TYPES);

export function isOperatorEventType(eventType: NotificationPreferenceEventType): boolean {
  return OPERATOR_EVENT_TYPE_SET.has(eventType);
}

/** Job events already understood by every supported backend. */
export const JOB_EVENT_TYPES: readonly NotificationPreferenceEventType[] = [
  NotificationPreferenceEventType.JobStarted,
  NotificationPreferenceEventType.JobCompleted,
  NotificationPreferenceEventType.JobFailed,
  NotificationPreferenceEventType.JobPaused,
] as const;

export interface EventRowMeta {
  eventType: NotificationPreferenceEventType;
  label: string;
  description: string;
  /** `true` when in-app notifications for this row cannot be turned off. */
  alwaysOnInApp?: boolean;
}

export const JOB_EVENT_ROWS: readonly EventRowMeta[] = [
  {
    eventType: NotificationPreferenceEventType.JobStarted,
    label: 'Print Started',
    description: 'When a print job begins printing',
  },
  {
    eventType: NotificationPreferenceEventType.JobCompleted,
    label: 'Print Complete',
    description: 'When a print job finishes successfully',
  },
  {
    eventType: NotificationPreferenceEventType.JobFailed,
    label: 'Print Failed',
    description: 'When a print job fails or encounters errors',
    alwaysOnInApp: true,
  },
  {
    eventType: NotificationPreferenceEventType.JobPaused,
    label: 'Print Paused/Resumed',
    description: 'When a print job is paused or resumed',
  },
];

/**
 * Visible operator alert rows for #716. PrinterFailure is intentionally
 * excluded — see comment on OPERATOR_EVENT_TYPES above.
 */
export const OPERATOR_EVENT_ROWS: readonly EventRowMeta[] = [
  {
    eventType: NotificationPreferenceEventType.FilamentRunout,
    label: 'Filament Runout Risk',
    description: 'When a print is at risk of running out of filament before it finishes',
  },
  {
    eventType: NotificationPreferenceEventType.HarvestReady,
    label: 'Harvest Ready',
    description: 'When a completed print is ready to be removed from the printer',
  },
  {
    eventType: NotificationPreferenceEventType.MaintenanceDue,
    label: 'Maintenance Due',
    description: 'When a printer has scheduled maintenance coming due',
  },
  {
    eventType: NotificationPreferenceEventType.PrinterOffline,
    label: 'Printer Offline',
    description: 'When a printer stops responding or drops its connection',
  },
];
