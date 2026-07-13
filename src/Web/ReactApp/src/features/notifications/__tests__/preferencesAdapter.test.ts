import { describe, expect, it } from 'vitest';
import {
  NotificationFrequency,
  NotificationPreferenceEventType,
  type NotificationEventChannelPreferenceDto,
  type NotificationPreferencesDto,
  type UpdateNotificationPreferencesRequest,
} from '@/types/api';
import {
  buildSavePayload,
  defaultEventChannelPreferences,
  hydratePreferences,
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
      NotificationPreferenceEventType.RunoutRisk,
      NotificationPreferenceEventType.HarvestReady,
      NotificationPreferenceEventType.MaintenanceDue,
      NotificationPreferenceEventType.PrinterOffline,
    ]);
  });

  it('marks a legacy server (job-only matrix) as not supporting operator categories', () => {
    const { serverSupportsOperatorCategories, form } = hydratePreferences(baseLegacyServerResponse());

    expect(serverSupportsOperatorCategories).toBe(false);
    const runout = form.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.RunoutRisk,
    );
    expect(runout).toEqual({
      eventType: NotificationPreferenceEventType.RunoutRisk,
      inApp: false,
      email: false,
      push: false,
      telegram: false,
    });
  });

  it('marks a capable server (any operator row present) as supporting operator categories', () => {
    const capable = baseLegacyServerResponse();
    capable.eventChannelPreferences.push(
      row(NotificationPreferenceEventType.HarvestReady, { inApp: true, push: true }),
    );

    const { serverSupportsOperatorCategories, form } = hydratePreferences(capable);

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

    const { form } = hydratePreferences(withUnknown);

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

    const { form } = hydratePreferences(tampered);
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

  it('leaves operator rows in the matrix (adapter — not this function — strips them)', () => {
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

  it('strips operator rows on legacy servers so the request cannot 400', () => {
    const payload = buildSavePayload(formWithOperatorChanges(), false);
    const tokens = payload.eventChannelPreferences?.map(r => r.eventType) ?? [];

    expect(tokens).toContain(NotificationPreferenceEventType.JobStarted);
    expect(tokens).toContain(NotificationPreferenceEventType.JobCompleted);
    expect(tokens).toContain(NotificationPreferenceEventType.JobFailed);
    expect(tokens).toContain(NotificationPreferenceEventType.JobPaused);
    expect(tokens).not.toContain(NotificationPreferenceEventType.RunoutRisk);
    expect(tokens).not.toContain(NotificationPreferenceEventType.HarvestReady);
    expect(tokens).not.toContain(NotificationPreferenceEventType.MaintenanceDue);
    expect(tokens).not.toContain(NotificationPreferenceEventType.PrinterOffline);
  });

  it('keeps operator rows when the server has demonstrated support', () => {
    const payload = buildSavePayload(formWithOperatorChanges(), true);
    const harvest = payload.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.HarvestReady,
    );
    expect(harvest?.push).toBe(true);
    expect(harvest?.inApp).toBe(true);
  });

  it('preserves job-row values, always-on JobFailed in-app, and derived master flags', () => {
    const form = formWithOperatorChanges();
    const started = form.eventChannelPreferences!.find(
      r => r.eventType === NotificationPreferenceEventType.JobStarted,
    )!;
    started.email = true;

    const payload = buildSavePayload(form, false);
    const failed = payload.eventChannelPreferences?.find(
      r => r.eventType === NotificationPreferenceEventType.JobFailed,
    );
    expect(failed?.inApp).toBe(true);
    expect(payload.enableEmailNotifications).toBe(true);
    expect(payload.notifyOnStart).toBe(true);
    expect(payload.notifyOnFailure).toBe(true);
  });
});
