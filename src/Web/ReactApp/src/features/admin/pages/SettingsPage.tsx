import { type ReactNode, useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { createPortal } from 'react-dom';
import { useSettingsFooterSlot } from '@/features/settings/components/settingsFooterSlotContext';
import { useSearchParams } from 'react-router';
import clsx from 'clsx';
import { useSlicer } from '@/hooks/useSlicer';
import { SettingsPagelet, type SettingMetadata, type SettingValue } from '@/common/components/SettingsPagelet';
import { Badge, Button, Card, Input } from '@/common/components/ui';
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
  AdminSection,
  AdminEmpty,
  AttentionRow,
  useDirtyState,
  isStructurallyEqual,
  adminToast,
} from '@/common/components/admin';
import {
  deriveSettingsIssues,
  countIssuesBySection,
  severityBySection,
  focusSettingProperty,
  validateSection,
  type SettingsIssue,
} from '@/features/admin/settings/settingsAttention';
import {
  SettingsSaveRegistryContext,
  useSettingsSaveRegistry,
  formatDirtySummary,
  formatSaveOutcome,
  type GroupDirtySummary,
  type GroupSaveActions,
  type GroupSaveOutcome,
} from '@/features/admin/settings/settingsSaveRegistry';
import { getSectionRenderer } from '@/features/admin/settings/section-renderers';
import { isEssentialProperty } from '@/features/admin/settings/essential-manifest';
import { SettingsModeToggle } from '@/features/admin/settings/SettingsModeToggle';
import { SettingsHeaderPortal } from '@/features/settings/components/SettingsHeaderPortal';
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

/**
 * Validation lives in `settingsAttention` so the save path and the attention
 * banner run the identical predicates. See that module for why.
 */

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
  /** Stable group key. Used to address this block in the page's save registry. */
  group: string;
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
 * Card flow for a settings group.
 *
 * The old `grid-cols-1 md:grid-cols-2` had two defects: it never went past two
 * columns at any width, and a grid row is as tall as its tallest cell — so a
 * one-field card parked next to a twelve-field card reserved the tall one's
 * full height in dead whitespace. A CSS multi-column flow packs cards tightly
 * instead, and `break-inside-avoid` keeps a card whole.
 *
 * The breakpoints are container queries, not viewport queries, because the
 * space available to cards depends on the app rail and the settings sidebar,
 * not on the window. Measured on this page: a 1440px window leaves the flow
 * 814px, 1600px leaves 974px, 1920px leaves 1294px, 2560px leaves 1934px.
 *
 * The thresholds derive from a single number — the `26rem` (416px) card width
 * at which `SettingsPagelet` puts a field's label and control side by side. A
 * column is only added when *every* resulting card still clears it:
 *
 *   58rem →  2 cols → cards ≥ 456px  (1600px window: 479px)
 *   88rem →  3 cols → cards ≥ 448px  (2560px window: 634px)
 *
 * So field rows never collapse back to stacked just because a column was
 * added, and controls keep ~64% of their card at every size.
 *
 * The query container and the multi-column box must be *different* elements:
 * an element cannot respond to its own container query.
 */
const CARD_FLOW_CONTAINER_CLASS = '@container';

/**
 * Column classes for the page's band flow.
 *
 * This is the fix for the defect the per-band flow below could not reach.
 * Column count was decided *inside* a band, but a band routinely holds exactly
 * one section — the comment on `cardFlowClass` says so outright — so the flow
 * resolved to `columns-1` and the page rendered a single stack of full-width
 * cards at every width. Measured before this change, on `System Config`:
 *
 *   1440px window → 814px flow  → 1 column
 *   1920px window → 1294px flow → 1 column
 *   2560px window → 1934px flow → 1 column, card capped at 1024px,
 *                                 910px of the page left blank
 *
 * The unit that has to flow is therefore the band, not the card. Each band
 * keeps its caption glued to its own cards and stays whole across a break.
 *
 * Thresholds derive from the `23rem` (368px) card width at which
 * `SettingsPagelet` puts a field's label beside its control, plus ~36px of
 * card padding and border, plus the 16px column gap. A column is only opened
 * when every resulting card still clears it:
 *
 *   52rem → 2 cols → cards ≥ 404px  (1440px window: 435px)
 *   78rem → 3 cols → cards ≥ 405px  (1920px window: 444px)
 *
 * so adding a column never collapses a field row back to stacked.
 */
