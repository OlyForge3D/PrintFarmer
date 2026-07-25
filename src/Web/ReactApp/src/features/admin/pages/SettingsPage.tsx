import { type ReactNode, useEffect, useState, useCallback, useMemo, useRef } from 'react';
import clsx from 'clsx';
import { useSlicer } from '@/hooks/useSlicer';
import { SettingsPagelet, type SettingMetadata, type SettingValue } from '@/common/components/SettingsPagelet';
import { SettingInputType } from '@/types/SettingInputType';
import { Card } from '@/common/components/ui';
import { usePageTour } from '@/common/hooks/usePageTour';
import { settingsTour } from '@/features/admin/tours/settings.tour';
import { HelpButton } from '@/common/components/HelpButton';
import {
  fetchSettingsMetadata,
  fetchSettingsGroups,
  saveSettingsValues,
  fetchSettingsUnified,
  type SettingGroupMetadata,
} from '@/services/settingsApi';
import {
  AdminLoading,
  AdminError,
  AdminSaveBar,
  useDirtyState,
  adminToast,
} from '@/common/components/admin';
import { getSectionRenderer } from '@/features/admin/settings/section-renderers';

/**
 * Sidebar navigation item for settings sections. `order` is nominally optional
 * but we assign a fallback so sort compares always work.
 */
interface NavItem {
  key: string;
  displayName: string;
  icon?: string;
  group: string;
  order: number;
}

interface SettingsPageProps {
  allowedGroups?: string[];
  introText?: string;
  afterContent?: ReactNode;
}

type SectionValues = Record<string, SettingValue>;
type GroupValues = Record<string, SectionValues>;

function validateSection(
  metaItem: SettingMetadata,
  valuesObj: SectionValues,
): Record<string, string> {
  const errs: Record<string, string> = {};
  for (const prop of metaItem.properties) {
    const val = valuesObj[prop.name];
    if (prop.attributes.includes('RequiredAttribute')) {
      const empty = val === undefined
        || val === null
        || val === ''
        || (Array.isArray(val) && val.length === 0);
      if (empty) {
        errs[prop.name] = 'This field is required.';
        continue;
      }
    }
    const isNumberType = prop.display?.inputType === SettingInputType.Number
      || ['Number', 'int', 'double'].includes(prop.type);
    if (isNumberType) {
      const num = typeof val === 'number'
        ? val
        : typeof val === 'string' && val !== ''
          ? Number(val)
          : NaN;
      if (!Number.isNaN(num)) {
        if (typeof prop.display?.minValue === 'number' && num < prop.display.minValue) {
          errs[prop.name] = `Minimum is ${prop.display.minValue}`;
        }
        if (typeof prop.display?.maxValue === 'number' && num > prop.display.maxValue) {
          errs[prop.name] = `Maximum is ${prop.display.maxValue}`;
        }
      }
    }
  }
  return errs;
}

/**
 * Extract per-section field errors from a save failure. The unified controller
 * returns `data.errors` as either a flat `{ [dottedKey]: message }` map or a
 * plain `{ [fieldName]: message }` map when a single section is saved. We
 * normalise both into `{ [sectionKey]: { [fieldName]: message } }`.
 */
function extractFieldErrors(
  err: unknown,
  metadata: SettingMetadata[],
  defaultSectionKey: string,
): { fieldErrors: Record<string, Record<string, string>>; message?: string } {
  const result: { fieldErrors: Record<string, Record<string, string>>; message?: string } = {
    fieldErrors: {},
  };
  if (typeof err !== 'object' || err === null) return result;
  const maybeObj = err as Record<string, unknown>;
  const resp = maybeObj['response'];
  if (!resp || typeof resp !== 'object') return result;
  const data = (resp as Record<string, unknown>)['data'];
  if (!data || typeof data !== 'object') return result;
  const dataObj = data as Record<string, unknown>;
  const errorsObj = dataObj['errors'] as Record<string, unknown> | undefined;
  if (errorsObj && typeof errorsObj === 'object') {
    for (const [key, msg] of Object.entries(errorsObj)) {
      const parts = key.split('.');
      let section = parts.length > 1 ? parts[0] : undefined;
      const fieldName = parts.length > 1 ? parts.slice(1).join('.') : parts[0];
      if (!section) {
        const found = metadata.find((m) => m.properties.some((p) => p.name === fieldName));
        section = found?.key ?? defaultSectionKey;
      }
      result.fieldErrors[section] = result.fieldErrors[section] ?? {};
      result.fieldErrors[section][fieldName] = String(msg ?? 'Invalid value');
    }
  }
  if (typeof dataObj['message'] === 'string') {
    result.message = dataObj['message'] as string;
  }
  return result;
}

