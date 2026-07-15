import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Card, Toggle, Select, Alert, Button, Spinner } from '@/common/components/ui';
import { BellIcon } from '@/common/components/icons/MdiIcons';
import {
  useNotificationCapabilities,
  useNotificationPreferences,
  useUpdateNotificationPreferences,
} from '@/features/notifications/hooks/useNotificationPreferences';
import { usePushSubscription } from '@/features/notifications/hooks/usePushSubscription';
import { NotificationFrequency, NotificationPreferenceEventType } from '@/types/api';
import type { UpdateNotificationPreferencesRequest } from '@/types/api';
import {
  buildSavePayload,
  defaultEventChannelPreferences,
  hasAnyEnrolledPush,
  hydratePreferences,
  resolveSupportedEventTypes,
  withDerivedLegacyFlags,
} from '@/features/notifications/preferencesAdapter';
import {
  JOB_EVENT_ROWS,
  OPERATOR_EVENT_ROWS,
  type EventRowMeta,
} from '@/features/notifications/operatorCategories';
import { toast } from 'sonner';

const DEFAULT_PREFERENCES: UpdateNotificationPreferencesRequest = {
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
};

const FREQUENCY_OPTIONS = [
  { value: NotificationFrequency.RealTime, label: 'Real-time' },
  { value: NotificationFrequency.Hourly, label: 'Hourly digest' },
  { value: NotificationFrequency.Daily, label: 'Daily digest' },
  { value: NotificationFrequency.Weekly, label: 'Weekly digest' },
  { value: NotificationFrequency.Never, label: 'Disabled' },
];

