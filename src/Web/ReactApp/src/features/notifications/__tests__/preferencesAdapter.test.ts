import { describe, expect, it } from 'vitest';
import {
  NotificationFrequency,
  NotificationPreferenceEventType,
  type NotificationCapabilitiesResponse,
  type NotificationEventChannelPreferenceDto,
  type NotificationPreferencesDto,
  type UpdateNotificationPreferencesRequest,
} from '@/types/api';
import {
  buildSavePayload,
  defaultEventChannelPreferences,
  hydratePreferences,
  resolveSupportedEventTypes,
  serverAdvertisesOperatorCategories,
  withDerivedLegacyFlags,
} from '../preferencesAdapter';

function row(
  eventType: NotificationPreferenceEventType,
  overrides: Partial<Omit<NotificationEventChannelPreferenceDto, 'eventType'>> = {},
): NotificationEventChannelPreferenceDto {
  return {
    eventType,
    inApp: false,
    email: false,
    push: false,
    telegram: false,
    ...overrides,
  };
}

function baseLegacyServerResponse(): NotificationPreferencesDto {
  return {
    userId: 'u1',
    enableEmailNotifications: true,
    enablePushNotifications: true,
    enableInAppNotifications: true,
    enableTelegramNotifications: false,
    notifyOnCompletion: true,
    notifyOnFailure: true,
    notifyOnStart: false,
    notifyOnPause: true,
    eventChannelPreferences: [
      row(NotificationPreferenceEventType.JobStarted),
      row(NotificationPreferenceEventType.JobCompleted, { inApp: true, email: true, push: true }),
      row(NotificationPreferenceEventType.JobFailed, { inApp: true, email: true, push: true }),
      row(NotificationPreferenceEventType.JobPaused, { inApp: true, email: true, push: true }),
    ],
    frequency: NotificationFrequency.RealTime,
    retentionDays: 30,
  };
}

const CAPABLE_CAPABILITIES: NotificationCapabilitiesResponse = {
  // Enum-declaration order per #708 backend contract.
  supportedEventTypes: [
    NotificationPreferenceEventType.JobStarted,
    NotificationPreferenceEventType.JobCompleted,
    NotificationPreferenceEventType.JobFailed,
    NotificationPreferenceEventType.JobPaused,
    NotificationPreferenceEventType.PrinterFailure,
    NotificationPreferenceEventType.FilamentRunout,
    NotificationPreferenceEventType.HarvestReady,
    NotificationPreferenceEventType.MaintenanceDue,
    NotificationPreferenceEventType.PrinterOffline,
  ],
};

describe('preferencesAdapter.resolveSupportedEventTypes', () => {
  it('falls back to the classic four job tokens on 404 (null capabilities)', () => {
    const set = resolveSupportedEventTypes(null);
    expect([...set].sort()).toEqual(
      [
        NotificationPreferenceEventType.JobCompleted,
        NotificationPreferenceEventType.JobFailed,
        NotificationPreferenceEventType.JobPaused,
        NotificationPreferenceEventType.JobStarted,
      ].sort(),
    );
  });

  it('honours the exact list the server advertised, including unknown tokens', () => {
    const set = resolveSupportedEventTypes({
      supportedEventTypes: [
        NotificationPreferenceEventType.JobStarted,
        NotificationPreferenceEventType.FilamentRunout,
        'SomeFutureToken',
      ],
    });
    expect(set.has(NotificationPreferenceEventType.JobStarted)).toBe(true);
    expect(set.has(NotificationPreferenceEventType.FilamentRunout)).toBe(true);
    expect(set.has('SomeFutureToken')).toBe(true);
    expect(set.has(NotificationPreferenceEventType.HarvestReady)).toBe(false);
  });
});

