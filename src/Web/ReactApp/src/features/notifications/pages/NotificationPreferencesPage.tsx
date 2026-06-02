import { useState } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Card, Toggle, Select, Alert, Button, Spinner } from '@/common/components/ui';
import { BellIcon } from '@/common/components/icons/MdiIcons';
import { useNotificationPreferences, useUpdateNotificationPreferences } from '@/features/notifications/hooks/useNotificationPreferences';
import { usePushSubscription } from '@/features/notifications/hooks/usePushSubscription';
import { NotificationFrequency } from '@/types/api';
import type { UpdateNotificationPreferencesRequest } from '@/types/api';
import { toast } from 'sonner';

const DEFAULT_PREFERENCES: UpdateNotificationPreferencesRequest = {
  enableEmailNotifications: true,
  enablePushNotifications: true,
  enableInAppNotifications: true,
  notifyOnCompletion: true,
  notifyOnFailure: true,
  notifyOnStart: false,
  notifyOnPause: true,
  frequency: NotificationFrequency.RealTime,
  retentionDays: 30,
};

interface EventRow {
  key: keyof Pick<UpdateNotificationPreferencesRequest, 'notifyOnCompletion' | 'notifyOnFailure' | 'notifyOnStart' | 'notifyOnPause'>;
  label: string;
  description: string;
}

const EVENT_ROWS: EventRow[] = [
  { key: 'notifyOnCompletion', label: 'Print Complete', description: 'When a print job finishes successfully' },
  { key: 'notifyOnFailure', label: 'Print Failed', description: 'When a print job fails or encounters errors' },
  { key: 'notifyOnStart', label: 'Print Started', description: 'When a print job begins printing' },
  { key: 'notifyOnPause', label: 'Print Paused', description: 'When a print job is paused (includes resume)' },
];

const FREQUENCY_OPTIONS = [
  { value: NotificationFrequency.RealTime, label: 'Real-time' },
  { value: NotificationFrequency.Hourly, label: 'Hourly digest' },
  { value: NotificationFrequency.Daily, label: 'Daily digest' },
  { value: NotificationFrequency.Weekly, label: 'Weekly digest' },
  { value: NotificationFrequency.Never, label: 'Disabled' },
];

export function NotificationPreferencesPage({ embedded = false }: { embedded?: boolean }) {
  const { data: preferences, isLoading, error } = useNotificationPreferences();
  const updateMutation = useUpdateNotificationPreferences();
  const pushSubscription = usePushSubscription();

  const initialState: UpdateNotificationPreferencesRequest = preferences
    ? {
        enableEmailNotifications: preferences.enableEmailNotifications,
        enablePushNotifications: preferences.enablePushNotifications,
        enableInAppNotifications: preferences.enableInAppNotifications,
        notifyOnCompletion: preferences.notifyOnCompletion,
        notifyOnFailure: preferences.notifyOnFailure,
        notifyOnStart: preferences.notifyOnStart,
        notifyOnPause: preferences.notifyOnPause,
        frequency: preferences.frequency,
        retentionDays: preferences.retentionDays,
      }
    : DEFAULT_PREFERENCES;

  const [formState, setFormState] = useState<UpdateNotificationPreferencesRequest>(initialState);
  const [isDirty, setIsDirty] = useState(false);
  const [lastLoadedId, setLastLoadedId] = useState<string | undefined>(undefined);

  // Sync form when server data changes (e.g. first load)
  const preferencesId = preferences?.userId;
  if (preferencesId && preferencesId !== lastLoadedId) {
    setLastLoadedId(preferencesId);
    setFormState(initialState);
    setIsDirty(false);
  }

  const updateField = <K extends keyof UpdateNotificationPreferencesRequest>(
    key: K,
    value: UpdateNotificationPreferencesRequest[K]
  ) => {
    setFormState(prev => ({ ...prev, [key]: value }));
    setIsDirty(true);
  };

  const handleSave = async () => {
    try {
      await updateMutation.mutateAsync(formState);
      setIsDirty(false);
      toast.success('Notification preferences saved');
    } catch {
      toast.error('Failed to save notification preferences');
    }
  };

  const handleEnablePush = async () => {
    await pushSubscription.subscribe();
    if (!pushSubscription.error) {
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
    const errorContent = <Alert variant="error">Failed to load notification preferences</Alert>;
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
      disabled={!isDirty || updateMutation.isPending}
    >
      {updateMutation.isPending ? 'Saving...' : 'Save Preferences'}
    </Button>
  );

  const content = (
    <div className="space-y-6 max-w-4xl">
      {/* Channel Toggles */}
      <Card>
          <div className="p-4 border-b border-pf-border">
            <h2 className="text-lg font-semibold text-pf-text-primary">Delivery Channels</h2>
            <p className="text-sm text-pf-text-secondary mt-1">Choose how you receive notifications</p>
          </div>
          <div className="p-4 space-y-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium text-pf-text-primary">Email</p>
                <p className="text-xs text-pf-text-secondary">Receive notifications via email</p>
              </div>
              <Toggle
                checked={formState.enableEmailNotifications}
                onChange={(e) => updateField('enableEmailNotifications', e.target.checked)}
                aria-label="Enable email notifications"
              />
            </div>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium text-pf-text-primary">Browser Push</p>
                <p className="text-xs text-pf-text-secondary">Receive push notifications in your browser</p>
              </div>
              <div className="flex items-center gap-2">
                <Toggle
                  checked={formState.enablePushNotifications}
                  onChange={(e) => updateField('enablePushNotifications', e.target.checked)}
                  aria-label="Enable push notifications"
                />
                {formState.enablePushNotifications && !pushSubscription.isSubscribed && pushSubscription.isSupported && (
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={handleEnablePush}
                    disabled={pushSubscription.isLoading}
                  >
                    {pushSubscription.isLoading ? 'Enabling...' : 'Enable in Browser'}
                  </Button>
                )}
              </div>
            </div>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium text-pf-text-primary">In-App</p>
                <p className="text-xs text-pf-text-secondary">Show notifications within the dashboard</p>
              </div>
              <Toggle
                checked={formState.enableInAppNotifications}
                onChange={(e) => updateField('enableInAppNotifications', e.target.checked)}
                aria-label="Enable in-app notifications"
              />
            </div>
          </div>
        </Card>

        {pushSubscription.error && (
          <Alert variant="warning">{pushSubscription.error}</Alert>
        )}

        {/* Event × Channel Matrix */}
        <Card>
          <div className="p-4 border-b border-pf-border">
            <h2 className="text-lg font-semibold text-pf-text-primary">Event Notifications</h2>
            <p className="text-sm text-pf-text-secondary mt-1">Choose which events trigger notifications</p>
          </div>
          <div className="p-4">
            <div className="space-y-3">
              {EVENT_ROWS.map(row => (
                <div key={row.key} className="flex items-center justify-between py-2 border-b border-pf-border last:border-b-0">
                  <div>
                    <p className="text-sm font-medium text-pf-text-primary">{row.label}</p>
                    <p className="text-xs text-pf-text-secondary">{row.description}</p>
                  </div>
                  <Toggle
                    checked={formState[row.key]}
                    onChange={(e) => updateField(row.key, e.target.checked)}
                    aria-label={`Notify on ${row.label}`}
                  />
                </div>
              ))}
            </div>
          </div>
        </Card>

        {/* Frequency */}
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

  return (
    <PageTemplate
      title="Notification Preferences"
      icon={BellIcon}
      actions={saveButton}
    >
      {content}
    </PageTemplate>
  );
}
