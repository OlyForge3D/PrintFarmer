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

  it('sends the exact PascalCase wire tokens the #708 contract publishes', () => {
    const payload = buildSavePayload(formWithOperatorChanges(), CAPABLE_CAPABILITIES);
    const wireTokens = (payload.eventChannelPreferences ?? []).map(r => String(r.eventType));
    // Sanity: enum members carry the exact PascalCase wire string values.
    // Backend uses JsonStringEnumConverter without a naming policy, so
    // camelCase would 400.
    expect(wireTokens).toEqual(
      expect.arrayContaining([
        'JobStarted',
        'JobCompleted',
        'JobFailed',
        'JobPaused',
        'PrinterFailure',
        'FilamentRunout',
        'HarvestReady',
        'MaintenanceDue',
        'PrinterOffline',
      ]),
    );
  });

  it('forwards a server-returned printerFailure row verbatim even though the UI does not render it', () => {
    // Legacy-server matrix that already contains a printerFailure row from a
    // partially-upgraded backend. The UI does not render it, but hydrate must
    // preserve it and buildSavePayload must forward it when advertised.
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
    expect(echoed).toEqual({
      eventType: NotificationPreferenceEventType.PrinterFailure,
      inApp: true,
      email: true,
      push: true,
      telegram: false,
    });
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

  it('derives enablePushNotifications from the OUTBOUND payload — legacy strip happens before derive', () => {
    // User has push OFF on every visible job row, but the hidden operator
    // rows carry `push=true` defaults from DEFAULT_ATTENTION_ROW. Before the
    // strip-before-derive fix, the legacy save payload would still emit
    // `enablePushNotifications: true` because master flags were computed on
    // the full matrix. Backend treats that flag as a hard delivery gate, so
    // silently flipping it on every save was a real corruption.
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