describe('preferencesAdapter.serverAdvertisesOperatorCategories', () => {
  it('is false on legacy (null capabilities)', () => {
    expect(serverAdvertisesOperatorCategories(null)).toBe(false);
  });

  it('is false when capabilities only advertise job tokens', () => {
    expect(
      serverAdvertisesOperatorCategories({
        supportedEventTypes: [
          NotificationPreferenceEventType.JobStarted,
          NotificationPreferenceEventType.JobCompleted,
          NotificationPreferenceEventType.JobFailed,
          NotificationPreferenceEventType.JobPaused,
        ],
      }),
    ).toBe(false);
  });

  it('is true when at least one operator token is advertised', () => {
    expect(
      serverAdvertisesOperatorCategories({
        supportedEventTypes: [
          NotificationPreferenceEventType.JobStarted,
          NotificationPreferenceEventType.HarvestReady,
        ],
      }),
    ).toBe(true);
  });
});

describe('preferencesAdapter.hydratePreferences', () => {
  it('returns a full default matrix when the server has no preferences yet', () => {
    const { form, serverSupportsOperatorCategories } = hydratePreferences(null);

    expect(serverSupportsOperatorCategories).toBe(false);
    expect(form.frequency).toBe(NotificationFrequency.RealTime);
    expect(form.eventChannelPreferences?.map(r => r.eventType)).toEqual([
      NotificationPreferenceEventType.JobStarted,
      NotificationPreferenceEventType.JobCompleted,
      NotificationPreferenceEventType.JobFailed,
      NotificationPreferenceEventType.JobPaused,
      NotificationPreferenceEventType.PrinterFailure,
      NotificationPreferenceEventType.FilamentRunout,
      NotificationPreferenceEventType.HarvestReady,
      NotificationPreferenceEventType.MaintenanceDue,
      NotificationPreferenceEventType.PrinterOffline,
    ]);
    // New backend persistence defaults for attention rows: inApp=true,
    // push=true, email=false, telegram=false.
    const harvest = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.HarvestReady,
    );
    expect(harvest).toEqual({
      eventType: NotificationPreferenceEventType.HarvestReady,
      inApp: true,
      email: false,
      push: true,
      telegram: false,
    });
  });

  it('marks a legacy server (no capabilities probe) as not supporting operator categories', () => {
    const { serverSupportsOperatorCategories, form } = hydratePreferences(baseLegacyServerResponse(), null);

    expect(serverSupportsOperatorCategories).toBe(false);
    // Missing operator rows use the new backend defaults so the UI shows the
    // same starting state the server would persist for a new user.
    const runout = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.FilamentRunout,
    );
    expect(runout).toEqual({
      eventType: NotificationPreferenceEventType.FilamentRunout,
      inApp: true,
      email: false,
      push: true,
      telegram: false,
    });
  });

  it('marks a capable server (probe advertises operator tokens) as supporting operator categories', () => {
    const capable = baseLegacyServerResponse();
    capable.eventChannelPreferences.push(
      row(NotificationPreferenceEventType.HarvestReady, { inApp: true, push: true }),
    );

    const { serverSupportsOperatorCategories, form } = hydratePreferences(capable, CAPABLE_CAPABILITIES);

    expect(serverSupportsOperatorCategories).toBe(true);
    const harvest = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.HarvestReady,
    );
    expect(harvest?.inApp).toBe(true);
    expect(harvest?.push).toBe(true);
  });

  it('preserves unknown server-returned tokens verbatim (forward compatibility)', () => {
    const withUnknown = baseLegacyServerResponse();
    const unknownRow = row('SomeFuturePrefToken' as NotificationPreferenceEventType, { email: true });
    withUnknown.eventChannelPreferences.push(unknownRow);

    const { form } = hydratePreferences(withUnknown, CAPABLE_CAPABILITIES);

    const echoed = form.eventChannelPreferences?.find(r => r.eventType === unknownRow.eventType);
    expect(echoed).toEqual(unknownRow);
  });

  it('hydrates rich defaults when non-null preferences carry an empty matrix (Bishop L3 defence)', () => {
    // Degenerate response: server returned a full preferences DTO but with
    // an empty eventChannelPreferences array. Compliant #708 backends never
    // do this, but a partially-broken deployment should not silently
    // downgrade the user to an all-off matrix that diverges from the
    // null-preferences experience.
    const dto: NotificationPreferencesDto = {
      ...baseLegacyServerResponse(),
      eventChannelPreferences: [],
    };
    const { form } = hydratePreferences(dto, CAPABLE_CAPABILITIES);
    const completed = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.JobCompleted,
    );
    // Rich defaults, not DEFAULT_MATRIX_ROW (which would be all-false).
    // email=false matches the #708 canonical backend seed
    // (NotificationPreferencesDefaults.Apply): no surprise first-visit email.
    expect(completed).toEqual({
      eventType: NotificationPreferenceEventType.JobCompleted,
      inApp: true,
      email: false,
      push: true,
      telegram: false,
    });
    const harvest = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.HarvestReady,
    );
    expect(harvest).toEqual({
      eventType: NotificationPreferenceEventType.HarvestReady,
      inApp: true,
      email: false,
      push: true,
      telegram: false,
    });
  });

  it('coerces the JobFailed in-app row back on defensively', () => {
    const tampered = baseLegacyServerResponse();
    const failedIndex = tampered.eventChannelPreferences.findIndex(
      r => r.eventType === NotificationPreferenceEventType.JobFailed,
    );
    tampered.eventChannelPreferences[failedIndex] = row(NotificationPreferenceEventType.JobFailed, {
      inApp: false,
      email: false,
    });

    const { form } = hydratePreferences(tampered, null);
    const failed = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.JobFailed,
    );
    expect(failed?.inApp).toBe(true);
  });
});