interface GroupSaveBlockProps {
  groupDisplayName: string;
  metadataItems: SettingMetadata[];
  initialValues: GroupValues;
}

/**
 * Renders one settings group as a grid of section cards, backed by its own
 * `useDirtyState`. Each dirty section is saved via its dedicated per-section
 * endpoint (`POST /api/settings/{key}`) — never the batch `saveAll` endpoint.
 *
 * The block is intentionally isolated so multiple groups can be edited
 * independently; the AdminSaveBar it renders only reflects this group's dirty
 * state, and saving one group leaves other groups' unsaved edits intact.
 */
function GroupSaveBlock({
  groupDisplayName,
  metadataItems,
  initialValues,
}: GroupSaveBlockProps) {
  const state = useDirtyState<GroupValues>(initialValues);
  const [fieldErrors, setFieldErrors] = useState<Record<string, Record<string, string>>>({});
  const [isSaving, setIsSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  // Re-baseline when the parent hands us a new snapshot (fresh load).
  // Uses a JSON key so we only re-baseline when the content actually changes,
  // not on every render. We intentionally do NOT re-baseline while the block
  // is dirty — that would silently discard the user's in-flight edits.
  const initialKey = useMemo(() => {
    try {
      return JSON.stringify(initialValues);
    } catch {
      return String(Date.now());
    }
  }, [initialValues]);
  const lastBaseline = useRef(initialKey);
  useEffect(() => {
    if (lastBaseline.current === initialKey) return;
    if (state.isDirty) return;
    lastBaseline.current = initialKey;
    state.markPristine(initialValues);
    setFieldErrors({});
    setSaveError(null);
  // markPristine identity changes with values; we only want this effect on baseline changes.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialKey]);

  const handleFieldChange = useCallback((sectionKey: string, field: string, value: SettingValue) => {
    const nextSection = { ...(state.values[sectionKey] ?? {}), [field]: value };
    const nextGroup = { ...state.values, [sectionKey]: nextSection };
    state.replaceValues(nextGroup);

    const metaForSection = metadataItems.find((m) => m.key === sectionKey);
    if (metaForSection) {
      const errs = validateSection(metaForSection, nextSection);
      setFieldErrors((prev) => ({ ...prev, [sectionKey]: errs }));
    }
  }, [metadataItems, state]);

  const handleDiscard = useCallback(() => {
    state.reset();
    setFieldErrors({});
    setSaveError(null);
  }, [state]);

  const handleSave = useCallback(async () => {
    // Validate every changed section before hitting the wire.
    const allErrors: Record<string, Record<string, string>> = {};
    for (const key of state.changedKeys) {
      const sectionKey = String(key);
      const meta = metadataItems.find((m) => m.key === sectionKey);
      if (!meta) continue;
      const errs = validateSection(meta, state.values[sectionKey] ?? {});
      if (Object.keys(errs).length > 0) allErrors[sectionKey] = errs;
    }
    if (Object.keys(allErrors).length > 0) {
      setFieldErrors(allErrors);
      setSaveError('Fix validation errors before saving.');
      return;
    }

    setIsSaving(true);
    setSaveError(null);
    const changedSectionKeys = state.changedKeys.map((k) => String(k));
    const failed: string[] = [];
    const perSectionErrors: Record<string, Record<string, string>> = {};
    let firstMessage: string | undefined;

    for (const sectionKey of changedSectionKeys) {
      const meta = metadataItems.find((m) => m.key === sectionKey);
      if (!meta) continue;
      try {
        await saveSettingsValues(sectionKey, state.values[sectionKey] ?? {});
      } catch (err) {
        failed.push(meta.displayName || meta.className);
        const extracted = extractFieldErrors(err, metadataItems, sectionKey);
        Object.assign(perSectionErrors, extracted.fieldErrors);
        if (!firstMessage && extracted.message) firstMessage = extracted.message;
      }
    }

    setIsSaving(false);
    if (failed.length > 0) {
      setFieldErrors((prev) => ({ ...prev, ...perSectionErrors }));
      const summary = failed.length === 1
        ? `Failed to save ${failed[0]}.`
        : `Failed to save ${failed.length} sections: ${failed.join(', ')}.`;
      setSaveError(firstMessage ?? summary);
      adminToast.error(summary);
      // Keep the dirty state so the user can retry or discard.
      return;
    }

    // All sections saved — accept current values as the new baseline.
    state.markPristine(state.values);
    setFieldErrors({});
    adminToast.success(`${groupDisplayName} settings saved`);
  }, [groupDisplayName, metadataItems, state]);

  const changedLabels = useMemo(() => {
    return state.changedKeys
      .map((k) => {
        const meta = metadataItems.find((m) => m.key === String(k));
        return meta?.displayName ?? meta?.className ?? String(k);
      });
  }, [metadataItems, state.changedKeys]);

  return (
    <>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {metadataItems.map((meta) => {
          const renderer = getSectionRenderer(meta);
          const fullWidth = Boolean(renderer?.fullWidth);
          const extensionRender = renderer?.extension;
          const sectionValues = (state.values[meta.key] ?? {}) as SectionValues;

          return (
            <Card
              key={meta.key}
              className={clsx(
                'flex flex-col',
                fullWidth && 'md:col-span-2',
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
                  values={sectionValues}
                  onChange={(field, value) => handleFieldChange(meta.key, field, value)}
                  fieldErrors={fieldErrors[meta.key]}
                  compact
                />
                {extensionRender?.({
                  values: sectionValues,
                  onChange: (field, value) => handleFieldChange(meta.key, field, value),
                })}
              </Card.Body>
            </Card>
          );
        })}
      </div>

      <AdminSaveBar
        isDirty={state.isDirty}
        changeCount={state.changedCount}
        changedLabels={changedLabels}
        onDiscard={handleDiscard}
        onSave={handleSave}
        isSaving={isSaving}
        error={saveError}
        saveLabel={`Save ${groupDisplayName}`}
      />
    </>
  );
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
  const [values, setValues] = useState<GroupValues>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const loadSettings = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [meta, groups] = await Promise.all([
        fetchSettingsMetadata(),
        fetchSettingsGroups(),
      ]);
      const unified = await fetchSettingsUnified();
      const valueMap: GroupValues = {};
      for (const m of meta) {
        const sectionKey = m.key;
        valueMap[sectionKey] = (unified && typeof unified === 'object' && sectionKey in unified)
          ? ((unified as Record<string, unknown>)[sectionKey] as SectionValues)
          : {};
      }
      setMetadata(meta);
      setGroupMetadata(groups);
      setValues(valueMap);
    } catch (err) {
      setError(err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  // Intentionally NO parent-level refetch after per-group save. Each group's
  // block accepts its own successful save as the new baseline via
  // `state.markPristine(values)`. Refetching here would clobber unsaved edits
  // in OTHER groups, which is a worse UX than the small risk of drifting from
  // server-normalised values until the next page load.

  const allowedGroupSet = useMemo(
    () => allowedGroups ? new Set(allowedGroups) : null,
    [allowedGroups],
  );

  const { groupedNavItems, groupOrderMap, sortedGroups, metadataByGroup } = useMemo(() => {
    const visibleMetadata = metadata.filter(
      (item) => !allowedGroupSet || allowedGroupSet.has(item.group || 'Other'),
    );

    const navItems: NavItem[] = visibleMetadata
      .map((m) => ({
        key: m.key,
        displayName: m.displayName || m.className,
        icon: m.icon,
        group: m.group || 'Other',
        order: m.order ?? 999,
      }))
      .sort((a, b) => a.order - b.order);

    const byGroup: Record<string, NavItem[]> = {};
    for (const item of navItems) {
      const group = item.group;
      if (!byGroup[group]) byGroup[group] = [];
      byGroup[group].push(item);
    }

    const orderMap: Record<string, number> = {};
    for (const g of groupMetadata) orderMap[g.key] = g.order;

    const sorted = Object.keys(byGroup)
      .filter((group) => isSlicerAvailable || group !== 'Slicing')
      .sort((a, b) => {
        const orderA = orderMap[a] ?? 999;
        const orderB = orderMap[b] ?? 999;
        if (orderA !== orderB) return orderA - orderB;
        return a.localeCompare(b);
      });

    const metaByGroup: Record<string, SettingMetadata[]> = {};
    for (const group of sorted) {
      metaByGroup[group] = byGroup[group]
        .map((item) => visibleMetadata.find((m) => m.key === item.key))
        .filter((m): m is SettingMetadata => Boolean(m));
    }

    return {
      groupedNavItems: byGroup,
      groupOrderMap: orderMap,
      sortedGroups: sorted,
      metadataByGroup: metaByGroup,
    };
  }, [allowedGroupSet, groupMetadata, isSlicerAvailable, metadata]);
  // groupedNavItems and groupOrderMap are exposed so future consumers (e.g. a
  // future scroll-linked TOC) don't need to reproduce the derivation.
  void groupedNavItems;
  void groupOrderMap;

  const getGroupDisplayName = useCallback((groupKey: string): string => {
    const group = groupMetadata.find((g) => g.key === groupKey);
    return group?.displayName || groupKey;
  }, [groupMetadata]);

  if (loading) {
    return <AdminLoading variant="form" label="Loading settings" rows={5} />;
  }

  if (error) {
    return (
      <AdminError
        title="Failed to load settings"
        description="We couldn't reach the settings service. Check your connection and try again."
        error={error}
        onRetry={() => void loadSettings()}
      />
    );
  }

  return (
    <div className="space-y-6" data-tour="settings-content">
      <div className="flex items-center justify-between">
        <p className="text-sm text-pf-text-secondary">{introText}</p>
        <HelpButton onClick={startTour} />
      </div>

      {sortedGroups.map((group) => {
        const groupMeta = metadataByGroup[group] ?? [];
        if (groupMeta.length === 0) return null;

        // Build a slim initial-values object scoped to this group so the
        // GroupSaveBlock's dirty state only tracks its own sections.
        const initialGroupValues: GroupValues = {};
        for (const m of groupMeta) {
          initialGroupValues[m.key] = (values[m.key] ?? {}) as SectionValues;
        }
        const groupDisplay = getGroupDisplayName(group);

        return (
          <section key={group} aria-labelledby={`group-${group}`}>
            <h3
              id={`group-${group}`}
              className="text-base font-semibold text-pf-text-primary mb-4 flex items-center gap-2"
            >
              <span className="h-px flex-1 bg-pf-border" />
              <span className="px-3 text-pf-text-secondary uppercase tracking-wider text-xs">
                {groupDisplay}
              </span>
              <span className="h-px flex-1 bg-pf-border" />
            </h3>

            <GroupSaveBlock
              key={group}
              groupDisplayName={groupDisplay}
              metadataItems={groupMeta}
              initialValues={initialGroupValues}
            />
          </section>
        );
      })}

      {afterContent}
    </div>
  );
}

export default SettingsPage;
