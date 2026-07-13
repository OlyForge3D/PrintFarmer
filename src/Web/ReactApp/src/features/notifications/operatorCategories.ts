import { NotificationPreferenceEventType } from '@/types/api';

/**
 * Operator alert categories introduced by F3 (#708).
 *
 * These tokens are anticipatory. Until #708 ships the backend enum + DTO
 * contract, older servers will not accept them in `PUT /notifications/preferences`
 * and will reject the request with 400 during `JsonStringEnumConverter`
 * deserialization. `preferencesAdapter` is responsible for stripping them from
 * outbound payloads when server capability has not been observed.
 */
export const OPERATOR_EVENT_TYPES: readonly NotificationPreferenceEventType[] = [
  NotificationPreferenceEventType.RunoutRisk,
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

export const OPERATOR_EVENT_ROWS: readonly EventRowMeta[] = [
  {
    eventType: NotificationPreferenceEventType.RunoutRisk,
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