describe('preferencesAdapter.withDerivedLegacyFlags', () => {
  it('derives channel master flags and per-event legacy flags from the matrix', () => {
    const matrix = defaultEventChannelPreferences();
    const started = matrix.find(r => r.eventType === NotificationPreferenceEventType.JobStarted)!;
    started.push = true;
    // #708 canonical defaults have email=false on every row (see
    // defaultEventChannelPreferences), so exercise the email-derivation
    // path explicitly rather than relying on an incidental default.
    const completed = matrix.find(r => r.eventType === NotificationPreferenceEventType.JobCompleted)!;
    completed.email = true;

    const derived = withDerivedLegacyFlags({
      enableEmailNotifications: false,
      enablePushNotifications: false,
      enableInAppNotifications: false,
      enableTelegramNotifications: false,
      notifyOnCompletion: false,
      notifyOnFailure: false,
      notifyOnStart: false,
      notifyOnPause: false,
      eventChannelPreferences: matrix,
      frequency: NotificationFrequency.RealTime,
      retentionDays: 30,
    });

    expect(derived.enablePushNotifications).toBe(true);
    expect(derived.enableEmailNotifications).toBe(true);
    expect(derived.notifyOnStart).toBe(true);
    expect(derived.notifyOnFailure).toBe(true);
    const failed = derived.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.JobFailed,
    );
    expect(failed?.inApp).toBe(true);
  });

  it('leaves operator rows in the matrix (buildSavePayload — not this function — strips them)', () => {
    const matrix = defaultEventChannelPreferences();
    const harvest = matrix.find(r => r.eventType === NotificationPreferenceEventType.HarvestReady)!;
    harvest.email = true;

    const derived = withDerivedLegacyFlags({
      enableEmailNotifications: false,
      enablePushNotifications: false,
      enableInAppNotifications: false,
      enableTelegramNotifications: false,
      notifyOnCompletion: false,
      notifyOnFailure: false,
      notifyOnStart: false,
      notifyOnPause: false,
      eventChannelPreferences: matrix,
      frequency: NotificationFrequency.RealTime,
      retentionDays: 30,
    });

    expect(derived.eventChannelPreferences?.some(r => r.eventType === NotificationPreferenceEventType.HarvestReady)).toBe(true);
    expect(derived.enableEmailNotifications).toBe(true);
  });
});

