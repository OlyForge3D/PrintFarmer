import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Card, Toggle, Select, Alert, Button, Spinner } from '@/common/components/ui';
import { BellIcon } from '@/common/components/icons/MdiIcons';
import { useNotificationPreferences, useUpdateNotificationPreferences } from '@/features/notifications/hooks/useNotificationPreferences';
import { usePushSubscription } from '@/features/notifications/hooks/usePushSubscription';
import {
  useAttentionCategories,
  useAttentionPushPreferences,
  useUpdateAttentionPushPreferences,
} from '@/features/notifications/hooks/useAttentionPushPreferences';
import {
  buildAttentionPushSavePayload,
  buildCategoryUiMeta,
  hydrateAttentionPushPreferences,
} from '@/features/notifications/attentionPushCategories';
import { NotificationFrequency, NotificationPreferenceEventType } from '@/types/api';
import type {
  AttentionPushPreferencesDto,
  NotificationEventChannelPreferenceDto,
  UpdateNotificationPreferencesRequest,
} from '@/types/api';
import { toast } from 'sonner';

const DEFAULT_EVENT_CHANNEL_PREFERENCES: NotificationEventChannelPreferenceDto[] = [
  { eventType: NotificationPreferenceEventType.JobStarted, inApp: false, email: false, push: false, telegram: false },
  { eventType: NotificationPreferenceEventType.JobCompleted, inApp: true, email: true, push: true, telegram: false },
  { eventType: NotificationPreferenceEventType.JobFailed, inApp: true, email: true, push: true, telegram: false },
  { eventType: NotificationPreferenceEventType.JobPaused, inApp: true, email: true, push: true, telegram: false },
];

const DEFAULT_PREFERENCES: UpdateNotificationPreferencesRequest = {
  enableEmailNotifications: true,
  enablePushNotifications: true,
  enableInAppNotifications: true,
  enableTelegramNotifications: false,
  notifyOnCompletion: true,
  notifyOnFailure: true,
  notifyOnStart: false,
  notifyOnPause: true,
  eventChannelPreferences: DEFAULT_EVENT_CHANNEL_PREFERENCES,
  frequency: NotificationFrequency.RealTime,
  retentionDays: 30,
};

interface EventRow {
  eventType: NotificationPreferenceEventType;
  label: string;
  description: string;
}

const EVENT_ROWS: EventRow[] = [
  { eventType: NotificationPreferenceEventType.JobStarted, label: 'Print Started', description: 'When a print job begins printing' },
  { eventType: NotificationPreferenceEventType.JobCompleted, label: 'Print Complete', description: 'When a print job finishes successfully' },
  { eventType: NotificationPreferenceEventType.JobFailed, label: 'Print Failed', description: 'When a print job fails or encounters errors' },
  { eventType: NotificationPreferenceEventType.JobPaused, label: 'Print Paused/Resumed', description: 'When a print job is paused or resumed' },
];

const FREQUENCY_OPTIONS = [
  { value: NotificationFrequency.RealTime, label: 'Real-time' },
  { value: NotificationFrequency.Hourly, label: 'Hourly digest' },
  { value: NotificationFrequency.Daily, label: 'Daily digest' },
  { value: NotificationFrequency.Weekly, label: 'Weekly digest' },
  { value: NotificationFrequency.Never, label: 'Disabled' },
];

function withDerivedLegacyFlags(request: UpdateNotificationPreferencesRequest): UpdateNotificationPreferencesRequest {
  const matrix = request.eventChannelPreferences ?? DEFAULT_EVENT_CHANNEL_PREFERENCES;
  const byEvent = (eventType: NotificationPreferenceEventType) => matrix.find(x => x.eventType === eventType);
  const started = byEvent(NotificationPreferenceEventType.JobStarted);
  const completed = byEvent(NotificationPreferenceEventType.JobCompleted);
  const paused = byEvent(NotificationPreferenceEventType.JobPaused);

  return {
    ...request,
    enableEmailNotifications: matrix.some(x => x.email),
    enablePushNotifications: matrix.some(x => x.push),
    enableInAppNotifications: matrix.some(x => x.inApp),
    enableTelegramNotifications: matrix.some(x => x.telegram),
    notifyOnStart: !!started && (started.inApp || started.email || started.push || started.telegram),
    notifyOnCompletion: !!completed && (completed.inApp || completed.email || completed.push || completed.telegram),
    notifyOnFailure: true,
    notifyOnPause: !!paused && (paused.inApp || paused.email || paused.push || paused.telegram),
    eventChannelPreferences: matrix.map(item => (
      item.eventType === NotificationPreferenceEventType.JobFailed
        ? { ...item, inApp: true }
        : item
    )),
  };
}

