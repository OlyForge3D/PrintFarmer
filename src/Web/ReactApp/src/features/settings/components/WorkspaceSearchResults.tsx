/**
 * Persistent, always-visible workspace search for `SettingsShell` (#2505).
 *
 * This is deliberately a *different* surface from the modal
 * {@link CommandPalette} (#938): the palette is a focus-trapped dialog you
 * open and close, while this is inline navigation chrome that lives in the
 * shell header at all times. Both consume the same permission-filtered
 * {@link useSettingsSearchIndex} so results never drift apart, but this one
 * owns its own input, debounce, and inline listbox rather than a modal.
 *
 * Two invariants the issue calls out drive the design here:
 *  - Typing must never itself select a leaf, remount the dirty editor, or add
 *    a history entry per keystroke — so results are shown as a transient
 *    dropdown ("chrome"), and the debounced `q` sync uses `replace: true`
 *    (wired by the parent's `onQueryCommit`), never `navigate`/push.
 *  - Explicit selection (click or Enter) is the only thing that navigates,
 *    and it goes through the parent's `onSelect`, which applies the same
 *    draft-safety guard as every other in-shell navigation.
 */
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { ArrowRightIcon, CloseIcon, SearchIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input } from '@/common/components/ui';
import { HighlightedFuzzyText } from '@/features/settings/components/HighlightedFuzzyText';
import {
  getSettingsCategoryIcon,
  groupRankedResults,
  rankSettingsCommandItems,
  type SettingsCommandItem,
} from '@/features/settings/settings-navigation';
import { useSettingsSearchIndex } from '@/features/settings/hooks/useSettingsSearchIndex';

const MAX_VISIBLE_ITEMS = 8;
const COMMIT_DEBOUNCE_MS = 200;

export interface WorkspaceSearchResultsProps {
  /** Seeds the input on mount, e.g. from the current `q` URL param. */
  initialQuery: string;
  /**
   * Called ~200ms after the user stops typing (or immediately on clear) with
   * the latest raw text, so the parent can persist it into the URL with
   * `replace: true`. Never called per-keystroke, and never itself navigates.
   */
  onQueryCommit: (query: string) => void;
  /**
   * Called on explicit selection (click or Enter) with the chosen item and
   * the *current* input text — passed explicitly rather than read back from
   * the (possibly not-yet-committed) URL `q`, so selecting a result the
   * instant after typing never retains a stale query.
   */
  onSelect: (item: SettingsCommandItem, queryText: string) => void;
  className?: string;
}