describe('preferencesAdapter.buildSavePayload', () => {
  function formWithOperatorChanges(): UpdateNotificationPreferencesRequest {
    const matrix = defaultEventChannelPreferences();
    const harvest = matrix.find(r => r.eventType === NotificationPreferenceEventType.HarvestReady)!;
    harvest.push = true;
    harvest.inApp = true;
    return {
      enableEmailNotifications: false,
      enablePushNotifications: false,
      enableInAppNotifications: false,
      enableTelegramNotifications: false,
      notifyOnCompletion: false,
      notifyOnFailure: false,
      notifyOnStart: false,
      notifyOnPause: false,
      eventChannelPreferences: matrix,
      frequency: NotificationFrequency.RealTime,
      retentionDays: 30,
    };
  }

  it('strips operator rows on legacy servers (capabilities probe 404) so the request cannot 400', () => {
    const payload = buildSavePayload(formWithOperatorChanges(), null);
    const tokens = payload.eventChannelPreferences?.map(r => r.eventType) ?? [];

    expect(tokens).toContain(NotificationPreferenceEventType.JobStarted);
    expect(tokens).toContain(NotificationPreferenceEventType.JobCompleted);
    expect(tokens).toContain(NotificationPreferenceEventType.JobFailed);
    expect(tokens).toContain(NotificationPreferenceEventType.JobPaused);
    expect(tokens).not.toContain(NotificationPreferenceEventType.PrinterFailure);
    expect(tokens).not.toContain(NotificationPreferenceEventType.FilamentRunout);
    expect(tokens).not.toContain(NotificationPreferenceEventType.HarvestReady);
    expect(tokens).not.toContain(NotificationPreferenceEventType.MaintenanceDue);
    expect(tokens).not.toContain(NotificationPreferenceEventType.PrinterOffline);
  });

  it('sends the exact PascalCase wire tokens the #708 contract publishes (visible rows only)', () => {
    const payload = buildSavePayload(formWithOperatorChanges(), CAPABLE_CAPABILITIES);
    const wireTokens = (payload.eventChannelPreferences ?? []).map(r => String(r.eventType));
    // Sanity: enum members carry the exact PascalCase wire string values.
    // Backend uses JsonStringEnumConverter without a naming policy, so
    // camelCase would 400. PrinterFailure is advertised by the server but
    // OMITTED from the outbound payload (see the concurrent-write test
    // above) — backend partial-PUT preserves the persisted value.
    expect(wireTokens).toEqual(
      expect.arrayContaining([
        'JobStarted',
        'JobCompleted',
        'JobFailed',
        'JobPaused',
        'FilamentRunout',
        'HarvestReady',
        'MaintenanceDue',
        'PrinterOffline',
      ]),
    );
    expect(wireTokens).not.toContain('PrinterFailure');
  });

  it('OMITS printerFailure from the outbound payload even when advertised (backend preserves via partial PUT)', () => {
    // Prior behaviour echoed a server-returned printerFailure row back
    // verbatim on save; the reviewer flagged that as concurrent-write
    // clobber. #708 backend guarantees partial-PUT preservation for any
    // attention row absent from the request, so the safer behaviour is to
    // omit hidden rows entirely — a concurrent mobile write to
    // PrinterFailure cannot be silently overwritten.
    const preferences = baseLegacyServerResponse();
    preferences.eventChannelPreferences.push(
      row(NotificationPreferenceEventType.PrinterFailure, { inApp: true, email: true, push: true, telegram: false }),
    );
    const partial: NotificationCapabilitiesResponse = {
      supportedEventTypes: [
        NotificationPreferenceEventType.JobStarted,
        NotificationPreferenceEventType.JobCompleted,
        NotificationPreferenceEventType.JobFailed,
        NotificationPreferenceEventType.JobPaused,
        NotificationPreferenceEventType.PrinterFailure,
      ],
    };
    const { form } = hydratePreferences(preferences, partial);
    // Hydration still keeps the row internally so any future UI surface can
    // read it — only the outbound PUT filter excludes it.
    const printerFailure = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.PrinterFailure,
    );
    expect(printerFailure).toEqual({
      eventType: NotificationPreferenceEventType.PrinterFailure,
      inApp: true,
      email: true,
      push: true,
      telegram: false,
    });

    const payload = buildSavePayload(form, partial);
    const echoed = payload.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.PrinterFailure,
    );
    expect(echoed).toBeUndefined();
  });

  it('OMITS opaque server-returned unknown tokens from the outbound payload (concurrent-write safety)', () => {
    const withUnknown = baseLegacyServerResponse();
    withUnknown.eventChannelPreferences.push(
      row('SomeFuturePrefToken' as NotificationPreferenceEventType, { email: true }),
    );
    const capable: NotificationCapabilitiesResponse = {
      supportedEventTypes: [
        NotificationPreferenceEventType.JobStarted,
        NotificationPreferenceEventType.JobCompleted,
        NotificationPreferenceEventType.JobFailed,
        NotificationPreferenceEventType.JobPaused,
        NotificationPreferenceEventType.FilamentRunout,
        NotificationPreferenceEventType.HarvestReady,
        NotificationPreferenceEventType.MaintenanceDue,
        NotificationPreferenceEventType.PrinterOffline,
        'SomeFuturePrefToken' as NotificationPreferenceEventType,
      ],
    };
    const { form } = hydratePreferences(withUnknown, capable);
    const payload = buildSavePayload(form, capable);
    const tokens = payload.eventChannelPreferences?.map(r => String(r.eventType)) ?? [];
    // Unknown tokens are OMITTED from PUT even when the server advertises
    // them; only visible UI rows are sent so a concurrent writer keeps
    // ownership of tokens the user could not have touched.
    expect(tokens).not.toContain('SomeFuturePrefToken');
    // Visible tokens still ride the PUT.
    expect(tokens).toContain(NotificationPreferenceEventType.HarvestReady);
  });

  it('keeps operator rows when the capabilities probe advertises them', () => {
    const payload = buildSavePayload(formWithOperatorChanges(), CAPABLE_CAPABILITIES);
    const harvest = payload.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.HarvestReady,
    );
    expect(harvest?.push).toBe(true);
    expect(harvest?.inApp).toBe(true);
  });

  it('only sends the exact tokens the server advertised (partial rollout)', () => {
    // Server advertises jobs + HarvestReady, but not the other three operator tokens.
    const partial: NotificationCapabilitiesResponse = {
      supportedEventTypes: [
        NotificationPreferenceEventType.JobStarted,
        NotificationPreferenceEventType.JobCompleted,
        NotificationPreferenceEventType.JobFailed,
        NotificationPreferenceEventType.JobPaused,
        NotificationPreferenceEventType.HarvestReady,
      ],
    };
    const payload = buildSavePayload(formWithOperatorChanges(), partial);
    const tokens = payload.eventChannelPreferences?.map(r => r.eventType) ?? [];
    expect(tokens).toContain(NotificationPreferenceEventType.HarvestReady);
    expect(tokens).not.toContain(NotificationPreferenceEventType.FilamentRunout);
    expect(tokens).not.toContain(NotificationPreferenceEventType.MaintenanceDue);
    expect(tokens).not.toContain(NotificationPreferenceEventType.PrinterOffline);
  });

  it('derives enablePushNotifications from tokens the server accepts — unsupported operator defaults on a legacy server must not leak into the master flag', () => {
    // Legacy server (capabilities=null → supported = job tokens only). User
    // has push OFF on every visible job row, but the hidden operator rows
    // carry `push=true` defaults from DEFAULT_ATTENTION_ROW. Those rows are
    // UNSUPPORTED on a legacy server (it has no column for them at all), so
    // they must not influence the derived master flag — otherwise the save
    // payload would emit `enablePushNotifications: true` even though the
    // user has push off on every row the server actually understands.
    const matrix = defaultEventChannelPreferences().map(r =>
      // Force ALL push flags off (both job and operator defaults).
      r.eventType === NotificationPreferenceEventType.JobCompleted
        ? { ...r, push: false }
        : r.eventType === NotificationPreferenceEventType.JobFailed
          ? { ...r, push: false }
          : r.eventType === NotificationPreferenceEventType.JobPaused
            ? { ...r, push: false }
            : r,
    );
    const form: UpdateNotificationPreferencesRequest = {
      enableEmailNotifications: false,
      enablePushNotifications: true, // stale value from previous state
      enableInAppNotifications: false,
      enableTelegramNotifications: false,
      notifyOnCompletion: false,
      notifyOnFailure: false,
      notifyOnStart: false,
      notifyOnPause: false,
      eventChannelPreferences: matrix,
      frequency: NotificationFrequency.RealTime,
      retentionDays: 30,
    };
    const payload = buildSavePayload(form, null);

    // Every visible job row has push=false → outbound flag must be false.
    expect(payload.enablePushNotifications).toBe(false);
    // Sanity: the operator rows were stripped, so their push=true defaults
    // cannot influence the derived master.
    expect(payload.eventChannelPreferences?.some(
      r => r.eventType === NotificationPreferenceEventType.HarvestReady,
    )).toBe(false);
  });

  it('derives enablePushNotifications from the FULL matrix on a capable server — a hidden but SUPPORTED PrinterFailure row must not be masked by an all-off visible matrix', () => {
    // Regression for the Hicks/Dallas-adjudicated master-flag defect: on a
    // capable server (all 9 tokens advertised, including PrinterFailure),
    // every VISIBLE row has push off, but the hidden PrinterFailure row
    // (never rendered by the UI, always omitted from the outbound rows via
    // partial-PUT preservation) still carries push=true. The backend
    // enforces `enablePushNotifications` as a hard delivery gate, so
    // deriving the flag from the stripped (visible-only) matrix would emit
    // `false` and silently suppress delivery of the preserved PrinterFailure
    // alert even though the row itself survives the PUT untouched.
    const matrix = defaultEventChannelPreferences().map(r =>
      r.eventType === NotificationPreferenceEventType.PrinterFailure ? r : { ...r, push: false },
    );
    const hiddenPrinterFailure = matrix.find(
      r => r.eventType === NotificationPreferenceEventType.PrinterFailure,
    );
    expect(hiddenPrinterFailure?.push).toBe(true); // sanity: hidden row still push=true

    const form: UpdateNotificationPreferencesRequest = {
      enableEmailNotifications: false,
      enablePushNotifications: false,
      enableInAppNotifications: false,
      enableTelegramNotifications: false,
      notifyOnCompletion: false,
      notifyOnFailure: false,
      notifyOnStart: false,
      notifyOnPause: false,
      eventChannelPreferences: matrix,
      frequency: NotificationFrequency.RealTime,
      retentionDays: 30,
    };

    const payload = buildSavePayload(form, CAPABLE_CAPABILITIES);

    // The hidden-but-supported PrinterFailure row's push=true must still be
    // reflected in the master flag...
    expect(payload.enablePushNotifications).toBe(true);
    // ...even though the row itself remains omitted from the outbound rows
    // array (concurrent-write protection is unchanged).
    expect(payload.eventChannelPreferences?.some(
      r => r.eventType === NotificationPreferenceEventType.PrinterFailure,
    )).toBe(false);
  });
});

describe('preferencesAdapter.resolveSupportedEventTypes (degenerate cases)', () => {
  it('treats an empty supportedEventTypes array as legacy (job-only)', () => {
    // No compliant server returns [] (legacy → 404, capable → 9 tokens), but
    // if one ever does, silently stripping the four job rows on save would
    // be surprising. Treat empty as legacy fallback.
    const set = resolveSupportedEventTypes({ supportedEventTypes: [] });
    expect(set.has(NotificationPreferenceEventType.JobStarted)).toBe(true);
    expect(set.has(NotificationPreferenceEventType.JobCompleted)).toBe(true);
    expect(set.has(NotificationPreferenceEventType.JobFailed)).toBe(true);
    expect(set.has(NotificationPreferenceEventType.JobPaused)).toBe(true);
    expect(set.has(NotificationPreferenceEventType.HarvestReady)).toBe(false);
  });
});
