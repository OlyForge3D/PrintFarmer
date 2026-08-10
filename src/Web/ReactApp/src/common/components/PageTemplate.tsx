import React, { ReactNode, useEffect } from 'react';
import { Link } from 'react-router';
import { ArrowLeftIcon } from '@/common/components/icons/MdiIcons';
import { registerPageHeader } from '@/common/components/pageHeaderGuard';

/** A surface this page belongs to, rendered as a back link above the title. */
export interface PageParent {
  /** Human-readable name of the parent surface, e.g. "Admin Control Center" */
  label: string;
  /** Route to navigate back to, e.g. "/admin" */
  to: string;
}

interface PageTemplateProps {
  /** Page title */
  title: string;
  /** Optional subtitle/description */
  subtitle?: string;
  /** Optional icon component to display before title */
  icon?: React.ComponentType<{ className?: string }>;
  /** Optional control rendered inline to the right of the title */
  titleActions?: ReactNode;
  /**
   * Let the title wrap onto multiple lines instead of truncating with an
   * ellipsis. Off by default so existing pages keep the single-line
   * truncation they were built around; opt in for a title whose full text
   * matters more than staying on one line (e.g. the Admin Control Center
   * hub), so narrow viewports show the complete heading instead of clipping
   * it (#1415).
   */
  titleWrap?: boolean;
  /** Optional action buttons or controls for the header */
  actions?: ReactNode;
  /**
   * Surface this page belongs to. Renders a back link above the title so a page
   * reached from a hub is never a dead end.
   */
  parent?: PageParent;
  /**
   * Render children with no header, background or padding of our own.
   *
   * Use this when the page is mounted inside a shell that already supplies page
   * chrome. It guarantees a single `h1` per document instead of each page
   * hand-rolling its own `embedded ? content : <PageTemplate>` branch.
   *
   * The `title`, `subtitle`, `icon` and `parent` props are all suppressed — the
   * shell already answers "where am I". `actions` are not: they are the page's
   * own controls and have nowhere else to go, so they render right-aligned above
   * the content.
   */
  embedded?: boolean;
  /** Main page content */
  children: ReactNode;
  /** Optional max width container class (defaults to max-w-full) */
  maxWidth?: 'max-w-3xl' | 'max-w-4xl' | 'max-w-5xl' | 'max-w-6xl' | 'max-w-7xl' | 'max-w-full';
  /** Optional custom padding (defaults to p-6) */
  padding?: string;
  /** Whether to include top padding for fixed header (defaults to true) */
  includeTopPadding?: boolean;
  /** Optional background color (defaults to bg-pf-bg-2) */
  backgroundColor?: string;
  /** Hide the header region while retaining layout chrome */
  showHeader?: boolean;
  /**
   * Make the page fill exactly the viewport slot rather than growing with its
   * content, so a descendant marked `flex-1 min-h-0` becomes a real scrollport.
   *
   * Off by default: a page that simply flows down the document must keep
   * growing, and `min-h-full` is right for it. Opt in when the page owns an
   * internal scroll region — the settings shell does, because its save bar has
   * to stay docked below the fold-line while the cards above it scroll.
   *
   * Marks the root with `data-page-fill`, which `Layout` keys its `:has()`
   * variants off so the height chain above this component is only bounded for
   * pages that asked for it.
   */
  fill?: boolean;
}

/**
 * Standardized page template for consistent layout across all pages.
 * 
 * @example
 * ```tsx
 * <PageTemplate
 *   title="Printers"
 *   subtitle="Monitor and manage your 3D printers"
 *   icon={PrinterIcon}
 *   parent={{ label: 'Admin Control Center', to: '/admin' }}
 *   titleActions={<HelpButton onClick={startTour} />}
 *   actions={<AddPrinterButton />}
 * >
 *   <YourPageContent />
 * </PageTemplate>
 * ```
 *
 * When a page is mounted inside a shell that already renders page chrome, pass
 * `embedded` instead of branching on it at the call site. The header is
 * suppressed; `actions` still render, right-aligned above the content:
 *
 * ```tsx
 * <PageTemplate title="Printer Groups" actions={<AddGroupButton />} embedded={embedded}>
 *   <YourPageContent />
 * </PageTemplate>
 * ```
 */
export function PageTemplate({
  title,
  subtitle,
  icon: Icon,
  titleActions,
  titleWrap = false,
  actions,
  parent,
  embedded = false,
  children,
  maxWidth = 'max-w-full',
  padding = 'px-4',
  includeTopPadding = true,
  backgroundColor = 'bg-pf-bg-2',
  showHeader = true,
  fill = false
}: PageTemplateProps) {
  // Only a header-rendering instance can collide with another one, so only those
  // register. See pageHeaderGuard for what this catches.
  useEffect(() => {
    if (embedded || !showHeader) {
      return undefined;
    }
    return registerPageHeader(title);
  }, [embedded, showHeader, title]);

  // Mounted inside a shell that already renders page chrome: emit the page's own
  // actions and content, and nothing else, so the document keeps exactly one h1
  // and one page background.
  if (embedded) {
    return (
      <>
        {actions && (
          <div className="mb-4 flex flex-wrap items-center justify-end gap-2">{actions}</div>
        )}
        {children}
      </>
    );
  }

  return (
    <div
      className={`${fill ? 'flex min-h-0 flex-1 flex-col' : 'min-h-full'} ${backgroundColor} ${includeTopPadding ? 'pt-4 pb-4' : 'pb-4'}`}
      data-header-visible={showHeader ? 'true' : 'false'}
      data-page-fill={fill ? 'true' : undefined}
      aria-label={showHeader ? undefined : title}
    >
      <div className={`min-w-0 ${maxWidth} ${padding}${fill ? ' flex min-h-0 flex-1 flex-col' : ''}`}>
        {/* Page Header */}
        {showHeader && (
          <div className="mb-4 lg:mr-[var(--pf-floating-bar-inset,0px)]">
            {parent && (
              <Link
                to={parent.to}
                className="-ml-1 mb-1 inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-xs font-medium text-pf-text-secondary transition-colors hover:bg-pf-bg-1 hover:text-pf-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent"
              >
                <span aria-hidden="true" className="flex">
                  <ArrowLeftIcon className="h-3.5 w-3.5" />
                </span>
                {parent.label}
              </Link>
            )}
            <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
              <div className="min-w-0">
                <div className={`flex gap-2 min-w-0 ${titleWrap ? 'items-start' : 'items-center'}`}>
                  {Icon && <Icon className="h-6 w-6 shrink-0" aria-hidden="true" />}
                  <h1
                    className={`min-w-0 text-2xl font-bold text-pf-text-primary ${titleWrap ? 'whitespace-normal break-words' : 'truncate'}`}
                  >
                    {title}
                  </h1>
                  {titleActions && <div className="shrink-0">{titleActions}</div>}
                </div>
                {subtitle && (
                  <p className="text-pf-text-secondary mt-1">
                    {subtitle}
                  </p>
                )}
              </div>
              {actions && <div className="max-w-full self-start sm:shrink-0">{actions}</div>}
            </div>
          </div>
        )}

        {/* Page Content */}
        {children}
      </div>
    </div>
  );
}
