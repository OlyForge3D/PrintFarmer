import React, { ReactNode } from 'react';

interface PageTemplateProps {
  /** Page title */
  title: string;
  /** Optional subtitle/description */
  subtitle?: string;
  /** Optional icon component to display before title */
  icon?: React.ComponentType<{ className?: string }>;
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
  actions,
  children,
  maxWidth = 'max-w-full',
  padding = 'px-4',
  includeTopPadding = true,
  backgroundColor = 'bg-pf-bg-2'
}: PageTemplateProps) {
  return (
    <div className={`min-h-screen ${backgroundColor} ${includeTopPadding ? 'pt-4 pb-4' : 'pb-4'}`}>
      <div className={`${maxWidth} ${padding}`}>
        {/* Page Header */}
        <div className="flex justify-between items-center mb-4">
          <div>
            <h2 className="text-2xl font-bold text-pf-text-primary flex items-center">
              {Icon && <Icon className="h-6 w-6 mr-2" />}
              {title}
            </h2>
            {subtitle && (
              <p className="text-pf-text-secondary mt-1">
                {subtitle}
              </p>
            )}
          </div>
          {actions && <div className="flex-shrink-0">{actions}</div>}
        </div>

        {/* Page Content */}
        {children}
      </div>
    </div>
  );
}
