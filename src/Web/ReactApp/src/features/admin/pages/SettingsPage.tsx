import { type ReactNode, useEffect, useState, useCallback, useMemo } from 'react';
import clsx from 'clsx';
import { useSlicer } from '@/hooks/useSlicer';
import { SettingsPagelet, SettingMetadata, SettingValue } from '@/common/components/SettingsPagelet';
import { SettingInputType } from '@/types/SettingInputType';
import { Button, Card } from '@/common/components/ui';
import { usePageTour } from '@/common/hooks/usePageTour';
import { settingsTour } from '@/features/admin/tours/settings.tour';
import { HelpButton } from '@/common/components/HelpButton';
import {
  fetchSettingsMetadata,
  fetchSettingsGroups,
  saveAllSettings,
  fetchSettingsUnified,
  SettingGroupMetadata,
} from '@/services/settingsApi';
import { ObicoServersSection } from '@/features/admin/components/ObicoServersSection';
import { FailureDetectionStatusCard } from '@/features/admin/components/FailureDetectionStatusCard';

/** Sidebar navigation item for settings sections */
interface NavItem {
  key: string;
  displayName: string;
  icon?: string;
  group?: string;
  order?: number;
}

interface SettingsPageProps {
  allowedGroups?: string[];
  introText?: string;
  afterContent?: ReactNode;
}