export function NotificationPreferencesPage({ embedded = false }: { embedded?: boolean }) {
  const navigate = useNavigate();
  const { data: preferences, isLoading: isPrefsLoading, error: prefsError } = useNotificationPreferences();
  const {
    data: capabilities,
    isLoading: isCapsLoading,
    error: capsError,
  } = useNotificationCapabilities();
  const updateMutation = useUpdateNotificationPreferences();
  const pushSubscription = usePushSubscription();

  const [formState, setFormState] = useState<UpdateNotificationPreferencesRequest>(DEFAULT_PREFERENCES);
  const [serverSupportsOperatorCategories, setServerSupportsOperatorCategories] = useState(false);
  const [isDirty, setIsDirty] = useState(false);
  const isDirtyRef = useRef(false);

  // The capability probe explicitly resolves to `null` on legacy servers
  // (endpoint 404). Any other unresolved state — still loading, or a
  // non-404 network/server error — must NOT be interpreted as legacy or the
  // save path silently strips operator edits. `capabilitiesResolved` is
  // the single source of truth for "safe to build a save payload".
  const capabilitiesResolved = !isCapsLoading && capsError == null;

  useEffect(() => {
    isDirtyRef.current = isDirty;
  }, [isDirty]);

  useEffect(() => {
    const { form, serverSupportsOperatorCategories: supports } = hydratePreferences(
      preferences ?? null,
      capabilities ?? null,
    );
    // The banner-visibility flag must sync independently of form dirtiness
    // so a background capabilities refetch on a capable server (after
    // preferences hydrated first) always clears the stale legacy banner.
    // eslint-disable-next-line react-hooks/set-state-in-effect -- sync server capability visibility into local UI state
    setServerSupportsOperatorCategories(supports);
    if (isDirtyRef.current) return;

    setFormState(withDerivedLegacyFlags(form));
    setIsDirty(false);
  }, [preferences, capabilities]);

  // Per-row capability gate for the operator block. `serverSupportsOperatorCategories`
  // only tells us the server advertised AT LEAST ONE operator token — during a
  // partial rollout it can advertise some operator tokens but not others, and
  // enabling edits on a row whose token isn't in `supportedEventTypes` would let
  // the user toggle a row that `buildSavePayload` then silently strips on save.
  const supportedEventTypes = useMemo(
    () => resolveSupportedEventTypes(capabilities ?? null),
    [capabilities],
  );
  // Browser-push enrollment should only key off rows that are both visible and
  // supported. Otherwise unsupported visible rows from an anticipatory matrix
  // can keep the prompt stuck on even when every supported push row is off.
  const visibleEventTypes = useMemo(
    () => new Set<NotificationPreferenceEventType>([
      ...JOB_EVENT_ROWS.map(r => r.eventType),
      ...OPERATOR_EVENT_ROWS.map(r => r.eventType),
    ]),
    [],
  );
  const isAnyPushEnabled = useMemo(
    () => hasAnyEnrolledPush(formState.eventChannelPreferences, visibleEventTypes, supportedEventTypes),
    [formState.eventChannelPreferences, visibleEventTypes, supportedEventTypes],
  );

  const updateMatrixField = (
    eventType: NotificationPreferenceEventType,
    key: 'inApp' | 'email' | 'push' | 'telegram',
    value: boolean,
  ) => {
    const current = formState.eventChannelPreferences ?? defaultEventChannelPreferences();
    const nextMatrix = current.map(item => {
      if (item.eventType !== eventType) return item;
      if (eventType === NotificationPreferenceEventType.JobFailed && key === 'inApp') {
        return { ...item, inApp: true };
      }
      return { ...item, [key]: value };
    });

    setFormState(prev => withDerivedLegacyFlags({ ...prev, eventChannelPreferences: nextMatrix }));
    setIsDirty(true);
  };

  const updateField = <K extends keyof UpdateNotificationPreferencesRequest>(
    key: K,
    value: UpdateNotificationPreferencesRequest[K],
  ) => {
    setFormState(prev => ({ ...prev, [key]: value }));
    setIsDirty(true);
  };

  const getEventPreference = (eventType: NotificationPreferenceEventType) =>
    (formState.eventChannelPreferences ?? []).find(item => item.eventType === eventType);

  const handleSave = async () => {
    // Refuse to build a save payload from unresolved capability state.
    // `buildSavePayload(request, undefined)` would treat the server as legacy
    // and permanently strip operator-row edits, reporting success to the
    // user — a silent data-loss bug. Guarded by `capabilitiesResolved`.
    if (!capabilitiesResolved) {
      toast.error('Still checking notification capabilities — please retry in a moment');
      return;
    }
    try {
      await updateMutation.mutateAsync(buildSavePayload(formState, capabilities ?? null));
      setIsDirty(false);
      toast.success('Notification preferences saved');
    } catch {
      toast.error('Failed to save notification preferences');
    }
  };

  const handleEnablePush = async () => {
    await pushSubscription.subscribe();
    if (pushSubscription.error) {
      toast.error(pushSubscription.error);
    } else if (pushSubscription.isSubscribed) {
      toast.success('Browser notifications enabled');
    }
  };

  if (isPrefsLoading || isCapsLoading) {
    const loadingContent = (
      <div className="flex items-center justify-center py-12" role="status" aria-label="Loading preferences">
        <Spinner size="lg" />
      </div>
    );
    if (embedded) return loadingContent;
    return (
      <PageTemplate title="Notification Preferences" icon={BellIcon}>
        {loadingContent}
      </PageTemplate>
    );
  }

  if (prefsError) {
    const errorContent = <Alert type="error">Failed to load notification preferences</Alert>;
    if (embedded) return errorContent;
    return (
      <PageTemplate title="Notification Preferences" icon={BellIcon}>
        {errorContent}
      </PageTemplate>
    );
  }

  const saveButton = (
    <Button
      variant="primary"
      onClick={handleSave}
      disabled={!isDirty || updateMutation.isPending || !capabilitiesResolved}
    >
      {updateMutation.isPending ? 'Saving...' : 'Save Preferences'}
    </Button>
  );

  const renderRow = (row: EventRowMeta, disabledChannels = false) => {
    const pref = getEventPreference(row.eventType);
    const alwaysOnInApp = !!row.alwaysOnInApp;
    return (
      <div
        key={row.eventType}
        className="grid grid-cols-[2fr,1fr,1fr,1fr,1fr] gap-3 items-center py-2 border-b border-pf-border last:border-b-0"
      >
        <div>
          <p className="text-sm font-medium text-pf-text-primary">{row.label}</p>
          <p className="text-xs text-pf-text-secondary">{row.description}</p>
        </div>
        <div>
          <Toggle
            checked={alwaysOnInApp ? true : !!pref?.inApp}
            disabled={alwaysOnInApp || disabledChannels}
            onChange={(e) => updateMatrixField(row.eventType, 'inApp', e.target.checked)}
            aria-label={`${row.label} in-app`}
          />
          {alwaysOnInApp && (
            <p className="text-[11px] text-pf-text-secondary mt-1">Always on</p>
          )}
        </div>
        <div>
          <Toggle
            checked={!!pref?.email}
            disabled={disabledChannels}
            onChange={(e) => updateMatrixField(row.eventType, 'email', e.target.checked)}
            aria-label={`${row.label} email`}
          />
        </div>
        <div>
          <Toggle
            checked={!!pref?.push}
            disabled={disabledChannels}
            onChange={(e) => updateMatrixField(row.eventType, 'push', e.target.checked)}
            aria-label={`${row.label} push`}
          />
        </div>
        <div>
          <Toggle
            checked={!!pref?.telegram}
            disabled={disabledChannels}
            onChange={(e) => updateMatrixField(row.eventType, 'telegram', e.target.checked)}
            aria-label={`${row.label} Telegram`}
          />
        </div>
      </div>
    );
  };

  const matrixHeader = (
    <div className="grid grid-cols-[2fr,1fr,1fr,1fr,1fr] gap-3 items-center text-xs uppercase tracking-wide text-pf-text-secondary mb-2">
      <div>Event</div>
      <div>In-App</div>
      <div>Email</div>
      <div>Browser Push</div>
      <div>Telegram</div>
    </div>
  );

  const content = (
    <div className="space-y-6 max-w-4xl">
      <Card>
        <div className="p-4 border-b border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary">Event × Channel Matrix</h2>
          <p className="text-sm text-pf-text-secondary mt-1">Choose which channels each print event should use</p>
        </div>
        <div className="p-4">
          {matrixHeader}
          <div className="space-y-3">
            {JOB_EVENT_ROWS.map(row => renderRow(row))}
          </div>
        </div>
      </Card>

      <Card>
        <div className="p-4 border-b border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary">Operator Alerts</h2>
          <p className="text-sm text-pf-text-secondary mt-1">
            Farm-wide alerts that need operator attention beyond a single print
          </p>
        </div>
        <div className="p-4">
          {capsError ? (
            <Alert type="warning" className="mb-4">
              Could not verify notification capabilities. Operator alert changes are disabled
              until this server responds.
            </Alert>
          ) : !serverSupportsOperatorCategories ? (
            <Alert type="info" className="mb-4">
              This server does not yet expose operator alert categories. Toggles are disabled
              until the server is updated; existing print event preferences are unaffected.
            </Alert>
          ) : null}
          {matrixHeader}
          <div className="space-y-3">
            {OPERATOR_EVENT_ROWS.map(row =>
              renderRow(row, !supportedEventTypes.has(row.eventType) || !!capsError),
            )}
          </div>
        </div>
      </Card>

      {isAnyPushEnabled && !pushSubscription.isSubscribed && pushSubscription.isSupported && (
        <Card>
          <div className="p-4 flex items-center justify-between gap-3">
            <div>
              <p className="text-sm font-medium text-pf-text-primary">Enable browser push on this device</p>
              <p className="text-xs text-pf-text-secondary">Required before push notifications can be delivered here</p>
            </div>
            <Button
              variant="secondary"
              size="sm"
              onClick={handleEnablePush}
              disabled={pushSubscription.isLoading}
            >
              {pushSubscription.isLoading ? 'Enabling...' : 'Enable in Browser'}
            </Button>
          </div>
        </Card>
      )}

      {pushSubscription.error && (
        <Alert type="warning">{pushSubscription.error}</Alert>
      )}

      <Card>
        <div className="p-4 border-b border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary">Delivery Frequency</h2>
          <p className="text-sm text-pf-text-secondary mt-1">How often to send notification digests</p>
        </div>
        <div className="p-4">
          <Select
            value={formState.frequency}
            onChange={(e) => updateField('frequency', e.target.value as NotificationFrequency)}
            aria-label="Notification frequency"
          >
            {FREQUENCY_OPTIONS.map(opt => (
              <option key={opt.value} value={opt.value}>{opt.label}</option>
            ))}
          </Select>
        </div>
      </Card>
    </div>
  );

  if (embedded) {
    return (
      <div>
        {content}
        <div className="mt-4 flex justify-end">{saveButton}</div>
      </div>
    );
  }

  const headerActions = (
    <div className="flex items-center gap-2">
      <Button variant="ghost" onClick={() => navigate(-1)}>
        Back
      </Button>
      {saveButton}
    </div>
  );

  return (
    <PageTemplate
      title="Notification Preferences"
      icon={BellIcon}
      actions={headerActions}
    >
      {content}
    </PageTemplate>
  );
}