export function WorkspaceSearchResults({ initialQuery, onQueryCommit, onSelect, className }: WorkspaceSearchResultsProps) {
  const [query, setQuery] = useState(initialQuery);
  const [isFocused, setIsFocused] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const listboxId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const onQueryCommitRef = useRef(onQueryCommit);
  // Refs must not be written during render (React 19 disallows it) — keep the
  // "latest callback" ref fresh via an effect instead of a direct assignment.
  useEffect(() => {
    onQueryCommitRef.current = onQueryCommit;
  }, [onQueryCommit]);

  // Distinguishes "the parent's `?q=` changed because it's echoing back a
  // value this component just committed" (ignore — the debounce/commit
  // round trip is not guaranteed to land in the same render as the commit,
  // so this cannot be inferred from a synchronous "last written" snapshot)
  // from "the parent's `?q=` changed for some other reason" (browser
  // back/forward, a bookmarked deep link re-arriving while mounted, another
  // shell affordance clearing the query) — which must re-sync the visible
  // input/results, or they silently go stale relative to the persisted
  // search state. `pendingSelfCommitValue` holds the exact value most
  // recently committed and not yet seen echoed back; only a matching
  // `initialQuery` clears it without touching `query`, so a late echo can
  // never stomp on further typing that happened in the meantime. This is
  // the React-documented "adjusting state when a prop changes" pattern
  // (render-time, not an effect, and tracked as state rather than a ref so
  // the write is idempotent under React's render rules), for the same
  // reason the active-index reset below uses it: it must apply before
  // paint, not after.
  const [prevInitialQuery, setPrevInitialQuery] = useState(initialQuery);
  const [pendingSelfCommitValue, setPendingSelfCommitValue] = useState<string | null>(null);
  if (initialQuery !== prevInitialQuery) {
    setPrevInitialQuery(initialQuery);
    if (pendingSelfCommitValue !== null && pendingSelfCommitValue === initialQuery) {
      setPendingSelfCommitValue(null);
    } else {
      setQuery(initialQuery);
    }
  }

  const trimmedQuery = query.trim();
  // The index only needs to be fetched once the box has ever been used —
  // gating on focus-or-non-empty avoids an extra background fetch for every
  // authenticated visitor who never touches the search box, while `?q=`
  // arriving from a bookmark/direct-link (non-empty on mount) still fetches
  // immediately rather than waiting for a focus event.
  const { items, isLoading, isError, refetch } = useSettingsSearchIndex({
    enabled: isFocused || trimmedQuery.length > 0,
  });

  const rankedResults = useMemo(
    () => rankSettingsCommandItems(items, query, { maxVisible: MAX_VISIBLE_ITEMS }),
    [items, query],
  );
  const groupedResults = useMemo(() => groupRankedResults(rankedResults), [rankedResults]);

  // Debounced, replace-only URL sync — see the module doc for why this must
  // never push a history entry or drive navigation on its own.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      // Record before calling out — however long the parent takes to echo
      // this back through `initialQuery`, it will be recognized as our own
      // write rather than an external change to re-sync from.
      setPendingSelfCommitValue(query);
      onQueryCommitRef.current(query);
    }, COMMIT_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [query]);

  // "Adjusting state when a prop changes" (not a derived-value effect): reset
  // the active index during render when `query` changes, following the
  // React-documented pattern for this exact case, instead of a `useEffect`
  // that would call setState after paint and risk a stale-active-row flash.
  const [prevQueryForActiveIndex, setPrevQueryForActiveIndex] = useState(query);
  if (query !== prevQueryForActiveIndex) {
    setPrevQueryForActiveIndex(query);
    setActiveIndex(0);
  }

  const isOpen = isFocused && trimmedQuery.length > 0;
  const boundedActiveIndex = rankedResults.length === 0 ? 0 : Math.min(activeIndex, rankedResults.length - 1);
  const activeOption = rankedResults[boundedActiveIndex];
  const activeOptionId = activeOption ? `${listboxId}-${activeOption.item.id}` : undefined;

  const setActiveResult = useCallback((nextIndex: number) => {
    if (rankedResults.length === 0) {
      return;
    }
    setActiveIndex((nextIndex + rankedResults.length) % rankedResults.length);
  }, [rankedResults.length]);

  const commitSelection = useCallback((item: SettingsCommandItem) => {
    onSelect(item, query);
  }, [onSelect, query]);

  const handleInputKeyDown = useCallback((event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Escape') {
      if (isOpen) {
        event.preventDefault();
        // Close the dropdown without discarding the typed text — Escape here
        // collapses the "chrome", it doesn't clear a working search.
        setIsFocused(false);
        inputRef.current?.blur();
      }
      return;
    }

    if (rankedResults.length === 0) {
      return;
    }

    const currentActiveIndex = Math.min(activeIndex, rankedResults.length - 1);

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActiveResult(currentActiveIndex + 1);
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActiveResult(currentActiveIndex - 1);
      return;
    }

    if (event.key === 'Enter' && rankedResults[currentActiveIndex]) {
      event.preventDefault();
      commitSelection(rankedResults[currentActiveIndex].item);
    }
  }, [activeIndex, commitSelection, isOpen, rankedResults, setActiveResult]);

  const handleClear = useCallback(() => {
    setQuery('');
    inputRef.current?.focus();
  }, []);

  return (
    <div className={clsx('relative w-full max-w-xs', className)}>
      <div className="relative">
        <SearchIcon
          className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-pf-text-tertiary"
          ariaLabel=""
        />
        <Input
          ref={inputRef}
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          onFocus={() => setIsFocused(true)}
          onBlur={() => setIsFocused(false)}
          onKeyDown={handleInputKeyDown}
          placeholder="Search all settings"
          aria-label="Search all settings"
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={isOpen}
          aria-controls={isOpen ? listboxId : undefined}
          aria-activedescendant={isOpen ? activeOptionId : undefined}
          className="h-9 pl-9 pr-8 text-sm"
        />
        {query ? (
          <Button
            type="button"
            variant="unstyled"
            size="sm"
            onMouseDown={(event) => event.preventDefault()}
            onClick={handleClear}
            aria-label="Clear search"
            className="absolute right-2 top-1/2 flex h-5 w-5 -translate-y-1/2 items-center justify-center rounded-full text-pf-text-tertiary hover:bg-pf-bg-1 hover:text-pf-text-primary"
            iconCenter={<CloseIcon className="h-3.5 w-3.5" ariaLabel="Close" />}
          />
        ) : null}
      </div>

      {isOpen ? (
        <div
          id={listboxId}
          role="listbox"
          aria-label="Settings search results"
          className="absolute left-0 right-0 z-30 mt-2 max-h-96 overflow-y-auto rounded-lg border border-pf-border bg-pf-panel p-2 shadow-lg"
        >
          {isLoading ? (
            <div className="px-3 py-4 text-sm text-pf-text-secondary">Loading settings index…</div>
          ) : isError ? (
            <div className="flex flex-col items-start gap-2 px-3 py-4">
              <p className="text-sm text-pf-text-secondary">Couldn&apos;t load the settings search index.</p>
              <Button type="button" size="sm" variant="subtle" onClick={() => refetch()}>
                Retry
              </Button>
            </div>
          ) : rankedResults.length > 0 ? (
            <div className="space-y-2">
              {groupedResults.map((group) => {
                const startIndex = rankedResults.indexOf(group.results[0]);
                return (
                  <div key={group.kind} className="space-y-1">
                    <div
                      role="presentation"
                      className="px-2 pt-1 text-[10px] font-semibold uppercase tracking-[0.2em] text-pf-text-tertiary"
                    >
                      {group.label}
                    </div>
                    {group.results.map((result, offset) => {
                      const index = startIndex + offset;
                      const fallbackIcon = getSettingsCategoryIcon(result.item.categoryId);
                      const Icon = result.item.icon ?? fallbackIcon;
                      const isActive = index === boundedActiveIndex;

                      return (
                        <div
                          key={result.item.id}
                          id={`${listboxId}-${result.item.id}`}
                          role="option"
                          aria-selected={isActive}
                          onMouseMove={() => setActiveIndex(index)}
                          onMouseDown={(event) => event.preventDefault()}
                          onClick={() => commitSelection(result.item)}
                          className={clsx(
                            'flex cursor-pointer items-start gap-2.5 rounded-md px-2.5 py-2 text-left',
                            isActive ? 'bg-pf-accent-bg/22 text-pf-text-primary' : 'text-pf-text-secondary hover:bg-pf-bg-1/75',
                          )}
                        >
                          <span className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-pf-border/70 bg-pf-bg-0/80 text-pf-text-secondary" aria-hidden="true">
                            <Icon className="h-3.5 w-3.5" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="block text-[10px] font-semibold uppercase tracking-[0.16em] text-pf-text-tertiary">
                              <HighlightedFuzzyText text={result.item.breadcrumb} matches={result.breadcrumbMatches} />
                            </span>
                            <span className="mt-0.5 block text-sm font-medium text-pf-text-primary">
                              <HighlightedFuzzyText text={result.item.label} matches={result.labelMatches} />
                            </span>
                          </span>
                          <ArrowRightIcon className="mt-1 h-3.5 w-3.5 shrink-0 text-pf-text-tertiary" ariaLabel="Open" />
                        </div>
                      );
                    })}
                  </div>
                );
              })}
            </div>
          ) : (
            <div className="px-3 py-4 text-sm text-pf-text-secondary">
              No matches for &ldquo;{query}&rdquo;.
            </div>
          )}
        </div>
      ) : null}
    </div>
  );
}
