import { type ReactNode, useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import clsx from 'clsx';
import { useSlicer } from '@/hooks/useSlicer';
import { SettingsPagelet, type SettingMetadata, type SettingValue } from '@/common/components/SettingsPagelet';
import { SettingInputType } from '@/types/SettingInputType';
import { Button, Card, Input } from '@/common/components/ui';
import { CloseIcon, SearchIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
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
  AdminEmpty,
  useDirtyState,
  adminToast,
} from '@/common/components/admin';
import { getSectionRenderer } from '@/features/admin/settings/section-renderers';
import { isEssentialProperty } from '@/features/admin/settings/essential-manifest';
import { SettingsModeToggle } from '@/features/admin/settings/SettingsModeToggle';
import { useSettingsMode } from '@/features/admin/settings/useSettingsMode';
import { HighlightedText } from '@/features/admin/settings/HighlightedText';
import {
  groupMatchesQuery,
  propertyMatchesQuery,
  sectionMatchesQuery,
} from '@/features/admin/settings/search-utils';

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
  allowedGroups?: readonly string[];
  introText?: string;
  afterContent?: ReactNode;
}

type SectionValues = Record<string, SettingValue>;
type GroupValues = Record<string, SectionValues>;

/**
 * Per-section allowlist of property names to render inside a `GroupSaveBlock`.
 * The map is authoritative for *rendering only* — save and validation always
 * walk the full metadata so hidden fields survive round-trips.
 */
type PropertyVisibility = Readonly<Record<string, ReadonlySet<string>>>;

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
 * returns `data.errors` as either a `{ [section.field]: message }` map or a
 * `{ [fieldName]: message }` map for bare keys. We normalise both into
 * `{ [sectionKey]: { [fieldName]: message } }`.
 *
 * `defaultSectionKey` is the section the caller just posted. Because saves are
 * per-section (one `POST /api/settings/{key}` per changed group), any bare
 * error key returned by the server *must* belong to that section — searching
 * metadata for the first section that happens to declare the same property
 * name mis-attributes the error to whichever section renders first, which is
 * almost never the one the user was editing (`Enabled` alone is declared on 13
 * settings classes). Structured `section.field` keys from the backend still
 * override the default.
 *
 * A bare key that *equals* `defaultSectionKey` is treated as a section-level
 * error (returned in `sectionErrors`), not a field error — that shape is what
 * a memberless backend `ValidationException` produces, and no rendered
 * property is ever named the same as its section key, so attaching it to
 * `fieldErrors[defaultSectionKey][defaultSectionKey]` would render nowhere.
 */
