/* eslint-disable local/pf-no-raw-html-controls */
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import clsx from 'clsx';
import { ArrowRightIcon, CloseIcon, SearchIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input } from '@/common/components/ui';
import { getSettingsCategoryIcon, type SettingsCommandItem } from '@/features/settings/settings-navigation';

const ANIMATION_DURATION_MS = 120;
const MAX_VISIBLE_ITEMS = 12;
const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  'a[href]',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');
const COMMAND_PALETTE_NOISE = "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='64' height='64' filter='url(%23n)' opacity='1'/%3E%3C/svg%3E\")";

interface CommandPaletteProps {
  isOpen: boolean;
  items: SettingsCommandItem[];
  onClose: () => void;
  onSelect: (item: SettingsCommandItem) => void;
}

interface FuzzyResult {
  item: SettingsCommandItem;
  score: number;
  labelMatches: number[];
  breadcrumbMatches: number[];
}

function normalizeQuery(value: string): string {
  return value.trim().toLowerCase();
}

function getReducedMotionPreference(): boolean {
  return typeof window !== 'undefined'
    && typeof window.matchMedia === 'function'
    && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function getFuzzyMatchIndices(text: string, query: string): number[] | null {
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

function scoreMatches(matches: number[]): number {
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

function getFuzzyResult(item: SettingsCommandItem, query: string): FuzzyResult | null {
  if (!query) {
    return {
      item,
      score: 0,
      labelMatches: [],
      breadcrumbMatches: [],
    };
  }

  const labelMatches = getFuzzyMatchIndices(item.label, query);
  const breadcrumbMatches = getFuzzyMatchIndices(item.breadcrumb, query);
  const keywordExactMatch = item.keywords.some((keyword) => keyword.includes(query));

  if (!labelMatches && !breadcrumbMatches && !keywordExactMatch) {
    return null;
  }

  let score = 300;

  if (labelMatches) {
    score -= 180;
    score += scoreMatches(labelMatches);
    if (item.label.toLowerCase().includes(query)) {
      score -= 24;
    }
    if (item.label.toLowerCase().startsWith(query)) {
      score -= 30;
    }
  }

  if (breadcrumbMatches) {
    score -= 70;
    score += scoreMatches(breadcrumbMatches);
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

function HighlightedFuzzyText({ text, matches }: { text: string; matches: number[] }) {
  const matchSet = useMemo(() => new Set(matches), [matches]);

  if (matchSet.size === 0) {
    return <>{text}</>;
  }

  return (
    <>
      {Array.from(text).map((character, index) => (
        <span
          key={`${character}-${index}`}
          className={clsx(
            matchSet.has(index) && 'rounded-sm bg-pf-accent-bg/45 px-[0.08rem] text-pf-text-primary',
          )}
        >
          {character}
        </span>
      ))}
    </>
  );
}

export function CommandPalette({ isOpen, items, onClose, onSelect }: CommandPaletteProps) {
  const titleId = useId();
  const descriptionId = useId();
  const [query, setQuery] = useState('');
  const [activeIndex, setActiveIndex] = useState(0);
  const [isRendered, setIsRendered] = useState(isOpen);
  const [isVisible, setIsVisible] = useState(false);
  const [prefersReducedMotion, setPrefersReducedMotion] = useState(() => getReducedMotionPreference());
  const dialogRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const resultRefs = useRef<Map<string, HTMLButtonElement>>(new Map());
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const shouldRestoreFocusRef = useRef(true);

  const filteredItems = useMemo(() => {
    const normalizedQuery = normalizeQuery(query);
    const results = items
      .map((item) => getFuzzyResult(item, normalizedQuery))
      .filter((result): result is FuzzyResult => result !== null)
      .sort((left, right) => left.score - right.score || left.item.breadcrumb.localeCompare(right.item.breadcrumb));

    return results.slice(0, MAX_VISIBLE_ITEMS);
  }, [items, query]);

  useEffect(() => {
    if (activeIndex <= filteredItems.length - 1) {
      return;
    }

    setActiveIndex(0);
  }, [activeIndex, filteredItems.length]);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return undefined;
    }

    const mediaQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    const updatePreference = () => setPrefersReducedMotion(mediaQuery.matches);

    updatePreference();
    mediaQuery.addEventListener('change', updatePreference);
    return () => mediaQuery.removeEventListener('change', updatePreference);
  }, []);

  useEffect(() => {
    if (isOpen) {
      previousFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      shouldRestoreFocusRef.current = true;
      setIsRendered(true);
      setQuery('');
      setActiveIndex(0);

      const animationFrame = window.requestAnimationFrame(() => {
        setIsVisible(true);
      });

      return () => window.cancelAnimationFrame(animationFrame);
    }

    setIsVisible(false);
    const timeout = window.setTimeout(() => setIsRendered(false), prefersReducedMotion ? 0 : ANIMATION_DURATION_MS);

    if (shouldRestoreFocusRef.current) {
      previousFocusRef.current?.focus();
    }

    return () => window.clearTimeout(timeout);
  }, [isOpen, prefersReducedMotion]);

  useEffect(() => {
    if (!isRendered || !isOpen) {
      return;
    }

    inputRef.current?.focus();
  }, [isRendered, isOpen]);

  useEffect(() => {
    if (!isRendered) {
      return undefined;
    }

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [isRendered]);

  const focusResult = useCallback((index: number) => {
    const result = filteredItems[index];
    if (!result) {
      return;
    }

    setActiveIndex(index);
    resultRefs.current.get(result.item.id)?.focus();
  }, [filteredItems]);

  const handleDismiss = useCallback(() => {
    shouldRestoreFocusRef.current = true;
    onClose();
  }, [onClose]);

  const handleSelect = useCallback((item: SettingsCommandItem) => {
    shouldRestoreFocusRef.current = false;
    onSelect(item);
  }, [onSelect]);

  const handleDialogKeyDown = useCallback((event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Tab') {
      const focusableElements = dialogRef.current
        ? Array.from(dialogRef.current.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter((element) => !element.hasAttribute('aria-hidden'))
        : [];

      if (focusableElements.length === 0) {
        event.preventDefault();
        return;
      }

      const firstElement = focusableElements[0];
      const lastElement = focusableElements[focusableElements.length - 1];
      const activeElement = document.activeElement;

      if (event.shiftKey && activeElement === firstElement) {
        event.preventDefault();
        lastElement.focus();
      } else if (!event.shiftKey && activeElement === lastElement) {
        event.preventDefault();
        firstElement.focus();
      }

      return;
    }

    if (event.key === 'Escape') {
      event.preventDefault();
      handleDismiss();
      return;
    }

    if (event.target !== inputRef.current) {
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      focusResult(Math.min(activeIndex + 1, filteredItems.length - 1));
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      focusResult(Math.max(activeIndex - 1, 0));
      return;
    }

    if (event.key === 'Enter' && filteredItems[activeIndex]) {
      event.preventDefault();
      handleSelect(filteredItems[activeIndex].item);
    }
  }, [activeIndex, filteredItems, focusResult, handleDismiss, handleSelect]);

  const handleResultKeyDown = useCallback((event: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      focusResult(index === filteredItems.length - 1 ? 0 : index + 1);
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      focusResult(index === 0 ? filteredItems.length - 1 : index - 1);
      return;
    }

    if (event.key === 'Home') {
      event.preventDefault();
      focusResult(0);
      return;
    }

    if (event.key === 'End') {
      event.preventDefault();
      focusResult(filteredItems.length - 1);
      return;
    }

    if (event.key === 'Backspace' && !query) {
      inputRef.current?.focus();
    }
  }, [filteredItems.length, focusResult, query]);

  if (!isRendered) {
    return null;
  }

  return createPortal(
    <div
      className={clsx(
        'fixed inset-0 z-[60] flex items-start justify-center px-4 pt-[12vh] transition-opacity duration-[120ms] ease-out motion-reduce:transition-none',
        isVisible ? 'opacity-100' : 'opacity-0',
      )}
      onClick={handleDismiss}
      aria-hidden={false}
    >
      <div
        className={clsx(
          'absolute inset-0 bg-pf-bg-2/72 backdrop-blur-sm transition-opacity duration-[120ms] ease-out motion-reduce:transition-none',
          isVisible ? 'opacity-100' : 'opacity-0',
        )}
        aria-hidden="true"
      />

      <div
        ref={dialogRef}
        role="dialog"
        aria-hidden={!isOpen}
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        onKeyDown={handleDialogKeyDown}
        onClick={(event) => event.stopPropagation()}
        className={clsx(
          'relative w-full max-w-[32rem] overflow-hidden rounded-[1.75rem] border border-pf-border/80 bg-pf-bg-0/92 shadow-[0_24px_80px_-40px_rgba(0,0,0,0.9)] backdrop-blur-xl transition duration-[120ms] ease-out motion-reduce:transition-none',
          isVisible ? 'translate-y-0 scale-100 opacity-100' : 'translate-y-1 scale-[0.985] opacity-0',
        )}
      >
        <div className="pointer-events-none absolute inset-0 opacity-[0.03]" style={{ backgroundImage: COMMAND_PALETTE_NOISE, backgroundSize: '140px 140px' }} aria-hidden="true" />
        <div className="pointer-events-none absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-pf-border to-transparent" aria-hidden="true" />
        <div className="pointer-events-none absolute inset-x-0 top-0 h-20 bg-gradient-to-b from-pf-accent-bg/12 via-transparent to-transparent" aria-hidden="true" />

        <div className="relative border-b border-pf-border/70 px-5 py-4">
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-[11px] font-semibold uppercase tracking-[0.22em] text-pf-text-tertiary">Settings</p>
              <h2 id={titleId} className="mt-1 text-lg font-semibold text-pf-text-primary">Command palette</h2>
              <p id={descriptionId} className="mt-1 text-sm text-pf-text-secondary">Jump to any settings area with fuzzy search and keyboard navigation.</p>
            </div>
            <Button
              type="button"
              variant="unstyled"
              size="sm"
              onClick={handleDismiss}
              aria-label="Close command palette"
              className="rounded-full p-2 text-pf-text-secondary transition-colors duration-[120ms] ease-out hover:bg-pf-bg-1 hover:text-pf-text-primary focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset motion-reduce:transition-none"
              iconCenter={<span aria-hidden="true"><CloseIcon className="h-4 w-4" ariaLabel="Close" /></span>}
            />
          </div>

          <div className="relative mt-4">
            <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-secondary" aria-hidden="true">
              <SearchIcon className="h-4 w-4" ariaLabel="Search" />
            </span>
            <Input
              ref={inputRef}
              type="search"
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
                setActiveIndex(0);
              }}
              placeholder="Search settings, sections, or keywords"
              aria-label="Search settings command palette"
              className="h-12 pl-9 pr-24 text-sm"
            />
            <span className="pointer-events-none absolute right-3 top-1/2 hidden -translate-y-1/2 rounded-md border border-pf-border bg-pf-bg-1 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-[0.16em] text-pf-text-tertiary sm:inline-flex">
              {typeof navigator !== 'undefined' && navigator.platform.toLowerCase().includes('mac') ? '⌘K' : 'Ctrl K'}
            </span>
          </div>
        </div>

        <div className="relative px-3 py-3">
          {filteredItems.length > 0 ? (
            <div role="listbox" aria-label="Settings search results" className="space-y-1.5">
              {filteredItems.map((result, index) => {
                const Icon = getSettingsCategoryIcon(result.item.categoryId);
                const isActive = index === activeIndex;

                return (
                  <button
                    key={result.item.id}
                    ref={(element) => {
                      if (element) {
                        resultRefs.current.set(result.item.id, element);
                      } else {
                        resultRefs.current.delete(result.item.id);
                      }
                    }}
                    type="button"
                    role="option"
                    aria-selected={isActive}
                    onClick={() => handleSelect(result.item)}
                    onFocus={() => setActiveIndex(index)}
                    onKeyDown={(event) => handleResultKeyDown(event, index)}
                    className={clsx(
                      'group flex w-full items-start gap-3 rounded-2xl border px-4 py-3 text-left transition duration-[120ms] ease-out motion-reduce:transition-none',
                      isActive
                        ? 'border-pf-accent/60 bg-pf-accent-bg/14 text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.04)]'
                        : 'border-transparent bg-pf-bg-1/55 text-pf-text-secondary hover:border-pf-border hover:bg-pf-bg-1/75 hover:text-pf-text-primary',
                    )}
                  >
                    <span
                      className={clsx(
                        'mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-xl border border-pf-border/70 bg-pf-bg-0/80',
                        isActive ? 'text-pf-accent' : 'text-pf-text-secondary group-hover:text-pf-text-primary',
                      )}
                      aria-hidden="true"
                    >
                      <Icon className="h-4 w-4" />
                    </span>

                    <span className="min-w-0 flex-1">
                      <span className="block text-[11px] font-semibold uppercase tracking-[0.18em] text-pf-text-tertiary">
                        <HighlightedFuzzyText text={result.item.breadcrumb} matches={result.breadcrumbMatches} />
                      </span>
                      <span className="mt-1 block text-sm font-medium text-pf-text-primary">
                        <HighlightedFuzzyText text={result.item.label} matches={result.labelMatches} />
                      </span>
                      <span className="mt-1 block text-sm text-pf-text-secondary">{result.item.description}</span>
                    </span>

                    <span aria-hidden="true">
                      <ArrowRightIcon
                        className={clsx(
                          'mt-1 h-4 w-4 shrink-0 transition-transform duration-[120ms] ease-out motion-reduce:transition-none',
                          isActive ? 'translate-x-0 text-pf-accent' : 'text-pf-text-tertiary group-hover:translate-x-0.5',
                        )}
                        ariaLabel="Open setting"
                      />
                    </span>
                  </button>
                );
              })}
            </div>
          ) : (
            <div className="rounded-2xl border border-dashed border-pf-border/80 bg-pf-bg-1/45 px-5 py-8 text-center">
              <p className="text-sm font-medium text-pf-text-primary">No settings matched</p>
              <p className="mt-2 text-sm text-pf-text-secondary">Try a broader term like theme, workers, audit, or camera.</p>
            </div>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}
