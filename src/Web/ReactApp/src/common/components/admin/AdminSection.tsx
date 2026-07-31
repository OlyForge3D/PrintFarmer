import clsx from 'clsx';
import type { ReactNode } from 'react';
import { Badge, type BadgeVariant } from '@/common/components/ui/Badge';

export interface AdminSectionProps {
  /** The band's name. Rendered as a real heading, not an eyebrow. */
  caption: ReactNode;
  /** DOM id for the caption, so the section can be `aria-labelledby` it. */
  captionId?: string;
  /**
   * Optional count shown next to the caption. Omitted entirely when undefined
   * or zero — a badge reading "0" is noise, and an empty state says it better.
   */
  count?: number;
  /** Badge tone for `count`. */
  countVariant?: BadgeVariant;
  /** Right-aligned slot on the caption line: a timestamp, a filter, an action. */
  headerAside?: ReactNode;
  /** Gap between the caption line and the body. */
  gap?: 'tight' | 'default' | 'loose';
  /**
   * Heading level for the caption. Bands on a page whose shell already renders
   * an `<h2>` must use `3` or the document grows two competing h2 levels.
   */
  headingLevel?: 2 | 3;
  className?: string;
  children: ReactNode;
}

/**
 * One band of an admin page: a caption, an optional count, an optional
 * right-aligned slot, and the band's content.
 *
 * The caption sets its own face, case, and size rather than inheriting them.
 * That is deliberate: `index.css` has a base rule that force-uppercases every
 * `<h1>`/`<h2>` in the display face but leaves `<h3>` alone, so without this
 * the identical component rendered as an uppercase Space Grotesk band on the
 * Control Center and as a sentence-case Inter line — indistinguishable from a
 * card title — on the settings page. A shared primitive that changes character
 * with its heading level is not shared.
 *
 * The caption is also genuinely louder than anything nested inside it, which
 * fixes a measured inversion: the Control Center used to caption its bands at
 * 14px while the group headings underneath ran 16px, so every child
 * out-shouted its parent. Because the deepest level (a destination card title)
 * is fixed at 14px semibold primary, the only monotonic chain is to raise the
 * band above it rather than squeeze the middle. Group labels below a band
 * therefore take the small uppercase treatment — a classifier over a row of
 * cards, which is what they actually are.
 *
 * Renders a `<section>` so the caption is a real landmark label rather than
 * decoration.
 */
export function AdminSection({
  caption,
  captionId,
  count,
  countVariant = 'default',
  headerAside,
  gap = 'default',
  headingLevel = 2,
  className,
  children,
}: AdminSectionProps) {
  const showCount = typeof count === 'number' && count > 0;
  const gapClass = gap === 'tight' ? 'gap-2' : gap === 'loose' ? 'gap-4' : 'gap-3';
  const Heading = headingLevel === 3 ? 'h3' : 'h2';

  return (
    <section aria-labelledby={captionId} className={clsx('flex flex-col', gapClass, className)}>
      <header className="flex flex-wrap items-baseline justify-between gap-2">
        <div className="flex items-center gap-2">
          <Heading
            id={captionId}
            className="font-pf-display text-lg font-bold uppercase tracking-wide text-pf-text-primary"
          >
            {caption}
          </Heading>
          {showCount && (
            <Badge variant={countVariant} size="sm">
              {count}
            </Badge>
          )}
        </div>
        {headerAside}
      </header>
      {children}
    </section>
  );
}

export default AdminSection;
