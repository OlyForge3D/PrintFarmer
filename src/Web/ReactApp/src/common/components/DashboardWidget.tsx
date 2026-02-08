import React, { useState } from 'react';
import { Link } from 'react-router';
import { ChevronRightIcon, AlertIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';

export interface DashboardWidgetProps {
  /** Widget title */
  title: string;
  /** Optional icon to display in header */
  icon?: React.ComponentType<{ className?: string }>;
  /** Icon color class */
  iconColorClass?: string;
  /** Icon background color class */
  iconBgClass?: string;
  /** Subtitle text below title */
  subtitle?: string;
  /** Badge/count to show in header */
  badge?: React.ReactNode;
  /** Whether the widget starts collapsed */
  defaultCollapsed?: boolean;
  /** Content to display when there are items */
  children: React.ReactNode;
  /** Content to display when empty */
  emptyState?: React.ReactNode;
  /** Whether the widget has content (controls empty state display) */
  hasContent?: boolean;
  /** Additional class names */
  className?: string;
  /** Link for "More Info" action - renders as link with chevron */
  moreInfoLink?: string;
  /** Text for more info link (default: "View All") */
  moreInfoText?: string;
  /** Storage key for persisting collapsed state */
  storageKey?: string;
  /** Whether collapsing is enabled (default: false) */
  collapsible?: boolean;
  /** Whether the widget is loading */
  isLoading?: boolean;
  /** Error message to display */
  error?: string;
  /** Custom header action (e.g., refresh button) - renders in header next to moreInfoLink */
  headerAction?: React.ReactNode;
}

/**
 * DashboardWidget - A reusable widget for the dashboard.
 * Modeled after MaintenanceAlertsWidget and MaintenanceOverviewWidget.
 * 
 * Structure:
 * - Optional icon with colored background
 * - Title
 * - Optional subtitle
 * - Optional "View All >" link
 * - Horizontal separator
 * - Main content area
 */
export function DashboardWidget({
  title,
  icon: Icon,
  iconColorClass = 'text-pf-text-tertiary',
  iconBgClass = 'bg-pf-bg-2',
  subtitle,
  badge,
  defaultCollapsed = false,
  children,
  emptyState,
  hasContent = true,
  className = '',
  moreInfoLink,
  moreInfoText = 'View All',
  storageKey,
  collapsible = false,
  isLoading = false,
  error,
  headerAction,
}: DashboardWidgetProps) {
  // Use localStorage to persist collapsed state if storageKey provided
  const getInitialCollapsed = () => {
    if (!collapsible) return false;
    if (storageKey) {
      const stored = localStorage.getItem(`dashboard-widget-${storageKey}`);
      if (stored !== null) return stored === 'true';
    }
    return defaultCollapsed;
  };

  const [isCollapsed, setIsCollapsed] = useState(getInitialCollapsed);

  const toggleCollapsed = () => {
    if (!collapsible) return;
    const newState = !isCollapsed;
    setIsCollapsed(newState);
    if (storageKey) {
      localStorage.setItem(`dashboard-widget-${storageKey}`, String(newState));
    }
  };

  const contentId = `widget-content-${storageKey || title.replace(/\s+/g, '-').toLowerCase()}`;

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-xl overflow-hidden self-start ${className}`}>
      {/* Header - matches MaintenanceAlertsWidget styling exactly */}
      <div className={`px-4 py-3 flex items-center justify-between ${!isCollapsed ? 'border-b border-pf-border' : ''}`}>
        {/* Left side: Icon + Title/Subtitle */}
        <div 
          className={`flex items-center gap-3 ${collapsible ? 'cursor-pointer' : ''}`}
          onClick={collapsible ? toggleCollapsed : undefined}
          role={collapsible ? 'button' : undefined}
          aria-expanded={collapsible ? !isCollapsed : undefined}
          aria-controls={collapsible ? contentId : undefined}
          tabIndex={collapsible ? 0 : undefined}
          onKeyDown={collapsible ? (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
              e.preventDefault();
              toggleCollapsed();
            }
          } : undefined}
        >
          {/* Icon in colored container */}
          {Icon && (
            <div className={`p-2 rounded-lg shrink-0 ${iconBgClass}`}>
              <Icon className={`h-5 w-5 ${iconColorClass}`} />
            </div>
          )}
          
          {/* Title and subtitle */}
          <div>
            <h3 className="font-semibold text-pf-text-primary text-sm flex items-center gap-2">
              {title}
              {badge}
            </h3>
            {subtitle && (
              <p className="text-xs text-pf-text-tertiary">{subtitle}</p>
            )}
          </div>
        </div>
        
        {/* Right side: More Info link and/or custom action */}
        <div className="flex items-center gap-2">
          {headerAction}
          {moreInfoLink && (
            <Link to={moreInfoLink}>
              <Button 
                variant="subtle" 
                size="sm"
                iconRight={<ChevronRightIcon className="h-4 w-4 ml-1" />}
              >
                {moreInfoText}
              </Button>
            </Link>
          )}
        </div>
      </div>

      {/* Content Area */}
      {!isCollapsed && (
        <div id={contentId} className="p-3">
          {isLoading ? (
            <div className="space-y-2">
              {Array.from({ length: 3 }).map((_, i) => (
                <div key={i} className="h-12 bg-pf-border/50 rounded-lg animate-pulse" />
              ))}
            </div>
          ) : error ? (
            <div className="flex items-center gap-3 text-red-400 py-4">
              <AlertIcon className="h-5 w-5" />
              <span className="text-sm">{error}</span>
            </div>
          ) : hasContent ? children : emptyState}
        </div>
      )}
    </div>
  );
}

export default DashboardWidget;