function bandFlowClass(bandCount: number): string {
  return clsx(
    'columns-1 gap-4',
    bandCount >= 2 && '@[52rem]:columns-2',
    bandCount >= 3 && '@[78rem]:columns-3',
  );
}

/**
 * Column classes for a flow holding `cardCount` cards.
 *
 * Same thresholds as the band flow, and deliberately so. Each band carries its
 * own `@container`, so this resolves against the band's width rather than the
 * page's, and the two cases fall out without either flow having to know about
 * the other:
 *
 *   many bands  → each band is one column wide (~435px) → cards stack
 *   one band    → the band is the full content width    → its cards flow
 *
 * The count cap still matters: CSS multi-column fills column one before column
 * two, so a lone card in a two-column flow sits at half width with the other
 * half blank.
 */
function cardFlowClass(cardCount: number): string {
  return clsx(
    '-mb-4 gap-4 columns-1',
    // One card cannot fill a wide flow, and stretching it to 1300px only
    // pushes labels away from the controls they name. Cap the measure and let
    // the remainder read as page margin.
    cardCount <= 1 && 'max-w-[64rem]',
    cardCount >= 2 && '@[52rem]:columns-2',
    cardCount >= 3 && '@[78rem]:columns-3',
  );
}

/**
 * Renders one settings group as a card flow, backed by its own `useDirtyState`.
 * Each dirty section is saved via its dedicated per-section endpoint
 * (`POST /api/settings/{key}`) — never the batch `saveAll` endpoint.
 *
 * The block is intentionally isolated so groups can be edited independently:
 * saving one group leaves another group's unsaved edits exactly as the user
 * left them.
 *
 * It renders no save bar of its own. It publishes its dirty summary and its
 * save/discard callbacks to the page registry, and the page renders a single
 * bar for all groups. See `settingsSaveRegistry.ts` for why the summary travels
 * up while the state stays down.
 */