function extractFieldErrors(
  err: unknown,
  defaultSectionKey: string,
): {
  fieldErrors: Record<string, Record<string, string>>;
  sectionErrors: Record<string, string>;
  message?: string;
} {
  const result: {
    fieldErrors: Record<string, Record<string, string>>;
    sectionErrors: Record<string, string>;
    message?: string;
  } = {
    fieldErrors: {},
    sectionErrors: {},
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
      const hasSection = parts.length > 1;
      const msgString = String(msg ?? 'Invalid value');
      if (hasSection) {
        const section = parts[0];
        const fieldName = parts.slice(1).join('.');
        result.fieldErrors[section] = result.fieldErrors[section] ?? {};
        result.fieldErrors[section][fieldName] = msgString;
      } else if (key === defaultSectionKey) {
        // Bare key equal to the section we posted → section-level error, i.e.
        // a memberless backend ValidationException (`throw new ValidationException("...")`
        // with no MemberNames). Attach to the section card instead of dropping
        // it under a bogus field name.
        result.sectionErrors[defaultSectionKey] = msgString;
      } else {
        // Bare property name → field error on the posted section (member-names
        // path). Wrong-section attribution here is fixed by the `defaultSectionKey`
        // heuristic; see `SettingsPageBareErrorAttribution.test.tsx`.
        result.fieldErrors[defaultSectionKey] = result.fieldErrors[defaultSectionKey] ?? {};
        result.fieldErrors[defaultSectionKey][key] = msgString;
      }
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
  /**
   * Optional per-section render filter. When present, only the listed property
   * names render for each section (empty set = hide the section's card
   * entirely). Save and validation still walk the full `metadataItems`, so
   * unfiltered fields survive round-trips even if they aren't currently
   * displayed — this is what lets Essential mode and search results share the
   * same save path.
   */
  propertyFilter?: PropertyVisibility;
  /**
   * Suppresses section-renderer extensions (Obico servers table, per-engine
   * slicer map, material price defaults). Enabled during active search because
   * those extensions are not scoped to search matches and would drown out the
   * hit list.
   */
  suppressExtensions?: boolean;
  /** Substring to highlight in property labels. Empty string disables highlighting. */
  searchQuery?: string;
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
  propertyFilter,
  suppressExtensions,
  searchQuery,
}: GroupSaveBlockProps) {
  const state = useDirtyState<GroupValues>(initialValues);
  // No re-baselining effect here, deliberately. The parent unmounts every block
  // while `loading` is true, so a reload always produces a *fresh mount* with the
  // correct baseline rather than a prop update on a live block. There is also no
  // parent refetch after save (see the note beside `loadSettings`), so
  // `initialValues` cannot change underneath a mounted block.
  //
  // If a refetch-while-mounted is ever introduced, re-baseline by changing the
  // block's `key` so React remounts it — do NOT reintroduce a syncing effect,
  // which trips `react-hooks/set-state-in-effect` and needs a suppression.
  const [fieldErrors, setFieldErrors] = useState<Record<string, Record<string, string>>>({});
  // Section-level errors — currently sourced only from a memberless backend
  // `ValidationException` where `errors[sectionKey]` carries the reason. Rendered
  // inline on the section card via `SettingsPagelet.error`.
  const [sectionErrors, setSectionErrors] = useState<Record<string, string>>({});
  const [isSaving, setIsSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

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
    setSectionErrors({});
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
    const perSectionMessages: Record<string, string> = {};
    let firstMessage: string | undefined;

    for (const sectionKey of changedSectionKeys) {
      const meta = metadataItems.find((m) => m.key === sectionKey);
      if (!meta) continue;
      try {
        await saveSettingsValues(sectionKey, state.values[sectionKey] ?? {});
      } catch (err) {
        failed.push(meta.displayName || meta.className);
        const extracted = extractFieldErrors(err, sectionKey);
        Object.assign(perSectionErrors, extracted.fieldErrors);
        Object.assign(perSectionMessages, extracted.sectionErrors);
        if (!firstMessage && extracted.message) firstMessage = extracted.message;
      }
    }

    setIsSaving(false);
    if (failed.length > 0) {
      setFieldErrors((prev) => ({ ...prev, ...perSectionErrors }));
      setSectionErrors((prev) => ({ ...prev, ...perSectionMessages }));
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
    setSectionErrors({});
    adminToast.success(`${groupDisplayName} settings saved`);
  }, [groupDisplayName, metadataItems, state]);

  const changedLabels = useMemo(() => {
    return state.changedKeys
      .map((k) => {
        const meta = metadataItems.find((m) => m.key === String(k));
        return meta?.displayName ?? meta?.className ?? String(k);
      });
  }, [metadataItems, state.changedKeys]);

  const query = searchQuery ?? '';

  return (
    <>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {metadataItems.map((meta) => {
          // Filter the section's properties for display without touching the
          // metadata used for save / validation above.
          let displayProps = meta.properties;
          if (propertyFilter) {
            const allowed = propertyFilter[meta.key];
            if (!allowed) {
              // Section not in the filter map => nothing visible => skip card.
              return null;
            }
            displayProps = meta.properties.filter((p) => allowed.has(p.name));
            if (displayProps.length === 0) {
              return null;
            }
          }
          const displayMeta = displayProps.length === meta.properties.length
            ? meta
            : { ...meta, properties: displayProps };

          const renderer = getSectionRenderer(meta);
          const fullWidth = Boolean(renderer?.fullWidth);
          const extensionRender = suppressExtensions ? undefined : renderer?.extension;
          const sectionValues = (state.values[meta.key] ?? {}) as SectionValues;
          const cardTitle = displayMeta.displayName || displayMeta.className;

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
                  {query ? <HighlightedText text={cardTitle} query={query} /> : cardTitle}
                </h4>
                {meta.description && (
                  <p className="text-xs text-pf-text-secondary mt-0.5">
                    {query
                      ? <HighlightedText text={meta.description} query={query} />
                      : meta.description}
                  </p>
                )}
              </Card.Header>
              <Card.Body className="flex-1 pt-0">
                <SettingsPagelet
                  metadata={displayMeta}
                  values={sectionValues}
                  onChange={(field, value) => handleFieldChange(meta.key, field, value)}
                  fieldErrors={fieldErrors[meta.key]}
                  error={sectionErrors[meta.key]}
                  compact
                  searchQuery={query}
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
  const { mode, setMode } = useSettingsMode();
  const [searchParams] = useSearchParams();
  const fieldParam = searchParams.get('field');
  const [query, setQuery] = useState('');
  const [metadata, setMetadata] = useState<SettingMetadata[]>([]);
  const [groupMetadata, setGroupMetadata] = useState<SettingGroupMetadata[]>([]);
  const [values, setValues] = useState<GroupValues>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const scrolledFieldRef = useRef<string | null>(null);

  // When the command palette deep-links to a specific setting the URL carries
  // `?field=<PropertyName>`. That property might live in an "advanced" section
  // that the Essential-mode filter would hide by default, so treat this URL as
  // an override without touching the user's persisted Essential preference.
  const effectiveMode = fieldParam ? 'everything' : mode;

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

  // When a `?field=<PropertyName>` deep-link resolves, scroll the matching row
  // into view and highlight it briefly. Ref writes live inside the RAF callback
  // (never during render), and this effect never calls setState — deliberately,
  // per the React-compiler guidance the palette task calls out. The URL param
  // stays put so the link remains copy-pasteable.
  useEffect(() => {
    if (loading || !fieldParam) {
      return;
    }
    if (scrolledFieldRef.current === fieldParam) {
      return;
    }
    if (typeof window === 'undefined') {
      return;
    }

    const raf = window.requestAnimationFrame(() => {
      // The attribute value is quoted, so only backslashes and quotes need
      // escaping. CSS.escape is for bare identifiers and would mangle the dot
      // separator in a qualified `Section.Property` key.
      const escapedField = fieldParam.replace(/["\\]/g, '\\$&');
      // Property names are NOT unique across sections — `Enabled` alone appears
      // in 13 settings classes, several of which render on the same page. A
      // qualified `Section.Property` link therefore has to match exactly, or the
      // deep-link scrolls to whichever section happens to render first. Bare
      // property names keep the old suffix match so older links still resolve.
      const selector = fieldParam.includes('.')
        ? `[data-setting-property="${escapedField}"]`
        : `[data-setting-property$=".${escapedField}"]`;
      const target = document.querySelector<HTMLElement>(selector);
      if (target) {
        const prefersReducedMotion = typeof window.matchMedia === 'function'
          && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        target.scrollIntoView({
          block: 'center',
          behavior: prefersReducedMotion ? 'auto' : 'smooth',
        });
        target.classList.add('pf-setting-focus');
        window.setTimeout(() => {
          target.classList.remove('pf-setting-focus');
        }, 2000);
      }
      scrolledFieldRef.current = fieldParam;
    });

    return () => {
      window.cancelAnimationFrame(raf);
    };
  }, [loading, fieldParam]);

  // Intentionally NO parent-level refetch after per-group save. Each group's
  // block accepts its own successful save as the new baseline via
  // `state.markPristine(values)`. Refetching here would clobber unsaved edits
  // in OTHER groups, which is a worse UX than the small risk of drifting from
  // server-normalised values until the next page load.

  const allowedGroupSet = useMemo(
    () => allowedGroups ? new Set(allowedGroups) : null,
    [allowedGroups],
  );

  const { sortedGroups, metadataByGroup } = useMemo(() => {
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

    // `byGroup` and `orderMap` are intermediates only — they feed `sorted` and
    // `metaByGroup` and are deliberately not returned. Exposing them "for a
    // future consumer" would mean shipping values nobody reads.
    return {
      sortedGroups: sorted,
      metadataByGroup: metaByGroup,
    };
  }, [allowedGroupSet, groupMetadata, isSlicerAvailable, metadata]);

  const getGroupDisplayName = useCallback((groupKey: string): string => {
    const group = groupMetadata.find((g) => g.key === groupKey);
    return group?.displayName || groupKey;
  }, [groupMetadata]);

  // Aggregate visibility: given the current mode and query, decide which
  // property in each section should render. This drives filtering AND is the
  // single source of truth for the "N of M shown" counter — pure memo, no side
  // effects or ref writes.
  const trimmedQuery = query.trim();

  const { visibleByKey, totalSettingsCount, visibleSettingsCount, matchingSectionCount } = useMemo(() => {
    const filter: Record<string, ReadonlySet<string>> = {};
    let total = 0;
    let visible = 0;
    let sectionsWithHits = 0;

    for (const group of sortedGroups) {
      const groupDisplay = groupMetadata.find((g) => g.key === group)?.displayName || group;
      const groupMatched = groupMatchesQuery(group, groupDisplay, trimmedQuery);

      for (const section of metadataByGroup[group]) {
        total += section.properties.length;
        const sectionMatched = sectionMatchesQuery(section, trimmedQuery);
        const allowed = new Set<string>();

        if (trimmedQuery) {
          if (groupMatched || sectionMatched) {
            // Section itself matched — show every property so the user can see
            // exactly what's in the section they searched for.
            for (const p of section.properties) allowed.add(p.name);
          } else {
            for (const p of section.properties) {
              if (propertyMatchesQuery(p, trimmedQuery)) allowed.add(p.name);
            }
          }
        } else if (effectiveMode === 'essential') {
          for (const p of section.properties) {
            if (isEssentialProperty(section.key, p.name)) allowed.add(p.name);
          }
        } else {
          for (const p of section.properties) allowed.add(p.name);
        }

        filter[section.key] = allowed;
        visible += allowed.size;
        if (allowed.size > 0) sectionsWithHits += 1;
      }
    }

    return {
      visibleByKey: filter,
      totalSettingsCount: total,
      visibleSettingsCount: visible,
      matchingSectionCount: sectionsWithHits,
    };
  }, [sortedGroups, metadataByGroup, groupMetadata, effectiveMode, trimmedQuery]);

  const toggleHelperText = useMemo(() => {
    if (trimmedQuery) {
      if (visibleSettingsCount === 0) return 'No matching settings';
      return `${visibleSettingsCount} match${visibleSettingsCount === 1 ? '' : 'es'} in ${matchingSectionCount} section${matchingSectionCount === 1 ? '' : 's'}`;
    }
    if (totalSettingsCount === 0) return undefined;
    return `Showing ${visibleSettingsCount} of ${totalSettingsCount} settings`;
  }, [matchingSectionCount, totalSettingsCount, trimmedQuery, visibleSettingsCount]);

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

  const searchActive = trimmedQuery.length > 0;
  const noMatchingResults = searchActive && visibleSettingsCount === 0;

  return (
    <div className="space-y-6" data-tour="settings-content">
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-pf-text-secondary">{introText}</p>
        <HelpButton onClick={startTour} />
      </div>

      {/* Some tabs (e.g. Farm) render only `afterContent` and have no metadata-driven
          settings at all. Showing a filter toggle and a search box there would give the
          user two controls that visibly do nothing. */}
      {totalSettingsCount > 0 && (
      <div
        className="flex flex-wrap items-center gap-3 rounded-lg border border-pf-border bg-pf-bg-0 p-3"
        data-testid="settings-mode-controls"
      >
        <SettingsModeToggle
          mode={mode}
          onModeChange={setMode}
          helperText={searchActive ? undefined : toggleHelperText}
        />
        <div className="ml-auto flex flex-1 items-center gap-2 min-w-[200px] max-w-md">
          <SearchIcon className="w-4 h-4 text-pf-text-secondary" />
          <Input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            placeholder="Filter fields on this page…"
            aria-label="Filter setting fields on this page"
            className="flex-1"
          />
          {searchActive && (
            <Button
              type="button"
              variant="secondary"
              size="sm"
              iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
              onClick={() => setQuery('')}
              aria-label="Clear field filter"
            >
              Clear
            </Button>
          )}
        </div>
        {searchActive && (
          <div
            className="basis-full text-xs text-pf-text-secondary"
            aria-live="polite"
            role="status"
          >
            {toggleHelperText}
            <span className="ml-2 text-pf-text-tertiary">
              This filter covers every field on this page, including advanced ones.
            </span>
          </div>
        )}
      </div>
      )}

      {noMatchingResults && (
        <AdminEmpty
          icon={<SettingsIcon className="w-10 h-10" />}
          title="No fields match your filter"
          description={`Nothing on this page matches “${trimmedQuery}”. Try a different term, clear the filter, or use the search box above to look across other settings pages.`}
          action={
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={() => setQuery('')}
            >
              Clear filter
            </Button>
          }
        />
      )}

      {sortedGroups.map((group) => {
        const groupMeta = metadataByGroup[group] ?? [];
        if (groupMeta.length === 0) return null;

        // Skip the whole group when every one of its sections is filtered out.
        // Keep the underlying `groupMeta` (full list) as the block's authority
        // for save / validation so we don't accidentally strand any dirty edits.
        const groupHasVisible = groupMeta.some((m) => (visibleByKey[m.key]?.size ?? 0) > 0);
        if (!groupHasVisible) return null;

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
                {searchActive
                  ? <HighlightedText text={groupDisplay} query={trimmedQuery} />
                  : groupDisplay}
              </span>
              <span className="h-px flex-1 bg-pf-border" />
            </h3>

            <GroupSaveBlock
              key={group}
              groupDisplayName={groupDisplay}
              metadataItems={groupMeta}
              initialValues={initialGroupValues}
              propertyFilter={visibleByKey}
              suppressExtensions={searchActive}
              searchQuery={trimmedQuery}
            />
          </section>
        );
      })}

      {afterContent}
    </div>
  );
}

export default SettingsPage;
