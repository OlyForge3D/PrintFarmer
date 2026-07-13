import {
  NotificationFrequency,
  NotificationPreferenceEventType,
  type NotificationEventChannelPreferenceDto,
  type NotificationPreferencesDto,
  type UpdateNotificationPreferencesRequest,
} from '@/types/api';
import {
  JOB_EVENT_TYPES,
  OPERATOR_EVENT_TYPES,
  isOperatorEventType,
} from './operatorCategories';

/**
 * Isolated adapter between the notification-preferences page and the raw API
 * DTO. Responsibilities:
 *
 * 1. Hydrate the matrix returned by the server with default rows for every
 *    known operator category so the UI can always render the full grid, even
 *    on legacy servers that only know the four job categories.
 *
 * 2. Detect whether the server understands operator categories. The probe is
 *    "the server returned at least one operator category row in the GET
 *    response". This is a lower bound: a capable server that has never had
 *    operator prefs written may still look legacy. That is safe because the
 *    only consequence is that operator toggles are kept as local pending state
 *    until the first authoritative response arrives.
 *
 * 3. On save, strip operator category rows from the outbound payload when the
 *    server has not been observed supporting them. Legacy servers therefore
 *    never receive an unknown enum token and cannot 400 on
 *    JsonStringEnumConverter deserialization, so previously saved job
 *    preferences are never corrupted by an anticipatory UI.
 *
 * The tokens/labels here are gated by #708 — when the backend contract lands
 * on `feature/705-operator-redesign`, the operator token set in
 * `operatorCategories.ts` must be updated to match the shipped enum values.
 */

const DEFAULT_MATRIX_ROW = (
  eventType: NotificationPreferenceEventType,
): NotificationEventChannelPreferenceDto => ({
  eventType,
  inApp: false,
  email: false,
  push: false,
  telegram: false,
});

/** Default matrix used when the server returns no preferences at all. */
export function defaultEventChannelPreferences(): NotificationEventChannelPreferenceDto[] {
  return [
    { eventType: NotificationPreferenceEventType.JobStarted, inApp: false, email: false, push: false, telegram: false },
    { eventType: NotificationPreferenceEventType.JobCompleted, inApp: true, email: true, push: true, telegram: false },
    { eventType: NotificationPreferenceEventType.JobFailed, inApp: true, email: true, push: true, telegram: false },
    { eventType: NotificationPreferenceEventType.JobPaused, inApp: true, email: true, push: true, telegram: false },
    ...OPERATOR_EVENT_TYPES.map(DEFAULT_MATRIX_ROW),
  ];
}

export interface HydratedPreferences {
  form: UpdateNotificationPreferencesRequest;
  /**
   * True when the server has demonstrated it understands operator category
   * tokens in the preferences contract. Legacy servers report false; operator
   * toggles remain local-only until the server upgrades.
   */
  serverSupportsOperatorCategories: boolean;
}

/**
 * Build the initial page state from a preferences response (or `null` for
 * "no preferences saved yet"). Guarantees:
 *
 * - Every known job token has a row.
 * - Every known operator token has a row (defaulted to off when absent).
 * - Any unknown token the server returned is preserved verbatim so a
 *   forward-compatible client cannot silently drop server-side data.
 * - The `JobFailed` in-app toggle is coerced on to preserve the existing
 *   always-on invariant.
 */
