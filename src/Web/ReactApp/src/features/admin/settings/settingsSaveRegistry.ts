import { createContext, useContext } from 'react';
import type { SettingsIssue } from './settingsAttention';

/**
 * Result of asking one settings group to persist its dirty sections.
 *
 * Deliberately not a bare boolean. When four groups save in sequence and one
 * fails, "something went wrong" is useless — the user needs to know *which*
 * section still holds unsaved edits, and the bar needs enough detail to say so
 * without re-deriving it from group state the page cannot see.
 */
export type GroupSaveOutcome =
  | { ok: true; savedLabels: string[] }
  // A group holds several sections and each is its own request, so a failure is
  // rarely total: `savedLabels` names the ones that did land, or the bar reports
  // only what broke and the user never learns the rest is already persisted.
  | { ok: false; savedLabels?: string[]; failedLabels: string[]; message?: string };

/** What a group publishes for display in the page-level save bar. */
export interface GroupDirtySummary {
  /** Band name, e.g. "System". Used for grouping, not currently displayed. */
  displayName: string;
  /** Number of individual *fields* the user has edited across this group. */
  changeCount: number;
  /** Display names of the changed sections (cards), in render order. */
  labels: string[];
}

/** What the page-level bar calls when the user clicks Save or Discard. */
export interface GroupSaveActions {
  save: () => Promise<GroupSaveOutcome>;
  discard: () => void;
}

export interface SettingsSaveRegistry {
  /**
   * Publish (or with `null`, retract) a group's dirty summary. Drives what the
   * bar *shows*, so it lives in React state.
   */
  publishSummary: (group: string, summary: GroupDirtySummary | null) => void;
  /**
   * Publish (or with an empty array, clear) the validation issues a group's
   * *live* values currently produce. Drives the page's "needs attention" band.
   *
   * This travels the same summary-up path and for the same reason: the page owns
   * the band, but only the group knows its edited values. Publishing keeps the
   * band honest while the user types and after a save, without hoisting the
   * values themselves — which would undo the isolation the blocks exist for.
   */
  publishIssues: (group: string, issues: readonly SettingsIssue[]) => void;
  /**
   * Register (or with `null`, unregister) a group's save/discard callbacks.
   * Drives what the bar *does*. Held in a ref, because a change of callback
   * identity must never cause a render — the bar's appearance does not depend
   * on it.
   */
  registerActions: (group: string, actions: GroupSaveActions | null) => void;
}

/**
 * Lets many independently-dirty settings groups feed one page-level save bar.
 *
 * ## Why a registry and not lifted state
 *
 * Each group owns a `useDirtyState` so groups cannot clobber each other: saving
 * Networking must leave an unsaved edit in Storage exactly as the user left it.
 * That isolation is the whole reason the per-group blocks exist, and it is why
 * the values cannot simply be hoisted into the page.
 *
 * But *presentation* has the opposite requirement. Four blocks meant four save
 * bars, three of which read "No unsaved changes", and none of which told the
 * user what its own Save button would actually write. One bar needs a view
 * across all groups.
 *
 * So the split is: state stays down, summary comes up. Groups register; the
 * page aggregates and renders a single bar; clicking Save dispatches back into
 * each dirty group's own save path, unchanged.
 *
 * ## Two channels, on purpose
 *
 * `publishSummary` is state (it changes what is rendered). `registerActions` is
 * a ref (it changes only what a click invokes). Routing callbacks through state
 * would re-render the page on every keystroke in any group, because a group's
 * save closure changes identity whenever its values do.
 */
export const SettingsSaveRegistryContext = createContext<SettingsSaveRegistry | null>(null);

export function useSettingsSaveRegistry(): SettingsSaveRegistry {
  const registry = useContext(SettingsSaveRegistryContext);
  if (!registry) {
    throw new Error(
      'useSettingsSaveRegistry must be used inside a SettingsSaveRegistryContext provider. '
      + 'Settings group blocks rely on it to reach the page-level save bar.',
    );
  }
  return registry;
}

const MAX_ENUMERATED_SECTIONS = 2;

export interface DirtySummaryText {
  text: string;
  /** Full section list, for a `title` tooltip when `text` had to elide it. */
  title?: string;
}

/**
 * Builds the save bar's message. Names the affected sections while naming them
 * still fits; past that, counts them and puts the list in a tooltip.
 *
 * "3 unsaved changes" tells the user nothing they didn't already know. What
 * they need before pressing Save is *what* is about to be written.
 */
export function formatDirtySummary(changeCount: number, labels: string[]): DirtySummaryText {
  const noun = changeCount === 1 ? 'change' : 'changes';
  if (labels.length === 0) {
    return { text: `${changeCount} unsaved ${noun}` };
  }
  if (labels.length === 1) {
    return { text: `${changeCount} ${noun} in ${labels[0]}` };
  }
  if (labels.length <= MAX_ENUMERATED_SECTIONS) {
    return { text: `${changeCount} ${noun} in ${labels[0]} and ${labels[1]}` };
  }
  return {
    text: `${changeCount} ${noun} in ${labels.length} sections`,
    title: labels.join(', '),
  };
}

/**
 * Builds the message for a completed save round.
 *
 * A partial failure is the case worth getting right: some sections were written
 * and some were not, and collapsing that into "save failed" would leave the
 * user unsure whether to retry everything. Name both halves.
 */
export function formatSaveOutcome(savedLabels: string[], failedLabels: string[]): string {
  const list = (labels: string[]) => (
    labels.length <= MAX_ENUMERATED_SECTIONS + 1
      ? labels.join(', ')
      : `${labels.length} sections`
  );
  if (failedLabels.length === 0) {
    return `Saved ${list(savedLabels)}`;
  }
  if (savedLabels.length === 0) {
    return `Failed to save ${list(failedLabels)}`;
  }
  return `Saved ${list(savedLabels)}. Failed to save ${list(failedLabels)}`;
}
