import {
  NotificationFrequency,
  NotificationPreferenceEventType,
  type NotificationCapabilitiesResponse,
  type NotificationEventChannelPreferenceDto,
  type NotificationPreferencesDto,
  type UpdateNotificationPreferencesRequest,
} from '@/types/api';
import {
  JOB_EVENT_TYPES,
  OPERATOR_EVENT_TYPES,
  JOB_EVENT_ROWS,
  OPERATOR_EVENT_ROWS,
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
 * 2. Consume the `GET /notifications/preferences/capabilities` probe (introduced by #708)
 *    to know exactly which enum tokens the server accepts. A `null`
 *    capabilities response (endpoint 404) is treated as "legacy server:
 *    supportedEventTypes = the classic four job tokens only".
 *
 * 3. On save, filter the outbound matrix to only tokens the server is known
 *    to accept. Legacy servers therefore never receive an unknown enum
 *    token and cannot 400 on `JsonStringEnumConverter` deserialization, so
 *    previously saved job preferences are never corrupted by an anticipatory
 *    UI. Unknown tokens the server itself returned are still preserved on the
 *    outbound payload — the server said it accepts them.
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

/**
 * Default channel values for a newly-created operator/attention row. Mirrors
 * the backend persistence defaults published for the #708 shared contract
 * (inApp=true, push=true, email=false, telegram=false) so a null-preferences
 * user on a capable server sees the same starting state the server would
 * persist on their behalf.
 */
const DEFAULT_ATTENTION_ROW = (
  eventType: NotificationPreferenceEventType,
): NotificationEventChannelPreferenceDto => ({
  eventType,
  inApp: true,
  email: false,
  push: true,
  telegram: false,
});

/**
 * Default matrix used when the server returns no preferences at all.
 *
 * Job-row email defaults are `false` across the board to match the #708
 * canonical backend seed (`NotificationPreferencesDefaults.Apply`): a
 * first-visit user never gets surprise-emailed on a completion without an
 * explicit opt-in. InApp/push mirror the pre-#708 completion/failure/pause
 * defaults (start off, others on).
 */
export function defaultEventChannelPreferences(): NotificationEventChannelPreferenceDto[] {
  return [
    { eventType: NotificationPreferenceEventType.JobStarted, inApp: false, email: false, push: false, telegram: false },
    { eventType: NotificationPreferenceEventType.JobCompleted, inApp: true, email: false, push: true, telegram: false },
    { eventType: NotificationPreferenceEventType.JobFailed, inApp: true, email: false, push: true, telegram: false },
    { eventType: NotificationPreferenceEventType.JobPaused, inApp: true, email: false, push: true, telegram: false },
    ...OPERATOR_EVENT_TYPES.map(DEFAULT_ATTENTION_ROW),
  ];
}

/**
 * Resolve the effective set of accepted event tokens from the capabilities
 * probe. `null`/`undefined` means the probe returned 404 (or has not been
 * loaded yet) → treat as legacy job-only. An empty `supportedEventTypes`
 * array is also treated as legacy: no compliant server returns an empty
 * matrix (legacy → 404, capable → 9 tokens), and stripping every row would
 * silently drop even the four job tokens on a degenerate response.
 * Returned as a Set of raw string tokens so we can compare against unknown
 * (future) tokens without widening the client enum.
 */
export function resolveSupportedEventTypes(
  capabilities: NotificationCapabilitiesResponse | null | undefined,
): ReadonlySet<string> {
  if (
    !capabilities ||
    !Array.isArray(capabilities.supportedEventTypes) ||
    capabilities.supportedEventTypes.length === 0
  ) {
    return new Set<string>(JOB_EVENT_TYPES);
  }
  return new Set<string>(capabilities.supportedEventTypes);
}

/**
 * Returns `true` when the matrix has at least one push-enabled row that is
 * both visible in the current UI and supported by the current server.
 */
export function hasAnyEnrolledPush(
  matrix: readonly NotificationEventChannelPreferenceDto[] | null | undefined,
  visibleEventTypes: ReadonlySet<NotificationPreferenceEventType>,
  supportedEventTypes: ReadonlySet<string>,
): boolean {
  return (matrix ?? []).some(
    row => row.push && visibleEventTypes.has(row.eventType) && supportedEventTypes.has(row.eventType as string),
  );
}

/**
 * True when the capabilities probe advertises at least one operator token.
 * Used to drive the legacy-server info banner in the UI.
 */
export function serverAdvertisesOperatorCategories(
  capabilities: NotificationCapabilitiesResponse | null | undefined,
): boolean {
  const supported = resolveSupportedEventTypes(capabilities);
  for (const t of OPERATOR_EVENT_TYPES) {
    if (supported.has(t)) return true;
  }
  return false;
}

export interface HydratedPreferences {
  form: UpdateNotificationPreferencesRequest;
  /**
   * True when the capabilities probe advertised at least one operator token.
   * Legacy servers (probe 404) report false and the operator card renders a
   * banner explaining that selections will activate once the server updates.
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
 *
 * The `capabilities` argument is authoritative for whether the operator card
 * shows the legacy-server banner. It is intentionally decoupled from the raw
 * matrix so an empty-preferences user on a capable server still gets the full
 * UI.
 */
export function hydratePreferences(
  preferences: NotificationPreferencesDto | null | undefined,
  capabilities: NotificationCapabilitiesResponse | null | undefined = null,
): HydratedPreferences {
  const serverSupportsOperatorCategories = serverAdvertisesOperatorCategories(capabilities);

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
      serverSupportsOperatorCategories,
    };
  }

  const serverMatrix = preferences.eventChannelPreferences ?? [];

  // Bishop L3: when a compliant server returns non-null preferences but an
  // empty matrix, hydrate every known row from the rich defaults instead of
  // `DEFAULT_MATRIX_ROW` (all-false). Compliant upgraded backends always
  // materialize all nine rows on GET, so this path is degenerate — but a
  // partially-broken server should not silently downgrade the user to an
  // all-off matrix that diverges from the null-preferences experience.
  const isEmptyMatrix = serverMatrix.length === 0;

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
  if (isEmptyMatrix) {
    for (const defaultRow of defaultEventChannelPreferences()) {
      byType.set(defaultRow.eventType, defaultRow);
    }
  } else {
    for (const eventType of JOB_EVENT_TYPES) {
      if (!byType.has(eventType)) byType.set(eventType, DEFAULT_MATRIX_ROW(eventType));
    }
    for (const eventType of OPERATOR_EVENT_TYPES) {
      if (!byType.has(eventType)) byType.set(eventType, DEFAULT_ATTENTION_ROW(eventType));
    }
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
 * Sends only rows the UI actually renders — the four visible job rows plus
 * the four visible operator rows (from `JOB_EVENT_ROWS`/`OPERATOR_EVENT_ROWS`).
 * Hidden `PrinterFailure` and any opaque server-returned unknown tokens are
 * intentionally OMITTED from the outbound payload. The #708 backend contract
 * guarantees that a partial PUT preserves persisted values for any attention
 * row not present in the request (`NotificationsController.UpdatePreferences`
 * legacy-preservation gate). Sending back rows the user could not have
 * touched would risk clobbering concurrent writes from mobile or a second
 * browser tab, which was the reviewer's HIGH-severity concern.
 *
 * On legacy servers (capabilities probe 404 → `capabilities === null`) the
 * outbound matrix is filtered to only the four job tokens the server
 * accepts. Operator tokens are stripped regardless of whether they map to
 * visible rows, so the request cannot fail `JsonStringEnumConverter`
 * deserialization and previously saved job preferences are never corrupted.
 *
 * Master flag derivation (`enablePushNotifications`, etc.) runs on the
 * matrix filtered down to tokens the SERVER ACCEPTS (`supported`) — not the
 * UI-visible-only matrix, and not the raw unfiltered matrix:
 *
 * - Excluding UNSUPPORTED tokens matters on legacy/partial-rollout servers:
 *   the operator rows carry `push=true`/`inApp=true` defaults from
 *   `DEFAULT_ATTENTION_ROW` that the user never had a chance to see or
 *   toggle, and an unsupported server has no persisted column for them at
 *   all. Deriving master flags from those phantom defaults would force
 *   e.g. `enablePushNotifications=true` even when the user has push off on
 *   every row the server actually understands.
 * - Deliberately KEEPING supported-but-hidden tokens (hidden
 *   `PrinterFailure`, opaque unknown rows the server itself returned) in
 *   the derivation matters on capable servers: the backend enforces each
 *   master flag as a hard delivery gate. If a hidden row the server
 *   persists still has a channel on, the master flag for that channel must
 *   stay on too — otherwise the backend's partial-PUT preservation of the
 *   row's value becomes moot, since the gate would suppress delivery even
 *   though the row itself survived the PUT untouched.
 *
 * The rows actually included in the outbound `eventChannelPreferences`
 * remain restricted to `visibleTokens` (UI-rendered rows only) — that
 * concurrent-write protection is unchanged; only the master-flag derivation
 * source differs from the row-stripping filter.
 *
 * All other state (frequency, retentionDays, digest schedule) is passed
 * through unchanged.
 */
export function buildSavePayload(
  request: UpdateNotificationPreferencesRequest,
  capabilities: NotificationCapabilitiesResponse | null | undefined,
): UpdateNotificationPreferencesRequest {
  const supported = resolveSupportedEventTypes(capabilities);
  // Only the tokens the UI actually renders participate in the outbound
  // payload. This is a tighter filter than "everything the server accepts":
  // it deliberately excludes the hidden `PrinterFailure` row and any opaque
  // unknown rows the server echoed back, so a concurrent write from mobile
  // or another tab is not clobbered. The backend #708 contract preserves
  // any attention row absent from the request.
  const visibleTokens = new Set<string>([
    ...JOB_EVENT_ROWS.map(r => String(r.eventType)),
    ...OPERATOR_EVENT_ROWS.map(r => String(r.eventType)),
  ]);

  // Derive master/legacy flags from every row the SERVER accepts — not just
  // the visible ones — before stripping down to the outbound rows. See the
  // docstring above for why `supported` (not `visibleTokens`, and not the
  // raw unfiltered matrix) is the correct source for this derivation.
  const supportedMatrix = (request.eventChannelPreferences ?? []).filter(row =>
    supported.has(row.eventType as string),
  );
  const derived = withDerivedLegacyFlags({
    ...request,
    eventChannelPreferences: supportedMatrix,
  });

  const strippedMatrix = (derived.eventChannelPreferences ?? []).filter(row =>
    visibleTokens.has(String(row.eventType)),
  );

  return {
    ...derived,
    eventChannelPreferences: strippedMatrix,
  };
}

/** Re-exported for tests and other adapter consumers. */
export { isOperatorEventType };