export function hydratePreferences(
  preferences: NotificationPreferencesDto | null | undefined,
): HydratedPreferences {
  if (!preferences) {
    return {
      form: {
        enableEmailNotifications: true,
        enablePushNotifications: true,
        enableInAppNotifications: true,
        enableTelegramNotifications: false,
        notifyOnCompletion: true,
        notifyOnFailure: true,
        notifyOnStart: false,
        notifyOnPause: true,
        eventChannelPreferences: defaultEventChannelPreferences(),
        frequency: NotificationFrequency.RealTime,
        retentionDays: 30,
      },
      serverSupportsOperatorCategories: false,
    };
  }

  const serverMatrix = preferences.eventChannelPreferences ?? [];
  const serverSupportsOperatorCategories = serverMatrix.some(row =>
    isOperatorEventType(row.eventType),
  );

  const known = new Set<NotificationPreferenceEventType>([
    ...JOB_EVENT_TYPES,
    ...OPERATOR_EVENT_TYPES,
  ]);

  // Start from what the server told us (preserving any unknown tokens), then
  // ensure every known token has a row.
  const byType = new Map<NotificationPreferenceEventType, NotificationEventChannelPreferenceDto>();
  const unknownRows: NotificationEventChannelPreferenceDto[] = [];
  for (const row of serverMatrix) {
    if (known.has(row.eventType)) {
      byType.set(row.eventType, row);
    } else {
      unknownRows.push(row);
    }
  }
  for (const eventType of JOB_EVENT_TYPES) {
    if (!byType.has(eventType)) byType.set(eventType, DEFAULT_MATRIX_ROW(eventType));
  }
  for (const eventType of OPERATOR_EVENT_TYPES) {
    if (!byType.has(eventType)) byType.set(eventType, DEFAULT_MATRIX_ROW(eventType));
  }

  // Enforce JobFailed in-app always-on invariant defensively.
  const failed = byType.get(NotificationPreferenceEventType.JobFailed);
  if (failed) byType.set(NotificationPreferenceEventType.JobFailed, { ...failed, inApp: true });

  const eventChannelPreferences: NotificationEventChannelPreferenceDto[] = [
    ...JOB_EVENT_TYPES.map(t => byType.get(t)!),
    ...OPERATOR_EVENT_TYPES.map(t => byType.get(t)!),
    ...unknownRows,
  ];

  return {
    form: {
      enableEmailNotifications: preferences.enableEmailNotifications,
      enablePushNotifications: preferences.enablePushNotifications,
      enableInAppNotifications: preferences.enableInAppNotifications,
      enableTelegramNotifications: preferences.enableTelegramNotifications,
      notifyOnCompletion: preferences.notifyOnCompletion,
      notifyOnFailure: preferences.notifyOnFailure,
      notifyOnStart: preferences.notifyOnStart,
      notifyOnPause: preferences.notifyOnPause,
      eventChannelPreferences,
      frequency: preferences.frequency,
      retentionDays: preferences.retentionDays,
    },
    serverSupportsOperatorCategories,
  };
}

/**
 * Derive the legacy per-event flags (`notifyOnStart`, etc.) and the
 * per-channel master switches from the current matrix. Preserves the
 * pre-existing behaviour of `withDerivedLegacyFlags` in the page:
 *
 * - master channel flag = OR across every row/channel
 * - `notifyOnStart|Completion|Pause` = any channel on for that row
 * - `notifyOnFailure` stays hard-coded true (JobFailed in-app is always on)
 * - JobFailed in-app is coerced back on
 *
 * All matrix rows are kept (including operator rows and unknown tokens);
 * `buildSavePayload` is the only place that strips rows for legacy servers.
 */
export function withDerivedLegacyFlags(
  request: UpdateNotificationPreferencesRequest,
): UpdateNotificationPreferencesRequest {
  const matrix = request.eventChannelPreferences ?? defaultEventChannelPreferences();
  const byEvent = (eventType: NotificationPreferenceEventType) => matrix.find(x => x.eventType === eventType);
  const started = byEvent(NotificationPreferenceEventType.JobStarted);
  const completed = byEvent(NotificationPreferenceEventType.JobCompleted);
  const paused = byEvent(NotificationPreferenceEventType.JobPaused);

  const rowActive = (row: NotificationEventChannelPreferenceDto | undefined) =>
    !!row && (row.inApp || row.email || row.push || row.telegram);

  return {
    ...request,
    enableEmailNotifications: matrix.some(x => x.email),
    enablePushNotifications: matrix.some(x => x.push),
    enableInAppNotifications: matrix.some(x => x.inApp),
    enableTelegramNotifications: matrix.some(x => x.telegram),
    notifyOnStart: rowActive(started),
    notifyOnCompletion: rowActive(completed),
    notifyOnFailure: true,
    notifyOnPause: rowActive(paused),
    eventChannelPreferences: matrix.map(item =>
      item.eventType === NotificationPreferenceEventType.JobFailed
        ? { ...item, inApp: true }
        : item,
    ),
  };
}

/**
 * Prepare the payload sent to `PUT /notifications/preferences`.
 *
 * When the server does not yet understand operator categories, operator rows
 * are stripped from the outbound matrix so the request cannot fail
 * deserialization. All other state (job rows, unknown server-provided rows,
 * legacy flags, frequency, retentionDays) is passed through unchanged.
 */
export function buildSavePayload(
  request: UpdateNotificationPreferencesRequest,
  serverSupportsOperatorCategories: boolean,
): UpdateNotificationPreferencesRequest {
  const derived = withDerivedLegacyFlags(request);
  if (serverSupportsOperatorCategories) {
    return derived;
  }
  const matrix = derived.eventChannelPreferences ?? [];
  return {
    ...derived,
    eventChannelPreferences: matrix.filter(row => !isOperatorEventType(row.eventType)),
  };
}