export function NotificationPreferencesPage({ embedded = false }: { embedded?: boolean }) {
  const navigate = useNavigate();
  const { data: preferences, isLoading, error } = useNotificationPreferences();
  const updateMutation = useUpdateNotificationPreferences();
  const pushSubscription = usePushSubscription();

  // Attention push (#708/#716) — independent from the legacy event×channel matrix.
  const attentionCategoriesQuery = useAttentionCategories();
  const attentionPreferencesQuery = useAttentionPushPreferences();
  const updateAttentionMutation = useUpdateAttentionPushPreferences();

  const [formState, setFormState] = useState<UpdateNotificationPreferencesRequest>(DEFAULT_PREFERENCES);
  const [isDirty, setIsDirty] = useState(false);
  const isDirtyRef = useRef(false);

  const [attentionState, setAttentionState] = useState<AttentionPushPreferencesDto | null>(null);
  const [isAttentionDirty, setIsAttentionDirty] = useState(false);
  const isAttentionDirtyRef = useRef(false);

  useEffect(() => {
    isDirtyRef.current = isDirty;
  }, [isDirty]);

  useEffect(() => {
    isAttentionDirtyRef.current = isAttentionDirty;
  }, [isAttentionDirty]);

  useEffect(() => {
    if (isDirtyRef.current) {
      return;
    }

    if (!preferences) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- sync server preferences into local form state
      setFormState(DEFAULT_PREFERENCES);
      setIsDirty(false);
      return;
    }

    const nextState = withDerivedLegacyFlags({
      enableEmailNotifications: preferences.enableEmailNotifications,
      enablePushNotifications: preferences.enablePushNotifications,
      enableInAppNotifications: preferences.enableInAppNotifications,
      enableTelegramNotifications: preferences.enableTelegramNotifications,
      notifyOnCompletion: preferences.notifyOnCompletion,
      notifyOnFailure: preferences.notifyOnFailure,
      notifyOnStart: preferences.notifyOnStart,
      notifyOnPause: preferences.notifyOnPause,
      eventChannelPreferences: preferences.eventChannelPreferences?.length
        ? preferences.eventChannelPreferences
        : DEFAULT_EVENT_CHANNEL_PREFERENCES,
      frequency: preferences.frequency,
      retentionDays: preferences.retentionDays,
    });

    setFormState(nextState);
    setIsDirty(false);
  }, [preferences]);

  useEffect(() => {
    if (isAttentionDirtyRef.current) return;
    const raw = attentionPreferencesQuery.data?.preferences ?? null;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- sync server attention preferences into local form state
    setAttentionState(hydrateAttentionPushPreferences(raw));
    setIsAttentionDirty(false);
  }, [attentionPreferencesQuery.data]);

  const isAnyPushEnabled = useMemo(
    () => (formState.eventChannelPreferences ?? []).some(item => item.push),
    [formState.eventChannelPreferences],
  );

  const categoryMeta = useMemo(
    () => buildCategoryUiMeta(attentionCategoriesQuery.data ?? null),
    [attentionCategoriesQuery.data],
  );

  const attentionFeatureAvailable = attentionPreferencesQuery.data?.featureAvailable ?? false;

  const updateMatrixField = (eventType: NotificationPreferenceEventType, key: 'inApp' | 'email' | 'push' | 'telegram', value: boolean) => {
    const current = formState.eventChannelPreferences ?? DEFAULT_EVENT_CHANNEL_PREFERENCES;
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

  const updateAttentionEnabled = (value: boolean) => {
    setAttentionState(prev => (prev ? { ...prev, enabled: value } : hydrateAttentionPushPreferences({ enabled: value, categories: {} })));
    setIsAttentionDirty(true);
  };

  const updateAttentionCategory = (id: string, value: boolean) => {
    setAttentionState(prev => {
      const base = prev ?? hydrateAttentionPushPreferences(null);
      return {
        ...base,
        categories: { ...base.categories, [id]: value },
      };
    });
    setIsAttentionDirty(true);
  };

  const getEventPreference = (eventType: NotificationPreferenceEventType) =>
    (formState.eventChannelPreferences ?? []).find(item => item.eventType === eventType);

  const handleSave = async () => {
    let ok = true;

    if (isDirty) {
      try {
        await updateMutation.mutateAsync(withDerivedLegacyFlags(formState));
        setIsDirty(false);
      } catch {
        ok = false;
        toast.error('Failed to save notification preferences');
      }
    }

    if (ok && isAttentionDirty && attentionFeatureAvailable && attentionState) {
      try {
        await updateAttentionMutation.mutateAsync(buildAttentionPushSavePayload(attentionState));
        setIsAttentionDirty(false);
      } catch {
        ok = false;
        toast.error('Failed to save operator alert preferences');
      }
    }

    if (ok) toast.success('Notification preferences saved');
  };

  const handleEnablePush = async () => {
    await pushSubscription.subscribe();
    if (pushSubscription.error) {
      toast.error(pushSubscription.error);
    } else if (pushSubscription.isSubscribed) {
      toast.success('Browser notifications enabled');
    }
  };

  if (isLoading) {
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

  if (error) {
    const errorContent = <Alert type="error">Failed to load notification preferences</Alert>;
    if (embedded) return errorContent;
    return (
      <PageTemplate title="Notification Preferences" icon={BellIcon}>
        {errorContent}
      </PageTemplate>
    );
  }

  const anyDirty = isDirty || (isAttentionDirty && attentionFeatureAvailable);
  const anyPending = updateMutation.isPending || updateAttentionMutation.isPending;

  const saveButton = (
    <Button
      variant="primary"
      onClick={handleSave}
      disabled={!anyDirty || anyPending}
    >
      {anyPending ? 'Saving...' : 'Save Preferences'}
    </Button>
  );

  const operatorEnabled = attentionState?.enabled ?? false;
  const categoryTogglesDisabled = !attentionFeatureAvailable || !operatorEnabled;

  const content = (
    <div className="space-y-6 max-w-4xl">
      <Card>
        <div className="p-4 border-b border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary">Event × Channel Matrix</h2>
          <p className="text-sm text-pf-text-secondary mt-1">Choose which channels each print event should use</p>
        </div>
        <div className="p-4">
          <div className="grid grid-cols-[2fr,1fr,1fr,1fr,1fr] gap-3 items-center text-xs uppercase tracking-wide text-pf-text-secondary mb-2">
            <div>Event</div>
            <div>In-App</div>
            <div>Email</div>
            <div>Browser Push</div>
            <div>Telegram</div>
          </div>
          <div className="space-y-3">
            {EVENT_ROWS.map(row => {
              const pref = getEventPreference(row.eventType);
              return (
                <div key={row.eventType} className="grid grid-cols-[2fr,1fr,1fr,1fr,1fr] gap-3 items-center py-2 border-b border-pf-border last:border-b-0">
                  <div>
                    <p className="text-sm font-medium text-pf-text-primary">{row.label}</p>
                    <p className="text-xs text-pf-text-secondary">{row.description}</p>
                  </div>
                  <div>
                    <Toggle
                      checked={row.eventType === NotificationPreferenceEventType.JobFailed ? true : !!pref?.inApp}
                      disabled={row.eventType === NotificationPreferenceEventType.JobFailed}
                      onChange={(e) => updateMatrixField(row.eventType, 'inApp', e.target.checked)}
                      aria-label={`${row.label} in-app`}
                    />
                    {row.eventType === NotificationPreferenceEventType.JobFailed && (
                      <p className="text-[11px] text-pf-text-secondary mt-1">Always on</p>
                    )}
                  </div>
                  <div>
                    <Toggle
                      checked={!!pref?.email}
                      onChange={(e) => updateMatrixField(row.eventType, 'email', e.target.checked)}
                      aria-label={`${row.label} email`}
                    />
                  </div>
                  <div>
                    <Toggle
                      checked={!!pref?.push}
                      onChange={(e) => updateMatrixField(row.eventType, 'push', e.target.checked)}
                      aria-label={`${row.label} push`}
                    />
                  </div>
                  <div>
                    <Toggle
                      checked={!!pref?.telegram}
                      onChange={(e) => updateMatrixField(row.eventType, 'telegram', e.target.checked)}
                      aria-label={`${row.label} Telegram`}
                    />
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </Card>

      <Card>
        <div className="p-4 border-b border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary">Operator Alerts</h2>
          <p className="text-sm text-pf-text-secondary mt-1">
            Farm-wide alerts routed through native push (runout, harvest, maintenance, offline, failure)
          </p>
        </div>
        <div className="p-4 space-y-4">
          {!attentionFeatureAvailable && (
            <Alert type="info">
              Operator alert notifications are not available on this server. Update the server to
              enable native push categories; existing print event preferences are unaffected.
            </Alert>
          )}

          <div className="flex items-center justify-between gap-3">
            <div>
              <p className="text-sm font-medium text-pf-text-primary">Enable operator alert push notifications</p>
              <p className="text-xs text-pf-text-secondary">Master switch for the categories listed below</p>
            </div>
            <Toggle
              checked={operatorEnabled}
              disabled={!attentionFeatureAvailable}
              onChange={(e) => updateAttentionEnabled(e.target.checked)}
              aria-label="Enable operator alert push notifications"
            />
          </div>

          <div className="pt-2 border-t border-pf-border">
            <ul className="space-y-3">
              {categoryMeta.map(cat => {
                const checked = !!attentionState?.categories[cat.id];
                return (
                  <li key={cat.id} className="flex items-center justify-between gap-3">
                    <div>
                      <p className="text-sm font-medium text-pf-text-primary">{cat.label}</p>
                      <p className="text-xs text-pf-text-secondary">{cat.description}</p>
                    </div>
                    <Toggle
                      checked={checked}
                      disabled={categoryTogglesDisabled}
                      onChange={(e) => updateAttentionCategory(cat.id, e.target.checked)}
                      aria-label={`${cat.label} operator alert`}
                    />
                  </li>
                );
              })}
            </ul>
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
