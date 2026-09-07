import { useMemo } from 'react';
import clsx from 'clsx';

/**
 * Renders `text` with the characters at `matches` (fuzzy-match indices from
 * {@link getFuzzyResult} in `settings-navigation.ts`) visually highlighted.
 * Shared between the modal {@link CommandPalette} (#938) and the persistent
 * workspace search results panel (#2505) — both surfaces rank the same
 * {@link FuzzyResult} shape and must highlight matches identically.
 */
export function HighlightedFuzzyText({ text, matches }: { text: string; matches: number[] }) {
  const matchSet = useMemo(() => new Set(matches), [matches]);

  if (matchSet.size === 0) {
    return <span className="break-words">{text}</span>;
  }

  // Wrap the per-character spans in a single containing element rather than
  // a bare fragment. Call sites render this inside a `flex gap-*` row (e.g.
  // the label next to the destructive "Confirm" badge); a fragment lets
  // React flatten every character span directly into that flex container,
  // so the row's gap gets inserted between each individual character and
  // overflows the result card on narrow viewports (#1710). Keeping this as
  // one element makes it exactly one flex item, and `break-words` lets long
  // unbroken text wrap within the card instead of overflowing it.
  // `aria-label` overrides the computed accessible name so assistive tech
  // reads `text` as-written. Without it, the accessible-name algorithm trims
  // each single-character span independently, and a lone whitespace
  // character trims to "" — silently merging words together (e.g. "Login
  // Audit" -> "LoginAudit"). The per-character `<span>`s below stay
  // `aria-hidden` so they contribute only visual highlighting, never text.
  return (
    <span className="break-words" aria-label={text}>
      {Array.from(text).map((character, index) => (
        <span
          key={`${character}-${index}`}
          aria-hidden="true"
          className={clsx(
            matchSet.has(index) && 'rounded-sm bg-pf-accent-bg/45 px-[0.08rem] text-pf-text-primary',
          )}
        >
          {character}
        </span>
      ))}
    </span>
  );
}
