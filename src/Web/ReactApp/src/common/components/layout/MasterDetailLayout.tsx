import React, { useState, useEffect } from 'react';
import { ArrowLeftIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';

interface MasterDetailLayoutProps {
  /**
   * The master (list/nav) panel content
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
   * Layout orientation
   * - 'vertical': master on left (sidebar), detail on right
   * - 'horizontal': master on top (tabs), detail below
   * @default 'vertical'
   */
  orientation?: 'vertical' | 'horizontal';

  /**
   * Master panel width on desktop (default: 'w-80')
   * Only used for vertical orientation
   */
  masterWidth?: string;

  /**
   * Master panel height on desktop (default: 'h-20')
   * Only used for horizontal orientation
   */
  masterHeight?: string;

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
 * Responsive master-detail layout component with flexible orientation
 *
 * **Vertical Mode (default):**
 * - Desktop: Shows master list (sidebar, left) + detail panel (right) side-by-side
 * - Mobile: Shows either master list OR detail panel (toggled)
 * - Best for: CRUD operations (User, Location, Tag admin pages)
 *
 * **Horizontal Mode:**
 * - Desktop: Shows master nav (tabs, top) + detail content (below, full-width)
 * - Mobile: Shows either master nav OR detail (stacked/toggled)
 * - Best for: Tabbed interfaces (Models/GCode/Harvest on FilesPage)
 *
 * Usage:
 * ```tsx
 * // Vertical (sidebar) - for admin list pages
 * <MasterDetailLayout
 *   orientation="vertical"
 *   master={<UsersList selected={selected} onSelect={setSelected} />}
 *   detail={<UserDetails user={selected} />}
 *   hasDetail={!!selected}
 *   onCloseDetail={() => setSelected(null)}
 * />
 *
 * // Horizontal (tabs) - for tabbed content
 * <MasterDetailLayout
 *   orientation="horizontal"
 *   master={<FileTabs activeTab={tab} onChange={setTab} />}
 *   detail={<FilesList tab={tab} />}
 *   hasDetail={true}
 * />
 * ```
 */
export function MasterDetailLayout({
  master,
  detail,
  hasDetail,
  onCloseDetail,
  detailTitle,
  orientation = 'vertical',
  masterWidth = 'w-80',
  masterHeight = 'h-auto',
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
    <div className={`flex h-full w-full bg-pf-bg-0 ${orientation === 'horizontal' ? 'flex-col' : 'flex-row'}`}>
      {/* Master Panel */}
      {(isDesktop || !showDetail) && (
        <div
          className={`
            ${orientation === 'vertical' 
              ? `${masterWidth} border-r border-pf-border` 
              : `${masterHeight} border-b border-pf-border w-full`
            }
            overflow-hidden flex flex-col ${masterClassName}
          `}
        >
          {master}
        </div>
      )}

      {/* Detail Panel */}
      {(isDesktop || showDetail) && (
        <div className={`flex-1 overflow-hidden flex flex-col ${detailClassName}`}>
          {/* Mobile header with back button */}
          {!isDesktop && showDetail && (
            <div className="flex items-center gap-2 px-4 py-3 border-b border-pf-border bg-pf-bg-1 shrink-0">
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
