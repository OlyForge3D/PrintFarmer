import type { ComponentType } from 'react';
import {
  AccountIcon,
  DatabaseIcon,
  GearIcon,
  KeyIcon,
  LayersIcon,
  NetworkIcon,
  ServerIcon,
  ShieldIcon,
  UsersIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import type { SettingMetadata } from '@/common/components/SettingsPagelet';
import type {
  AdminDestination,
  AdminDestinationGroup,
} from '@/features/admin/registry/adminDestinations';
import { ADMIN_DESTINATION_GROUPS } from '@/features/admin/registry/adminDestinations';
import type { SettingGroupMetadata } from '@/services/settingsApi';
import {
  DEFAULT_SCOPE,
  SETTINGS_CATEGORIES,
  getDefaultCategoryForScope,
  getDefaultSubPage,
  getSettingsCategory,
  getSettingsScope,
  isSettingsScope,
  type SettingsScopeId,
} from '@/features/settings/types';

export type SettingsCategoryIcon = ComponentType<{ className?: string; ariaLabel?: string }>;

/**
 * Kind discriminator for palette items. Section headers, sort priority, and
 * execution behaviour all key off this — a `settings-nav` item navigates to a
 * settings sub-page, a `setting` navigates AND focuses a specific field, an
 * `action` invokes a callback (with optional confirmation), and a `destination`
 * points at any admin route.
 */
export type SettingsCommandItemKind = 'destination' | 'settings-nav' | 'setting' | 'action';

/**
 * Palette item shape. Backwards-compatible with the pre-#938 shape: legacy
 * consumers still emit `settings-nav` items with `scopeId` / `categoryId` /
 * `subPageId`, and the existing `CommandPalette.test.tsx` continues to pass
 * because the new fields are optional.
 */
export interface SettingsCommandItem {
  id: string;
  /** Result grouping. Defaults to `'settings-nav'` for legacy callers. */
  kind?: SettingsCommandItemKind;
  scopeId: SettingsScopeId;
  categoryId: string;
  subPageId?: string;
  label: string;
  description: string;
  breadcrumb: string;
  keywords: string[];
  /** Icon override. Falls back to the category icon when omitted. */
  icon?: SettingsCategoryIcon;
  /** Direct navigation target for `destination` and `setting` items. */
  href?: string;
  /**
   * Handler invoked when an `action` item is selected. The provider passes
   * `close` so long-running actions (e.g. sign-out) can dismiss the palette
   * before their promise resolves.
   */
  onExecute?: (helpers: { close: () => void }) => void | Promise<void>;
  /**
   * Confirmation prompt used by destructive `action` items. When present the
   * provider prompts via the in-app `ConfirmationModal` before invoking
   * `onExecute`.
   */
  confirmMessage?: string;
}

export interface ResolvedSettingsNavigationTarget {
  scopeId: SettingsScopeId;
  categoryId: string;
  subPageId?: string;
}

/**
 * Fuzzy-match ranking shared between the modal {@link CommandPalette} (#938)
 * and the persistent workspace search results panel (#2505). Both surfaces
 * search the same permission-filtered item shape ({@link SettingsCommandItem})
 * and must rank/group results identically — kept here as the single
 * implementation rather than duplicated per surface.
 */
export interface FuzzyResult {
  item: SettingsCommandItem;
  score: number;
  labelMatches: number[];
  breadcrumbMatches: number[];
}

export interface FuzzyGroup {
  kind: SettingsCommandItemKind;
  label: string;
  results: FuzzyResult[];
}

/**
 * Display order and human-readable label for each result section. Kinds
 * missing from this list still render, but only at the tail of the results in
 * insertion order — the array is authoritative for the visible sections.
 */
export const KIND_SECTION_ORDER: { kind: SettingsCommandItemKind; label: string }[] = [
  { kind: 'destination', label: 'Places' },
  { kind: 'settings-nav', label: 'Settings sections' },
  { kind: 'setting', label: 'Individual settings' },
  { kind: 'action', label: 'Actions' },
];

export function getItemKind(item: SettingsCommandItem): SettingsCommandItemKind {
  return item.kind ?? 'settings-nav';
}

export function normalizeSearchQuery(value: string): string {
  return value.trim().toLowerCase();
}

/**
 * Subsequence fuzzy match: every character of `query`, in order, must appear
 * somewhere in `text` (case-insensitive). Returns the matched character
 * indices (for highlighting) or `null` when no such subsequence exists.
 */
export function getFuzzyMatchIndices(text: string, query: string): number[] | null {
  if (!query) {
    return [];
  }

  const normalizedText = text.toLowerCase();
  const matches: number[] = [];
  let searchIndex = 0;

  for (const character of query) {
    const nextMatch = normalizedText.indexOf(character, searchIndex);
    if (nextMatch === -1) {
      return null;
    }

    matches.push(nextMatch);
    searchIndex = nextMatch + 1;
  }

  return matches;
}

function scoreFuzzyMatches(matches: number[]): number {
  if (matches.length === 0) {
    return 0;
  }

  const spread = matches[matches.length - 1] - matches[0];
  let contiguousBonus = 0;

  for (let index = 1; index < matches.length; index += 1) {
    if (matches[index] === matches[index - 1] + 1) {
      contiguousBonus += 4;
    }
  }

  return spread - contiguousBonus;
}

/**
 * Score a single item against an already-normalized query. Lower scores rank
 * first. Returns `null` when the item does not match at all (label,
 * breadcrumb, and keywords all miss).
 */
export function getFuzzyResult(item: SettingsCommandItem, normalizedQuery: string): FuzzyResult | null {
  if (!normalizedQuery) {
    return {
      item,
      score: 0,
      labelMatches: [],
      breadcrumbMatches: [],
    };
  }

  const labelMatches = getFuzzyMatchIndices(item.label, normalizedQuery);
  const breadcrumbMatches = getFuzzyMatchIndices(item.breadcrumb, normalizedQuery);
  const keywordExactMatch = item.keywords.some((keyword) => keyword.includes(normalizedQuery));

  if (!labelMatches && !breadcrumbMatches && !keywordExactMatch) {
    return null;
  }

  let score = 300;

  if (labelMatches) {
    score -= 180;
    score += scoreFuzzyMatches(labelMatches);
    if (item.label.toLowerCase().includes(normalizedQuery)) {
      score -= 24;
    }
    if (item.label.toLowerCase().startsWith(normalizedQuery)) {
      score -= 30;
    }
  }

  if (breadcrumbMatches) {
    score -= 70;
    score += scoreFuzzyMatches(breadcrumbMatches);
  }

  if (keywordExactMatch) {
    score -= 28;
  }

  if (item.subPageId) {
    score -= 6;
  }

  return {
    item,
    score,
    labelMatches: labelMatches ?? [],
    breadcrumbMatches: breadcrumbMatches ?? [],
  };
}

/**
 * Rank every item against a raw (not-yet-normalized) query, dropping
 * non-matches and sorting best-first. Ties break on breadcrumb so results are
 * stable across renders. `maxVisible` bounds the result count (the modal
 * palette and the workspace search panel may want different limits).
 */
export function rankSettingsCommandItems(
  items: readonly SettingsCommandItem[],
  query: string,
  options?: { maxVisible?: number },
): FuzzyResult[] {
  const normalizedQuery = normalizeSearchQuery(query);
  const results = items
    .map((item) => getFuzzyResult(item, normalizedQuery))
    .filter((result): result is FuzzyResult => result !== null)
    .sort((left, right) => left.score - right.score || left.item.breadcrumb.localeCompare(right.item.breadcrumb));

  return options?.maxVisible ? results.slice(0, options.maxVisible) : results;
}

/**
 * Bucket ranked results by {@link SettingsCommandItemKind}, in
 * {@link KIND_SECTION_ORDER} order, dropping empty sections. Any kind absent
 * from that order still renders, at the tail, labelled by its raw kind.
 */
export function groupRankedResults(results: readonly FuzzyResult[]): FuzzyGroup[] {
  if (results.length === 0) {
    return [];
  }
  const byKind = new Map<SettingsCommandItemKind, FuzzyResult[]>();
  for (const result of results) {
    const kind = getItemKind(result.item);
    const bucket = byKind.get(kind) ?? [];
    bucket.push(result);
    byKind.set(kind, bucket);
  }

  const groups: FuzzyGroup[] = [];
  for (const { kind, label } of KIND_SECTION_ORDER) {
    const bucket = byKind.get(kind);
    if (bucket && bucket.length > 0) {
      groups.push({ kind, label, results: bucket });
      byKind.delete(kind);
    }
  }
  for (const [kind, bucket] of byKind.entries()) {
    groups.push({ kind, label: kind, results: bucket });
  }
  return groups;
}

export const SETTINGS_CATEGORY_ICONS: Record<string, SettingsCategoryIcon> = {
  profile: AccountIcon,
  general: GearIcon,
  slicing: LayersIcon,
  hardware: WrenchIcon,
  integrations: NetworkIcon,
  quotas: DatabaseIcon,
  operations: ServerIcon,
  users: UsersIcon,
  data: DatabaseIcon,
};

export const SETTINGS_SUBPAGE_ICONS: Record<string, SettingsCategoryIcon> = {
  preferences: GearIcon,
  'api-keys': KeyIcon,
  notifications: NetworkIcon,
  passkeys: ShieldIcon,
};

function createScopeBreadcrumb(scopeId: SettingsScopeId): string {
  return `Settings / ${getSettingsScope(scopeId)?.label ?? 'Settings'}`;
}

export function getSettingsCategoryIcon(categoryId: string): SettingsCategoryIcon {
  return SETTINGS_CATEGORY_ICONS[categoryId] ?? GearIcon;
}

const TAB_SHORTHANDS: Record<string, { categoryId: string; subPageId?: string }> = {
  printers: { categoryId: 'hardware', subPageId: 'printer-groups' },
  farm: { categoryId: 'general', subPageId: 'farm' },
  system: { categoryId: 'general', subPageId: 'system' },
  automation: { categoryId: 'general', subPageId: 'automation' },
};

export function resolveSettingsNavigationTarget(
  categoryId?: string | null,
  subPageId?: string | null,
  scopeId?: string | null,
): ResolvedSettingsNavigationTarget {
  const scopedFallback = isSettingsScope(scopeId) ? scopeId : DEFAULT_SCOPE;

  let targetCat = categoryId;
  let targetSub = subPageId;

  if (targetCat && TAB_SHORTHANDS[targetCat]) {
    const shorthand = TAB_SHORTHANDS[targetCat];
    targetCat = shorthand.categoryId;
    targetSub = targetSub ?? shorthand.subPageId;
  }

  if (targetCat) {
    const directCategory = getSettingsCategory(targetCat);
    if (directCategory) {
      const resolvedSubPageId = targetSub && directCategory.subPages.some((subPage) => subPage.id === targetSub)
        ? targetSub
        : getDefaultSubPage(directCategory.id) || undefined;

      return {
        scopeId: directCategory.scopeId,
        categoryId: directCategory.id,
        subPageId: resolvedSubPageId,
      };
    }
  }

  const fallbackCategoryId = getDefaultCategoryForScope(scopedFallback);
  const fallbackSubPageId = getDefaultSubPage(fallbackCategoryId) || undefined;

  return {
    scopeId: scopedFallback,
    categoryId: fallbackCategoryId,
    subPageId: fallbackSubPageId,
  };
}

export function buildSettingsCommandItems(): SettingsCommandItem[] {
  return SETTINGS_CATEGORIES.flatMap((category) => {
    const scopeBreadcrumb = createScopeBreadcrumb(category.scopeId);
    const categoryItem: SettingsCommandItem = {
      id: `${category.scopeId}.${category.id}`,
      kind: 'settings-nav',
      scopeId: category.scopeId,
      categoryId: category.id,
      label: category.label,
      description: category.description,
      breadcrumb: `${scopeBreadcrumb} / ${category.label}`,
      keywords: [
        ...category.keywords,
        category.label.toLowerCase(),
        category.scopeId,
        getSettingsScope(category.scopeId)?.label.toLowerCase() ?? category.scopeId,
      ],
    };

    const subPageItems = category.subPages.map<SettingsCommandItem>((subPage) => ({
      id: `${category.scopeId}.${category.id}.${subPage.id}`,
      kind: 'settings-nav',
      scopeId: category.scopeId,
      categoryId: category.id,
      subPageId: subPage.id,
      label: subPage.label,
      description: subPage.description,
      breadcrumb: `${scopeBreadcrumb} / ${category.label} / ${subPage.label}`,
      keywords: [
        ...category.keywords,
        ...subPage.keywords,
        category.label.toLowerCase(),
        subPage.label.toLowerCase(),
        category.scopeId,
      ],
    }));

    return category.subPages.length > 0 ? [categoryItem, ...subPageItems] : [categoryItem];
  });
}

/**
 * Which settings sub-page renders the properties in a given metadata `group`.
 * Kept in sync by hand with `SUB_PAGE_CONTENT` in `SettingsShell.tsx` — every
 * group referenced from an `allowedGroups` prop there must map back here, or
 * palette navigation for that group would drop the user on the wrong sub-page.
 *
 * Groups that live inside a mixed sub-page (e.g. `Networking` under
 * `general.system`) all point at the same destination; the shell renders them
 * side-by-side inside a single page.
 */
export const SETTINGS_GROUP_TO_LOCATION: Record<
  string,
  { scopeId: SettingsScopeId; categoryId: string; subPageId: string }
> = {
  General: { scopeId: 'system', categoryId: 'general', subPageId: 'farm' },
  System: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Networking: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Catalog: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Files: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Printers: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Operations: { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  Monitoring: { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  Maintenance: { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  // `HistorySeedingBackgroundService` declares Group = "Job Queue". Without an
  // entry here the palette silently skips it (`if (!location) continue`), and
  // without a matching `allowedGroups` entry in the automation sub-page it
  // renders nowhere — leaving the section unreachable by any route.
  'Job Queue': { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  Integrations: { scopeId: 'system', categoryId: 'integrations', subPageId: 'connections' },
  Slicing: { scopeId: 'system', categoryId: 'slicing', subPageId: 'defaults' },
};

/**
 * Personal settings and farm configuration have separate canonical routes.
 */
export function buildSettingsPath(
  target: { scopeId: SettingsScopeId; categoryId: string; subPageId?: string; field?: string },
): string {
  const basePath = target.scopeId === 'system'
      ? '/admin/settings'
      : '/settings';
  const params = new URLSearchParams();
  params.set('scope', target.scopeId);
  params.set('tab', target.categoryId);
  if (target.subPageId) {
    params.set('sub', target.subPageId);
  }
  if (target.field) {
    params.set('field', target.field);
  }
  return `${basePath}?${params.toString()}`;
}

/**
 * Re-append the current workspace search text onto a same-shell destination
 * href built by {@link buildSettingsPath}/{@link buildSettingCommandItems}/
 * {@link buildAdminDestinationCommandItems}, so explicit workspace-search
 * result selection "retains q within `/admin/settings`" (#2505) instead of
 * dropping the in-progress search the moment the user lands. Only same-shell
 * targets (`/admin/settings`, `/settings`) get `q` appended — an admin
 * destination that lands somewhere else entirely (a standalone operational
 * page) must not leak a settings search param onto an unrelated surface.
 */
export function withRetainedQuery(href: string, query: string): string {
  const [path, existingQuery] = href.split('?');
  if (!query || (path !== '/admin/settings' && path !== '/settings')) {
    return href;
  }
  const params = new URLSearchParams(existingQuery ?? '');
  params.set('q', query);
  return `${path}?${params.toString()}`;
}

const ADMIN_GROUP_LABEL_BY_ID = new Map<AdminDestinationGroup, string>(
  ADMIN_DESTINATION_GROUPS.map((group) => [group.id, group.label] as const),
);

/**
 * Build palette items from the admin destination registry (#934). These become
 * the "Places" section of the palette and are the single source of truth for
 * admin-surface navigation — the pre-existing `buildSettingsCommandItems`
 * output is intentionally scoped to `user` in the provider to avoid emitting
 * both a `Places` and a `Settings section` row that point at the same URL.
 */
export function buildAdminDestinationCommandItems(
  destinations: readonly AdminDestination[],
): SettingsCommandItem[] {
  return destinations.map<SettingsCommandItem>((destination) => {
    const groupLabel = ADMIN_GROUP_LABEL_BY_ID.get(destination.group) ?? destination.group;
    return {
      id: `dest.${destination.id}`,
      kind: 'destination',
      // `scopeId` is only meaningful for settings-nav items; pick a safe default
      // so downstream sorters that read the field still work.
      scopeId: 'system',
      categoryId: destination.group,
      label: destination.label,
      description: destination.description,
      breadcrumb: `Admin / ${groupLabel}`,
      keywords: [
        ...(destination.keywords ?? []),
        destination.label.toLowerCase(),
        groupLabel.toLowerCase(),
        'admin',
      ],
      icon: destination.icon,
      href: destination.path,
    };
  });
}

/**
 * Build palette items for individual settings — one per property, keyed on
 * `sectionKey.propertyName`. Requires the section metadata list and the
 * ordered group list so labels and breadcrumbs stay in sync with the sidebar.
 *
 * Only properties whose owning section has a `group` mapped in
 * {@link SETTINGS_GROUP_TO_LOCATION} become palette items. Anything else is
 * silently skipped — those properties either render inside a page the palette
 * cannot deep-link to yet, or are model-generated leftovers we do not want to
 * surface as a "jump to" target.
 */
export function buildSettingCommandItems(
  metadata: readonly SettingMetadata[] | undefined,
  groups: readonly SettingGroupMetadata[] | undefined,
): SettingsCommandItem[] {
  if (!metadata || metadata.length === 0) {
    return [];
  }

  const groupDisplayNameByKey = new Map<string, string>(
    (groups ?? []).map((group) => [group.key, group.displayName || group.key] as const),
  );

  const items: SettingsCommandItem[] = [];

  for (const section of metadata) {
    const groupKey = section.group || 'Other';
    const location = SETTINGS_GROUP_TO_LOCATION[groupKey];
    if (!location) {
      continue;
    }

    const sectionLabel = section.displayName || section.className;
    const groupLabel = groupDisplayNameByKey.get(groupKey) ?? groupKey;

    for (const property of section.properties) {
      const displayName = property.display?.name || property.name;
      const description = property.display?.description
        || section.description
        || `Setting inside ${sectionLabel}.`;

      items.push({
        id: `setting.${section.key}.${property.name}`,
        kind: 'setting',
        scopeId: location.scopeId,
        categoryId: location.categoryId,
        subPageId: location.subPageId,
        label: displayName,
        description,
        breadcrumb: `Admin / ${groupLabel} / ${sectionLabel}`,
        keywords: [
          displayName.toLowerCase(),
          property.name.toLowerCase(),
          sectionLabel.toLowerCase(),
          groupLabel.toLowerCase(),
          'setting',
        ],
        href: buildSettingsPath({ ...location, field: `${section.key}.${property.name}` }),
      });
    }
  }

  return items;
}