export function SettingsPage({
  allowedGroups,
  introText = 'Configure system-wide defaults for your print farm.',
  afterContent,
}: SettingsPageProps = {}) {
  const { isSlicerAvailable } = useSlicer();
  const { startTour } = usePageTour({ tourId: 'settings', steps: settingsTour });
  const [metadata, setMetadata] = useState<SettingMetadata[]>([]);
  const [groupMetadata, setGroupMetadata] = useState<SettingGroupMetadata[]>([]);
  const [settingsValues, setSettingsValues] = useState<Record<string, Record<string, unknown>>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [fieldErrorsBySection, setFieldErrorsBySection] = useState<Record<string, Record<string, string>>>({});

  const refetchSettingsValues = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [meta, groups] = await Promise.all([
        fetchSettingsMetadata(),
        fetchSettingsGroups(),
      ]);
      setMetadata(meta);
      setGroupMetadata(groups);
      const unified = await fetchSettingsUnified();
      const valueMap: Record<string, Record<string, unknown>> = {};
      for (const m of meta) {
        const sectionKey = m.key;
        valueMap[sectionKey] = (unified && typeof unified === 'object' && sectionKey in unified) ? (unified as Record<string, unknown>)[sectionKey] as Record<string, unknown> : {};
      }
      setSettingsValues(valueMap);
      setLoading(false);
    } catch (err) {
      setError('Failed to reload settings after save.');
      setLoading(false);
      console.error('Error in refetchSettingsValues:', err);
    }
  }, []);

  const loadSettings = useCallback(() => {
    setLoading(true);
    setError(null);
    return Promise.all([fetchSettingsMetadata(), fetchSettingsGroups()])
      .then(async ([meta, groups]) => {
        setMetadata(meta);
        setGroupMetadata(groups);
        await refetchSettingsValues();
        setLoading(false);
      })
      .catch(() => {
        setError('Failed to load settings metadata.');
        setLoading(false);
      });
  // eslint-disable-next-line react-hooks/exhaustive-deps -- refetchSettingsValues is stable; load should not re-run on its identity
  }, []);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  const allowedGroupSet = useMemo(
    () => allowedGroups ? new Set(allowedGroups) : null,
    [allowedGroups],
  );

  const visibleMetadata = useMemo(
    () => metadata.filter((item) => !allowedGroupSet || allowedGroupSet.has(item.group || 'Other')),
    [allowedGroupSet, metadata],
  );

  // Build navigation items grouped and sorted
  const navItems: NavItem[] = visibleMetadata.map(m => ({
    key: m.key,
    displayName: m.displayName || m.className,
    icon: m.icon,
    group: m.group || 'Other',
    order: m.order ?? 999,
  })).sort((a, b) => (a.order ?? 999) - (b.order ?? 999));

  // Group nav items by group
  const groupedNavItems = navItems.reduce<Record<string, NavItem[]>>((acc, item) => {
    const group = item.group || 'Other';
    if (!acc[group]) acc[group] = [];
    acc[group].push(item);
    return acc;
  }, {});

  // Build a map of group key to order from group metadata
  const groupOrderMap = groupMetadata.reduce<Record<string, number>>((acc, g) => {
    acc[g.key] = g.order;
    return acc;
  }, {});

  // Sort groups by their order from group metadata, with fallback for undefined groups
  // Filter out Slicing group when slicer is not available
  const sortedGroups = useMemo(() => {
    return Object.keys(groupedNavItems)
      .filter(group => isSlicerAvailable || group !== 'Slicing')
      .sort((a, b) => {
        const orderA = groupOrderMap[a] ?? 999;
        const orderB = groupOrderMap[b] ?? 999;
        if (orderA !== orderB) return orderA - orderB;
        return a.localeCompare(b);
      });
  }, [groupedNavItems, groupOrderMap, isSlicerAvailable]);

  // Get display name for a group from metadata
  const getGroupDisplayName = (groupKey: string): string => {
    const group = groupMetadata.find(g => g.key === groupKey);
    return group?.displayName || groupKey;
  };

  const handleFieldChange = (className: string, field: string, value: SettingValue) => {
    setSettingsValues(prev => ({
      ...prev,
      [className]: {
        ...(prev[className] || {}),
        [field]: value,
      }
    }));

    const metaForSection = metadata.find(m => m.key === className);
    if (metaForSection) {
      const sectionValues = {
        ...(settingsValues[className] || {}),
        [field]: value,
      };
      const errs = validateSection(metaForSection, sectionValues);
      setFieldErrorsBySection(prev => ({ ...prev, [className]: errs }));
    }
  };

  const handleSave = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      const allErrors: Record<string, Record<string, string>> = {};
      for (const metaItem of visibleMetadata) {
        const sectionKey = metaItem.key;
        const vals = settingsValues[sectionKey] || {};
        const errs = validateSection(metaItem, vals);
        if (Object.keys(errs).length > 0) allErrors[sectionKey] = errs;
      }
      if (Object.keys(allErrors).length > 0) {
        setFieldErrorsBySection(allErrors);
        setSaveError('Fix validation errors before saving.');
        setSaving(false);
        return;
      }

      const payload: Record<string, unknown> = {};
      for (const meta of visibleMetadata) {
        const sectionKey = meta.key;
        payload[sectionKey] = settingsValues[sectionKey];
      }
      await saveAllSettings(payload);
      await refetchSettingsValues();
    } catch (err) {
      const maybe = err as unknown;
      if (typeof maybe === 'object' && maybe !== null) {
        const maybeObj = maybe as Record<string, unknown>;
        const resp = maybeObj['response'];
        if (resp && typeof resp === 'object') {
          const data = (resp as Record<string, unknown>)['data'];
          if (data && typeof data === 'object') {
            const errorsObj = (data as Record<string, unknown>)['errors'] as Record<string, unknown> | undefined;
            if (errorsObj && typeof errorsObj === 'object') {
              const newFieldErrors: Record<string, Record<string, string>> = {};
              for (const [key, msg] of Object.entries(errorsObj)) {
                const parts = key.split('.');
                let section = parts.length > 1 ? parts[0] : undefined;
                const fieldName = parts.length > 1 ? parts.slice(1).join('.') : parts[0];
                if (!section) {
                  const found = metadata.find(m => m.properties.some(p => p.name === fieldName));
                  section = found?.key;
                }
                if (section) {
                  newFieldErrors[section] = newFieldErrors[section] || {};
                  newFieldErrors[section][fieldName] = String(msg ?? 'Invalid value');
                } else {
                  setSaveError(String(msg ?? 'Failed to save settings.'));
                }
              }
              setFieldErrorsBySection(prev => ({ ...prev, ...newFieldErrors }));
            }
            if ('message' in (data as Record<string, unknown>) && !saveError) {
              setSaveError(String((data as Record<string, unknown>)['message'] ?? ''));
            }
          }
        }
      }
      if (!saveError) {
        setSaveError('Failed to save settings.');
      }
    } finally {
      setSaving(false);
    }
  };

  const validateSection = (metaItem: SettingMetadata, valuesObj: Record<string, unknown>): Record<string, string> => {
    const errs: Record<string, string> = {};
    for (const prop of metaItem.properties) {
      const val = valuesObj[prop.name];
      if (prop.attributes.includes('RequiredAttribute')) {
        const empty = val === undefined || val === null || val === '' || (Array.isArray(val) && val.length === 0);
        if (empty) { errs[prop.name] = 'This field is required.'; continue; }
      }
      const isNumberType = prop.display?.inputType === SettingInputType.Number || ['Number', 'int', 'double'].includes(prop.type);
      if (isNumberType) {
        const num = typeof val === 'number' ? val : (typeof val === 'string' && val !== '' ? Number(val) : NaN);
        if (!isNaN(num)) {
          if (typeof prop.display?.minValue === 'number' && num < prop.display!.minValue!) errs[prop.name] = `Minimum is ${prop.display!.minValue}`;
          if (typeof prop.display?.maxValue === 'number' && num > prop.display!.maxValue!) errs[prop.name] = `Maximum is ${prop.display!.maxValue}`;
        }
      }
    }
    return errs;
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center py-16" role="status" aria-label="Loading settings">
        <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-8 text-center" role="alert">
        <p className="text-pf-error">{error}</p>
        <Button
          type="button"
          variant="secondary"
          size="sm"
          className="mt-4"
          onClick={() => void loadSettings()}
        >
          Retry
        </Button>
      </div>
    );
  }

  return (
    <div className="space-y-6" data-tour="settings-content">
      {/* Header with Help Button */}
      <div className="flex items-center justify-between">
        <p className="text-sm text-pf-text-secondary">
          {introText}
        </p>
        <HelpButton onClick={startTour} />
      </div>

      {/* Multi-column grid layout — grouped by category */}
      {sortedGroups.map((group) => (
        <section key={group} aria-labelledby={`group-${group}`}>
          {/* Group header */}
          <h3
            id={`group-${group}`}
            className="text-base font-semibold text-pf-text-primary mb-4 flex items-center gap-2"
          >
            <span className="h-px flex-1 bg-pf-border" />
            <span className="px-3 text-pf-text-secondary uppercase tracking-wider text-xs">
              {getGroupDisplayName(group)}
            </span>
            <span className="h-px flex-1 bg-pf-border" />
          </h3>

          {/* Responsive grid: 1 col mobile, 2 cols tablet+ */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            {groupedNavItems[group].map((item) => {
              const meta = metadata.find((m) => m.key === item.key);
              if (!meta) return null;

              const hasObico = meta.key === 'Obico';

              return (
                <Card
                  key={meta.key}
                  className={clsx(
                    'flex flex-col',
                    // Obico section spans full width due to extra content
                    hasObico && 'md:col-span-2'
                  )}
                >
                  <Card.Header className="pb-2">
                    <h4 className="text-sm font-semibold text-pf-text-primary">
                      {meta.displayName || meta.className}
                    </h4>
                    {meta.description && (
                      <p className="text-xs text-pf-text-secondary mt-0.5">
                        {meta.description}
                      </p>
                    )}
                  </Card.Header>
                  <Card.Body className="flex-1 pt-0">
                    <SettingsPagelet
                      metadata={meta}
                      values={(settingsValues[meta.key] || {}) as Record<string, SettingValue>}
                      onChange={(field, value) => handleFieldChange(meta.key, field, value)}
                      fieldErrors={fieldErrorsBySection[meta.key]}
                      compact
                    />
                    {hasObico && (
                      <div className="mt-4 grid grid-cols-1 lg:grid-cols-2 gap-4">
                        <FailureDetectionStatusCard />
                        <ObicoServersSection />
                      </div>
                    )}
                  </Card.Body>
                </Card>
              );
            })}
          </div>
        </section>
      ))}

      {afterContent}

      {/* Save button bar */}
      {saveError && (
        <div className="text-pf-error text-sm" role="alert">
          {saveError}
        </div>
      )}
      <div
        className="sticky bottom-0 py-4 flex justify-end border-t border-pf-border bg-pf-bg-0/95 backdrop-blur-xs"
        data-tour="settings-save"
      >
        <Button
          type="button"
          onClick={handleSave}
          variant="primary"
          disabled={saving}
          loading={saving}
        >
          {saving ? 'Saving...' : 'Save All Settings'}
        </Button>
      </div>
    </div>
  );
}
