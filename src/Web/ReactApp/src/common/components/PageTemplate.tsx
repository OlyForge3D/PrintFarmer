import React, { ReactNode } from 'react';

interface PageTemplateProps {
  /** Page title */
  title: string;
  /** Optional subtitle/description */
  subtitle?: string;
  /** Optional icon component to display before title */
  icon?: React.ComponentType<{ className?: string }>;
  /** Optional control rendered inline to the right of the title */
  titleActions?: ReactNode;
  /** Optional action buttons or controls for the header */
  actions?: ReactNode;
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
 *   titleActions={<HelpButton onClick={startTour} />}
 *   actions={<AddPrinterButton />}
 * >
 *   <YourPageContent />
 * </PageTemplate>
 * ```
 */
export function PageTemplate({
  title,
  subtitle,
  icon: Icon,
  titleActions,
  actions,
  children,
  maxWidth = 'max-w-full',
  padding = 'px-4',
  includeTopPadding = true,
  backgroundColor = 'bg-pf-bg-2',
  showHeader = true
}: PageTemplateProps) {
  return (
    <div
      className={`min-h-full ${backgroundColor} ${includeTopPadding ? 'pt-4 pb-4' : 'pb-4'}`}
      data-header-visible={showHeader ? 'true' : 'false'}
      aria-label={showHeader ? undefined : title}
    >
      <div className={`min-w-0 ${maxWidth} ${padding}`}>
        {/* Page Header */}
        {showHeader && (
          <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between lg:mr-72">
            <div className="min-w-0">
              <div className="flex items-center gap-2 min-w-0">
                {Icon && <Icon className="h-6 w-6 shrink-0" aria-hidden="true" />}
                <h1 className="min-w-0 truncate text-2xl font-bold text-pf-text-primary">
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
        )}

        {/* Page Content */}
        {children}
      </div>
    </div>
  );
}
