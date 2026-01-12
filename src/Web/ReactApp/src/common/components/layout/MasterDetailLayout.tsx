import React, { useState, useEffect } from 'react';
import { ArrowLeftIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';

interface MasterDetailLayoutProps {
  /**
   * The master (list) panel content
   */
  master: React.ReactNode;

  /**
   * The detail panel content
   */
  detail: React.ReactNode;

  /**
   * Whether a detail is currently selected
   */
  hasDetail: boolean;

  /**
   * Callback to close detail panel on mobile
   */
  onCloseDetail?: () => void;

  /**
   * Detail panel title (shown on mobile above detail content)
   */
  detailTitle?: string;

  /**
   * Master panel width on desktop (default: 'w-80')
   */
  masterWidth?: string;

  /**
   * CSS classes for master panel
   */
  masterClassName?: string;

  /**
   * CSS classes for detail panel
   */
  detailClassName?: string;

  /**
   * Breakpoint for mobile/desktop (default: 'lg')
   * Can be: 'sm', 'md', 'lg', 'xl', '2xl'
   */
  breakpoint?: 'sm' | 'md' | 'lg' | 'xl' | '2xl';
}

/**
 * Responsive master-detail layout component
 * On desktop: Shows master list (sidebar) + detail panel side-by-side
 * On mobile: Shows either master list OR detail panel (toggled)
 *
 * Usage:
 * ```tsx
 * <MasterDetailLayout
 *   master={<ModelsList selected={selected} onSelect={setSelected} />}
 *   detail={<ModelDetails model={selected} />}
 *   hasDetail={!!selected}
 *   onCloseDetail={() => setSelected(null)}
 *   detailTitle={selected?.name}
 * />
 * ```
 */
export function MasterDetailLayout({
  master,
  detail,
  hasDetail,
  onCloseDetail,
  detailTitle,
  masterWidth = 'w-80',
  masterClassName = '',
  detailClassName = '',
  breakpoint = 'lg',
}: MasterDetailLayoutProps) {
  const [showDetail, setShowDetail] = useState(false);
  const [isDesktop, setIsDesktop] = useState(true);

  // Determine breakpoint pixel value - memoized object to avoid dependency issues
  const breakpointPixels = {
    sm: 640,
    md: 768,
    lg: 1024,
    xl: 1280,
    '2xl': 1536,
  } as const;

  // Detect screen size
  useEffect(() => {
    const pixels = breakpointPixels[breakpoint];
    const mediaQuery = window.matchMedia(`(min-width: ${pixels}px)`);

    const handleChange = (e: MediaQueryListEvent) => {
      setIsDesktop(e.matches);
      if (e.matches) {
        // On desktop, always show master
        setShowDetail(false);
      }
    };

    // Set initial state
    setIsDesktop(mediaQuery.matches);

    // Listen for changes
    mediaQuery.addEventListener('change', handleChange);
    return () => mediaQuery.removeEventListener('change', handleChange);
    // breakpointPixels is a constant object, safe to omit from dependencies
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [breakpoint]);

  // Handle detail state changes
  useEffect(() => {
    if (hasDetail && !isDesktop) {
      setShowDetail(true);
    } else if (!hasDetail) {
      setShowDetail(false);
    }
  }, [hasDetail, isDesktop]);

  const handleCloseDetail = () => {
    setShowDetail(false);
    onCloseDetail?.();
  };

  return (
    <div className="flex h-full w-full bg-pf-bg-0">
      {/* Master Panel - Always visible on desktop, conditional on mobile */}
      {(isDesktop || !showDetail) && (
        <div
          className={`${masterWidth} border-r border-pf-border overflow-hidden flex flex-col ${masterClassName}`}
        >
          {master}
        </div>
      )}

      {/* Detail Panel - Overlay on mobile, flex-1 on desktop */}
      {(isDesktop || showDetail) && (
        <div className={`flex-1 overflow-hidden flex flex-col ${detailClassName}`}>
          {/* Mobile header with back button */}
          {!isDesktop && showDetail && (
            <div className="flex items-center gap-2 px-4 py-3 border-b border-pf-border bg-pf-bg-1 flex-shrink-0">
              <Button
                variant="subtle"
                size="sm"
                onClick={handleCloseDetail}
                aria-label="Back to list"
                type="button"
              >
                <ArrowLeftIcon className="w-5 h-5" />
              </Button>
              <span className="text-sm font-medium flex-1 truncate text-pf-text-primary">
                {detailTitle || 'Details'}
              </span>
            </div>
          )}

          {/* Detail content */}
          <div className="flex-1 overflow-auto">{detail}</div>
        </div>
      )}
    </div>
  );
}
