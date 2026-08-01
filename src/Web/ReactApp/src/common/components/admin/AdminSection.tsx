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
  /**
   * Slot immediately after the caption, next to `count`. For labels that
   * qualify the section itself ("2 issues") and read as nonsense once they
   * drift to the far edge of a wide column.
   */
  captionAside?: ReactNode;
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
 * The caption is a quiet eyebrow — 12px, uppercase, letterspaced, secondary —
 * not a loud heading. An earlier revision sized it at 18px display-bold on the
 * premise that a band must be typographically louder than everything nested
 * inside it. That premise is wrong, and the design it was derived from never
 * claimed it: a band caption is a *classifier* over a region, the same role a
 * `<legend>` or a table caption plays, and it earns its place through case and
 * letterspacing rather than size. The content hierarchy that does have to stay
 * monotonic lives entirely below the caption — group heading (15px semibold
 * primary) over card title (14px semibold primary) — and is unaffected by how
 * the eyebrow above it is sized.
 *
 * The practical consequence is that the caption stops competing with the page
 * title and stops eating vertical rhythm on settings pages, where a page can
 * carry five or six bands.
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
  captionAside,
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
            className="font-pf-sans text-xs font-semibold uppercase tracking-[0.06em] text-pf-text-secondary"
          >
            {caption}
          </Heading>
          {showCount && (
            <Badge variant={countVariant} size="sm">
              {count}
            </Badge>
          )}
          {captionAside}
        </div>
        {headerAside}
      </header>
      {children}
    </section>
  );
}

export default AdminSection;