function GroupSaveBlock({
  group,
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
  // No `isSaving` / `saveError` here any more. Both were only ever read by this
  // block's own save bar, and that bar now lives on the page. Keeping local
  // copies would give the page two sources of truth for one visible state.
  const { publishSummary, publishIssues, registerActions } = useSettingsSaveRegistry();

  const handleFieldChange = useCallback((sectionKey: string, field: string, value: SettingValue) => {
    const nextSection = { ...(state.values[sectionKey] ?? {}), [field]: value };
    const nextGroup = { ...state.values, [sectionKey]: nextSection };
    state.replaceValues(nextGroup);

    const metaForSection = metadataItems.find((m) => m.key === sectionKey);
    if (metaForSection) {
      const errs = validateSection(metaForSection, nextSection);
      setFieldErrors((prev) => ({ ...prev, [sectionKey]: errs }));
    }

    // Section-level errors come from the server and can't be re-derived on the
    // client, so unlike field errors we clear (not recompute) them as the user
    // edits — otherwise a stale server alert lingers while they fix the value.
    setSectionErrors((prev) => {
      if (!(sectionKey in prev)) return prev;
      const next = { ...prev };
      delete next[sectionKey];
      return next;
    });
  }, [metadataItems, state]);

  const handleDiscard = useCallback(() => {
    state.reset();
    setFieldErrors({});
    setSectionErrors({});
  }, [state]);

  const handleSave = useCallback(async (): Promise<GroupSaveOutcome> => {
    // Not a "Save All" button, despite saving more than one section.
    //
    // The banned pattern is a button that posts the whole settings tree through
    // the batch `POST /api/settings` endpoint — one opaque write the user
    // cannot reason about and the server cannot partially reject. This does the
    // opposite: one `POST /api/settings/{keyName}` per section the user
    // actually edited, each independently validated, each able to fail on its
    // own, with the outcome named per section. `saveAllSettings` is never
    // called from anywhere in the frontend.
    //
    // The alternative — a save control per card — is what #1013 replaced. With
    // the density-aware band flow a single screen can show a dozen cards, and a
    // dozen Save buttons left users unsure which one covered the field they had
    // just touched.
    const labelFor = (sectionKey: string) => {
      const meta = metadataItems.find((m) => m.key === sectionKey);
      return meta?.displayName || meta?.className || sectionKey;
    };

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
      return {
        ok: false,
        failedLabels: Object.keys(allErrors).map(labelFor),
        message: 'Fix validation errors before saving.',
      };
    }

    const changedSectionKeys = state.changedKeys.map((k) => String(k));
    const failed: string[] = [];
    const saved: string[] = [];
    const perSectionErrors: Record<string, Record<string, string>> = {};
    const perSectionMessages: Record<string, string> = {};
    let firstMessage: string | undefined;

    for (const sectionKey of changedSectionKeys) {
      const meta = metadataItems.find((m) => m.key === sectionKey);
      if (!meta) continue;
      try {
        await saveSettingsValues(sectionKey, state.values[sectionKey] ?? {});
        saved.push(sectionKey);
      } catch (err) {
        failed.push(meta.displayName || meta.className);
        const extracted = extractFieldErrors(err, sectionKey);
        Object.assign(perSectionErrors, extracted.fieldErrors);
        Object.assign(perSectionMessages, extracted.sectionErrors);
        if (!firstMessage && extracted.message) firstMessage = extracted.message;
      }
    }

    if (failed.length > 0) {
      // Settle the groups that did save. Each group is its own request, so a
      // partial failure is normal — leaving the successes dirty would show the
      // user unsaved work that is already on the server, and re-POST it on the
      // next attempt. Only the groups that actually failed stay dirty.
      state.acceptKeys(saved);
      // Only errors produced by *this* attempt may remain for the sections we
      // just tried. Dropping the attempted keys first (rather than spreading
      // over `prev`) means a section that succeeded this round clears its stale
      // alert instead of carrying an error for a save that just went through.
      // Sections not part of this save are left untouched.
      setFieldErrors((prev) => {
        const next = { ...prev };
        for (const key of changedSectionKeys) delete next[key];
        return { ...next, ...perSectionErrors };
      });
      setSectionErrors((prev) => {
        const next = { ...prev };
        for (const key of changedSectionKeys) delete next[key];
        return { ...next, ...perSectionMessages };
      });
      // Keep the dirty state so the user can retry or discard. The page raises
      // the toast and renders the message — a per-group toast here would fire
      // once per group on a multi-group save.
      return { ok: false, failedLabels: failed, message: firstMessage };
    }

    // All sections saved — accept current values as the new baseline.
    state.markPristine(state.values);
    setFieldErrors({});
    setSectionErrors({});
    return { ok: true, savedLabels: changedSectionKeys.map(labelFor) };
  }, [metadataItems, state]);

  // Section display names, in the order the cards render — not in the arbitrary
  // order `changedKeys` happens to produce — so the save bar reads left-to-right
  // the same way the page does.
  const changedLabels = useMemo(() => {
    const changed = new Set(state.changedKeys.map((k) => String(k)));
    return metadataItems
      .filter((m) => changed.has(m.key))
      .map((m) => m.displayName || m.className);
  }, [metadataItems, state.changedKeys]);

  /**
   * How many individual *fields* the user edited, not how many sections contain
   * an edit. "3 changes in System Log" should mean three fields; counting
   * sections would report "1 change" for a section where three values moved.
   *
   * Uses the same structural comparison `useDirtyState` used to mark the section
   * dirty, so the count can never disagree with the bar's own visibility.
   */
  const changedFieldCount = useMemo(() => {
    let count = 0;
    for (const key of state.changedKeys) {
      const sectionKey = String(key);
      const before = (initialValues[sectionKey] ?? {}) as SectionValues;
      const after = (state.values[sectionKey] ?? {}) as SectionValues;
      for (const field of new Set([...Object.keys(before), ...Object.keys(after)])) {
        if (!isStructurallyEqual(before[field], after[field])) count += 1;
      }
    }
    return count;
  }, [initialValues, state.changedKeys, state.values]);

  // Publishing the summary is a render concern, so it goes through state and
  // reruns whenever the numbers move. The registry bails out when the published
  // value is unchanged, so a keystroke that does not alter the count costs one
  // comparison and no render.
  useEffect(() => {
    if (changedFieldCount === 0) {
      publishSummary(group, null);
      return;
    }
    publishSummary(group, {
      displayName: groupDisplayName,
      changeCount: changedFieldCount,
      labels: changedLabels,
    });
  }, [publishSummary, group, groupDisplayName, changedFieldCount, changedLabels]);

  /**
   * Issues this group's *current* values produce.
   *
   * Derived from live values rather than the loaded ones so the attention band
   * clears the moment the user fixes a field — and stays cleared after a save,
   * which matters because the page deliberately does not refetch. Server
   * section-level errors fold in here too, since a rejected save is the most
   * concrete signal there is.
   */
  const groupIssues = useMemo(
    () => deriveSettingsIssues(metadataItems, state.values, sectionErrors),
    [metadataItems, state.values, sectionErrors],
  );

  useEffect(() => {
    publishIssues(group, groupIssues);
  }, [publishIssues, group, groupIssues]);

  useEffect(() => () => publishIssues(group, []), [publishIssues, group]);

  /** Per-card issue counts, so a card can flag itself without a page prop. */
  const issueCountBySection = useMemo(
    () => countIssuesBySection(groupIssues),
    [groupIssues],
  );

  const issueSeverityBySection = useMemo(
    () => severityBySection(groupIssues),
    [groupIssues],
  );

  // Registering the callbacks is not a render concern, so it must not re-run on
  // every edit — `handleSave` closes over `state.values` and so changes identity
  // on every keystroke. A ref indirection keeps the registration itself stable
  // while still dispatching to the current closure.
  const callbacksRef = useRef({ save: handleSave, discard: handleDiscard });
  useEffect(() => {
    callbacksRef.current = { save: handleSave, discard: handleDiscard };
  }, [handleSave, handleDiscard]);

  useEffect(() => {
    registerActions(group, {
      save: () => callbacksRef.current.save(),
      discard: () => callbacksRef.current.discard(),
    });
    return () => {
      registerActions(group, null);
      publishSummary(group, null);
    };
  }, [registerActions, publishSummary, group]);

  const query = searchQuery ?? '';

  // Column count is capped by how many cards there actually are. CSS multicol
  // fills column 1 first, so a lone card in a two-column flow renders at half
  // width with the other half empty — the exact real-estate waste this layout
  // exists to remove. Bands routinely hold a single section.
  const visibleCardCount = useMemo(() => {
    if (!propertyFilter) return metadataItems.length;
    return metadataItems.filter((meta) => {
      const allowed = propertyFilter[meta.key];
      return Boolean(allowed) && meta.properties.some((p) => allowed.has(p.name));
    }).length;
  }, [metadataItems, propertyFilter]);

  return (
    <div className={CARD_FLOW_CONTAINER_CLASS}>
      <div className={cardFlowClass(visibleCardCount)} data-testid="settings-card-flow">
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
            const cardIssueCount = issueCountBySection[meta.key] ?? 0;
            const cardIsError = issueSeverityBySection[meta.key] === 'Error';

            return (
              <Card
                key={meta.key}
                className={clsx(
                  'mb-4 break-inside-avoid',
                  fullWidth && '[column-span:all]',
                  // A left rule reads as "this one" at a glance across a
                  // multi-column flow, where a badge alone gets lost.
                  cardIssueCount > 0 && 'border-l-2',
                  cardIssueCount > 0 && (cardIsError ? 'border-l-pf-error' : 'border-l-pf-warning'),
                )}
                dataAttributes={{
                  'data-section-key': meta.key,
                  'data-section-issues': cardIssueCount ? String(cardIssueCount) : undefined,
                  'data-section-severity': cardIssueCount ? (cardIsError ? 'Error' : 'Warning') : undefined,
                }}
              >
                <Card.Header className="pb-2">
                  <div className="flex items-start justify-between gap-2">
                    <h4 className="text-sm font-semibold text-pf-text-primary">
                      {query ? <HighlightedText text={cardTitle} query={query} /> : cardTitle}
                    </h4>
                    {cardIssueCount > 0 && (
                      <Badge variant={cardIsError ? 'error' : 'warning'} size="sm">
                        {cardIsError ? 'Save failed' : 'Action needed'}
                      </Badge>
                    )}
                  </div>
                  {meta.description && (
                    <p className="text-xs text-pf-text-secondary mt-0.5">
                      {query
                        ? <HighlightedText text={meta.description} query={query} />
                        : meta.description}
                    </p>
                  )}
                </Card.Header>
                <Card.Body className="pt-0">
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
    </div>
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

  // ── Page-level save aggregation ────────────────────────────────────────────
  // Dirty *values* stay inside each GroupSaveBlock so groups cannot clobber each
  // other. Only the summary comes up here, and only so one bar can describe all
  // of them. See settingsSaveRegistry.ts.
  const [dirtyByGroup, setDirtyByGroup] = useState<Record<string, GroupDirtySummary>>({});
  const [issuesByGroup, setIssuesByGroup] = useState<Record<string, SettingsIssue[]>>({});
  const groupActionsRef = useRef(new Map<string, GroupSaveActions>());
  const [isSavingAll, setIsSavingAll] = useState(false);
  const [saveAllError, setSaveAllError] = useState<string | null>(null);
  const footerSlot = useSettingsFooterSlot();

  const publishSummary = useCallback((group: string, summary: GroupDirtySummary | null) => {
    setDirtyByGroup((prev) => {
      const current = prev[group];
      if (!summary) {
        if (!current) return prev;
        const next = { ...prev };
        delete next[group];
        return next;
      }
      // Returning `prev` unchanged is what stops a publish-on-every-keystroke
      // from becoming a re-render on every keystroke.
      const same = current
        && current.changeCount === summary.changeCount
        && current.displayName === summary.displayName
        && current.labels.length === summary.labels.length
        && current.labels.every((label, i) => label === summary.labels[i]);
      return same ? prev : { ...prev, [group]: summary };
    });
  }, []);

  const registerActions = useCallback((group: string, actions: GroupSaveActions | null) => {
    if (actions) groupActionsRef.current.set(group, actions);
    else groupActionsRef.current.delete(group);
  }, []);

  // Same identity-bailout discipline as `publishSummary`: blocks republish on
  // every keystroke, and only a genuine change of the issue list may re-render
  // the page. Comparing the flattened messages is enough — two issue lists that
  // agree field-for-field and message-for-message render identically.
  const publishIssues = useCallback((group: string, issues: readonly SettingsIssue[]) => {
    setIssuesByGroup((prev) => {
      const current = prev[group];
      if (issues.length === 0) {
        if (!current) return prev;
        const next = { ...prev };
        delete next[group];
        return next;
      }
      const same = current
        && current.length === issues.length
        && current.every((issue, i) => (
          issue.sectionKey === issues[i].sectionKey
          && issue.field === issues[i].field
          && issue.message === issues[i].message
          && issue.severity === issues[i].severity
        ));
      return same ? prev : { ...prev, [group]: [...issues] };
    });
  }, []);

  const saveRegistry = useMemo(
    () => ({ publishSummary, publishIssues, registerActions }),
    [publishSummary, publishIssues, registerActions],
  );

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

  /**
   * Which bands were flagged *at load time*.
   *
   * Ordering deliberately uses the loaded values, not the live ones. If the sort
   * key tracked live edits, the band a user is actively fixing would jump out
   * from under their cursor the instant the field validated — the page
   * rearranging itself mid-keystroke. Freezing the order per load means a
   * flagged band rises to the top once and stays put until the next load, while
   * the attention band's *contents* stay live.
   */
  const attentionGroups = useMemo(() => {
    const flagged = new Set<string>();
    for (const issue of deriveSettingsIssues(metadata, values)) {
      const meta = metadata.find((m) => m.key === issue.sectionKey);
      if (meta) flagged.add(meta.group || 'Other');
    }
    return flagged;
  }, [metadata, values]);

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
        // Bands needing attention lead. With nothing flagged this term is
        // constant and the declared backend order below is untouched, so a
        // healthy page renders exactly as it did before attention existed.
        const flaggedA = attentionGroups.has(a) ? 0 : 1;
        const flaggedB = attentionGroups.has(b) ? 0 : 1;
        if (flaggedA !== flaggedB) return flaggedA - flaggedB;

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
  }, [allowedGroupSet, attentionGroups, groupMetadata, isSlicerAvailable, metadata]);

  const getGroupDisplayName = useCallback((groupKey: string): string => {
    const group = groupMetadata.find((g) => g.key === groupKey);
    return group?.displayName || groupKey;
  }, [groupMetadata]);

  // ── Save aggregation, derived ──────────────────────────────────────────────
  // Ordered by `sortedGroups`, not by which group the user happened to touch
  // first. The bar should read in the same order the page does, and a save
  // should walk the page top to bottom rather than in edit order.
  const dirtyGroupKeys = useMemo(
    () => sortedGroups.filter((group) => group in dirtyByGroup),
    [sortedGroups, dirtyByGroup],
  );

  const totalChangeCount = useMemo(
    () => dirtyGroupKeys.reduce((sum, key) => sum + dirtyByGroup[key].changeCount, 0),
    [dirtyByGroup, dirtyGroupKeys],
  );

  const dirtySectionLabels = useMemo(
    () => dirtyGroupKeys.flatMap((key) => dirtyByGroup[key].labels),
    [dirtyByGroup, dirtyGroupKeys],
  );

  const dirtySummary = useMemo(
    () => formatDirtySummary(totalChangeCount, dirtySectionLabels),
    [totalChangeCount, dirtySectionLabels],
  );

  const handleSaveAll = useCallback(async () => {
    if (dirtyGroupKeys.length === 0) return;

    setIsSavingAll(true);
    setSaveAllError(null);

    const saved: string[] = [];
    const failed: string[] = [];
    let firstMessage: string | undefined;

    // Sequential, not parallel. Each section is a separate write and some of
    // them restart a subsystem — dispatching all of them at once turns an
    // ordered set of changes into a race.
    for (const group of dirtyGroupKeys) {
      const actions = groupActionsRef.current.get(group);
      if (!actions) continue;
      const outcome = await actions.save();
      if (outcome.ok) {
        saved.push(...outcome.savedLabels);
      } else {
        failed.push(...outcome.failedLabels);
        if (!firstMessage && outcome.message) firstMessage = outcome.message;
      }
    }

    setIsSavingAll(false);

    const report = formatSaveOutcome(saved, failed);
    if (failed.length > 0) {
      // Groups that succeeded retract their summaries; the failed group keeps
      // its own, so the bar narrows itself to exactly what is left to save.
      setSaveAllError(firstMessage ?? report);
      adminToast.error(report);
      return;
    }
    adminToast.success(report);
  }, [dirtyGroupKeys]);

  const handleDiscardAll = useCallback(() => {
    setSaveAllError(null);
    for (const group of dirtyGroupKeys) {
      groupActionsRef.current.get(group)?.discard();
    }
  }, [dirtyGroupKeys]);

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

  // Bands that actually render, which is what the column flow has to size for.
  // A group whose every section is filtered out returns null below, so counting
  // `sortedGroups` would open columns for bands that never appear and leave the
  // trailing ones empty.
  const visibleBandCount = useMemo(
    () =>
      sortedGroups.filter((group) =>
        (metadataByGroup[group] ?? []).some((m) => (visibleByKey[m.key]?.size ?? 0) > 0),
      ).length,
    [sortedGroups, metadataByGroup, visibleByKey],
  );

  /**
   * Every live issue on the page, ordered the way the bands render so the banner
   * reads top-to-bottom in the same order as the page beneath it.
   *
   * Only issues in bands that are currently *visible* count. A field hidden by
   * the Essential filter or an active search is not something the user can act
   * on from here, and a "Fix" button that scrolls to nothing is worse than no
   * button at all.
   */
  const attentionIssues = useMemo(() => {
    const ordered: SettingsIssue[] = [];
    for (const group of sortedGroups) {
      for (const issue of issuesByGroup[group] ?? []) {
        const visible = visibleByKey[issue.sectionKey];
        if (!visible || visible.size === 0) continue;
        if (issue.field && !visible.has(issue.field)) continue;
        ordered.push(issue);
      }
    }
    return ordered;
  }, [sortedGroups, issuesByGroup, visibleByKey]);

  const issueCountBySection = useMemo(
    () => countIssuesBySection(attentionIssues),
    [attentionIssues],
  );

  // Red only when something actually failed; unfinished config stays amber.
  const attentionVariant = useMemo(
    () => (attentionIssues.some((issue) => issue.severity === 'Error') ? 'error' : 'warning'),
    [attentionIssues],
  );

  const issueCountByGroup = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const group of sortedGroups) {
      const total = (metadataByGroup[group] ?? [])
        .reduce((sum, meta) => sum + (issueCountBySection[meta.key] ?? 0), 0);
      if (total > 0) counts[group] = total;
    }
    return counts;
  }, [sortedGroups, metadataByGroup, issueCountBySection]);

  /** Same red-means-failed rule as the attention band, scoped to one group. */
  const errorGroups = useMemo(() => {
    const worst = severityBySection(attentionIssues);
    const flagged = new Set<string>();
    for (const group of sortedGroups) {
      const hasError = (metadataByGroup[group] ?? []).some((meta) => worst[meta.key] === 'Error');
      if (hasError) flagged.add(group);
    }
    return flagged;
  }, [sortedGroups, metadataByGroup, attentionIssues]);

  const toggleHelperText = useMemo(() => {
    if (trimmedQuery) {
      if (visibleSettingsCount === 0) return 'No matching settings';
      return `${visibleSettingsCount} match${visibleSettingsCount === 1 ? '' : 'es'} in ${matchingSectionCount} section${matchingSectionCount === 1 ? '' : 's'}`;
    }
    if (totalSettingsCount === 0) return undefined;
    // "Showing 7 of 26 settings" sat on the page permanently and stated a fact
    // the user could not act on. The fact worth surfacing is the *absence*:
    // Basic mode hides fields, and a user hunting for one needs to know that is
    // why it is not here. When nothing is hidden there is nothing to say.
    const hiddenCount = totalSettingsCount - visibleSettingsCount;
    if (hiddenCount <= 0) return undefined;
    return `${hiddenCount} advanced field${hiddenCount === 1 ? '' : 's'} hidden`;
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

  // One bar for the whole page. Every group saves through its own endpoint
  // underneath, but the user sees a single place to commit and a message that
  // names what is about to be written.
  const saveBar = (
    <AdminSaveBar
      isDirty={dirtyGroupKeys.length > 0}
      summary={dirtySummary.text}
      summaryTitle={dirtySummary.title}
      onDiscard={handleDiscardAll}
      onSave={handleSaveAll}
      isSaving={isSavingAll}
      error={saveAllError}
    />
  );

  return (
    <SettingsSaveRegistryContext value={saveRegistry}>
      <div className="space-y-6" data-tour="settings-content">
        <div className="flex items-center justify-between gap-4">
          <p className="text-sm text-pf-text-secondary">{introText}</p>
          <HelpButton onClick={startTour} />
        </div>

        {/* Some tabs (e.g. Farm) render only `afterContent` and have no metadata-driven
            settings at all. Showing a filter toggle and a search box there would give the
            user two controls that visibly do nothing. */}
        {totalSettingsCount > 0 && (
          <>
            {/* The mode is a global, persisted preference, so it belongs with the page's
                other global actions rather than in a box above the content. The filter
                below is ephemeral and page-local, so it stays with what it filters. */}
            <SettingsHeaderPortal>
              <SettingsModeToggle mode={mode} onModeChange={setMode} />
            </SettingsHeaderPortal>

            <div
              className="flex flex-wrap items-center gap-3"
              data-testid="settings-mode-controls"
            >
              <div className="flex flex-1 items-center gap-2 min-w-[200px] max-w-md">
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
              <p className="text-xs text-pf-text-secondary" aria-live="polite" role="status">
                {toggleHelperText}
                {searchActive && (
                  <span className="ml-2 text-pf-text-tertiary">
                    This filter covers every field on this page, including advanced ones.
                  </span>
                )}
              </p>
            </div>
          </>
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

        <div className={CARD_FLOW_CONTAINER_CLASS}>
          {attentionIssues.length > 0 && (
            <AdminSection
              caption="Needs attention"
              captionId="settings-attention"
              count={attentionIssues.length}
              countVariant={attentionVariant}
              headingLevel={3}
              className="mb-6"
            >
              <ul
                className="flex flex-col gap-3"
                data-testid="settings-attention-list"
              >
                {attentionIssues.map((issue) => (
                  <AttentionRow
                    key={`${issue.sectionKey}.${issue.field}`}
                    severity={issue.severity}
                    showSeverity={false}
                    title={issue.title}
                    detail={issue.detail}
                    action={
                      issue.field
                        ? {
                          label: 'Fix',
                          onClick: () => focusSettingProperty(issue.sectionKey, issue.field),
                        }
                        : undefined
                    }
                    dataAttributes={{
                      'data-testid': 'settings-attention-item',
                      'data-attention-section': issue.sectionKey,
                      'data-attention-field': issue.field || undefined,
                    }}
                  />
                ))}
              </ul>
            </AdminSection>
          )}

          <div className={bandFlowClass(visibleBandCount)} data-testid="settings-band-flow">
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
          const groupIssueCount = issueCountByGroup[group] ?? 0;

          return (
            <AdminSection
              key={group}
              caption={
                searchActive ? (
                  <HighlightedText text={groupDisplay} query={trimmedQuery} />
                ) : (
                  groupDisplay
                )
              }
              captionId={`group-${group}`}
              headingLevel={3}
              gap="loose"
              className="mb-6 break-inside-avoid"
              captionAside={
                groupIssueCount > 0 ? (
                  <Badge variant={errorGroups.has(group) ? 'error' : 'warning'} size="sm">
                    {groupIssueCount} {groupIssueCount === 1 ? 'issue' : 'issues'}
                  </Badge>
                ) : undefined
              }
            >
              <GroupSaveBlock
                key={group}
                group={group}
                groupDisplayName={groupDisplay}
                metadataItems={groupMeta}
                initialValues={initialGroupValues}
                propertyFilter={visibleByKey}
                suppressExtensions={searchActive}
                searchQuery={trimmedQuery}
              />
            </AdminSection>
          );
            })}
          </div>
        </div>

        {afterContent}

        {/* Fallback for standalone mounts. `SettingsPage` is normally drawn by
            `SettingsShell`, which supplies a footer slot below its scrollport;
            rendered on its own (tests, or any future embed without the shell)
            there is nothing to portal into, and the bar has to stay in flow or
            it would vanish entirely. */}
        {!footerSlot ? saveBar : null}
      </div>

      {footerSlot ? createPortal(saveBar, footerSlot) : null}
    </SettingsSaveRegistryContext>
  );
}

export default SettingsPage;
