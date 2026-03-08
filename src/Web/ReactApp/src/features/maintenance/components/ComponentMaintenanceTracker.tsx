/**
 * ComponentMaintenanceTracker Component
 * 
 * Displays maintenance tracking data organized by component type.
 * Shows schedules, maintenance history, and cost tracking per component.
 */

import React, { useState } from 'react';
import { format } from 'date-fns';
import { 
  GearIcon, 
  AlertIcon, 
  CheckCircleIcon,
  ChevronRightIcon,
  ClockIcon,
  TagIcon
} from '@/common/components/icons/MdiIcons';
import { Badge, Button } from '@/common/components/ui';
import type { ComponentMaintenanceData } from '../hooks/useComponentMaintenance';

export interface ComponentMaintenanceTrackerProps {
  /** Component maintenance data */
  componentData: ComponentMaintenanceData[];
  /** Loading state */
  isLoading?: boolean;
  /** Callback when component is selected */
  onComponentSelect?: (component: string) => void;
  /** Currently selected component */
  selectedComponent?: string;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Get icon for component type
 */
function getComponentIcon(): React.ReactNode {
  // All use GearIcon for now, could be customized per component
  return <GearIcon className="h-5 w-5" />;
}

/**
 * Get color for component based on maintenance status
 */
function getComponentColor(data: ComponentMaintenanceData): string {
  if (data.scheduleCount === 0) return 'text-pf-text-tertiary';
  if (data.maintenanceCount === 0) return 'text-pf-warning';
  return 'text-pf-success';
}

interface ComponentCardProps {
  data: ComponentMaintenanceData;
  isSelected: boolean;
  onSelect: () => void;
}

function ComponentCard({ data, isSelected, onSelect }: ComponentCardProps) {
  const colorClass = getComponentColor(data);

  return (
    <Button
      variant={isSelected ? 'tab' : 'subtle'}
      type="button"
      onClick={onSelect}
      className={`
        w-full text-left p-4 rounded-xl transition-all duration-150
        ${isSelected 
          ? 'bg-pf-accent/10 ring-2 ring-pf-accent/30' 
          : 'bg-pf-bg-1 hover:bg-pf-border/30'
        }
      `}
    >
      <div className="flex items-start gap-3">
        {/* Icon */}
        <div className={`p-2 rounded-lg bg-pf-bg-2 ${colorClass}`}>
          {getComponentIcon()}
        </div>

        {/* Content */}
        <div className="flex-1 min-w-0">
          <div className="flex items-center justify-between">
            <h3 className="font-semibold text-pf-text-primary">
              {data.component}
            </h3>
            <ChevronRightIcon 
              className={`h-4 w-4 text-pf-text-tertiary transition-transform ${isSelected ? 'rotate-90' : ''}`} 
            />
          </div>

          <div className="mt-2 grid grid-cols-2 gap-2 text-xs">
            <div className="flex items-center gap-1.5">
              <ClockIcon className="h-3.5 w-3.5 text-pf-text-tertiary" />
              <span className="text-pf-text-secondary">
                {data.scheduleCount} schedule{data.scheduleCount !== 1 ? 's' : ''}
              </span>
            </div>
            <div className="flex items-center gap-1.5">
              <CheckCircleIcon className="h-3.5 w-3.5 text-pf-text-tertiary" />
              <span className="text-pf-text-secondary">
                {data.maintenanceCount} completed
              </span>
            </div>
            {data.printerCount > 0 && (
              <div className="flex items-center gap-1.5">
                <GearIcon className="h-3.5 w-3.5 text-pf-text-tertiary" />
                <span className="text-pf-text-secondary">
                  {data.printerCount} printer{data.printerCount !== 1 ? 's' : ''}
                </span>
              </div>
            )}
            {data.totalCost > 0 && (
              <div className="flex items-center gap-1.5">
                <TagIcon className="h-3.5 w-3.5 text-pf-text-tertiary" />
                <span className="text-pf-text-secondary">
                  ${data.totalCost.toFixed(2)}
                </span>
              </div>
            )}
          </div>

          {data.averageIntervalDays && (
            <p className="mt-2 text-xs text-pf-text-tertiary">
              Avg interval: {Math.round(data.averageIntervalDays)} days
            </p>
          )}

          {data.lastMaintenanceDate && (
            <p className="mt-1 text-xs text-pf-text-tertiary">
              Last: {format(data.lastMaintenanceDate, 'MMM d, yyyy')}
            </p>
          )}
        </div>
      </div>
    </Button>
  );
}

interface ComponentDetailPanelProps {
  data: ComponentMaintenanceData;
}

function ComponentDetailPanel({ data }: ComponentDetailPanelProps) {
  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-5">
      <div className="flex items-center gap-3 mb-4">
        <div className={`p-2 rounded-lg bg-pf-bg-2 ${getComponentColor(data)}`}>
          {getComponentIcon()}
        </div>
        <div>
          <h3 className="font-semibold text-pf-text-primary text-lg">
            {data.component}
          </h3>
          <p className="text-sm text-pf-text-tertiary">
            Component maintenance details
          </p>
        </div>
      </div>

      {/* Stats Grid */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 mb-6">
        <div className="bg-pf-bg-2 rounded-lg p-3 text-center">
          <p className="text-2xl font-bold text-pf-text-primary">{data.scheduleCount}</p>
          <p className="text-xs text-pf-text-tertiary">Schedules</p>
        </div>
        <div className="bg-pf-bg-2 rounded-lg p-3 text-center">
          <p className="text-2xl font-bold text-pf-text-primary">{data.maintenanceCount}</p>
          <p className="text-xs text-pf-text-tertiary">Completed</p>
        </div>
        <div className="bg-pf-bg-2 rounded-lg p-3 text-center">
          <p className="text-2xl font-bold text-pf-text-primary">{data.printerCount}</p>
          <p className="text-xs text-pf-text-tertiary">Printers</p>
        </div>
        <div className="bg-pf-bg-2 rounded-lg p-3 text-center">
          <p className="text-2xl font-bold text-pf-text-primary">
            ${data.totalCost.toFixed(0)}
          </p>
          <p className="text-xs text-pf-text-tertiary">Total Cost</p>
        </div>
      </div>

      {/* Active Schedules */}
      {data.schedules.length > 0 && (
        <div className="mb-6">
          <h4 className="font-medium text-pf-text-primary mb-3">
            Active Schedules ({data.schedules.length})
          </h4>
          <div className="space-y-2">
            {data.schedules.map((schedule) => (
              <div
                key={schedule.id}
                className="flex items-center justify-between p-3 bg-pf-bg-2 rounded-lg"
              >
                <div>
                  <p className="text-sm font-medium text-pf-text-primary">
                    {schedule.taskName}
                  </p>
                  {schedule.description && (
                    <p className="text-xs text-pf-text-tertiary mt-0.5">
                      {schedule.description}
                    </p>
                  )}
                </div>
                <div className="text-right">
                  <Badge
                    variant={schedule.priority >= 3 ? 'error' : 'default'}
                    className="text-xs"
                  >
                    P{schedule.priority}
                  </Badge>
                  <p className="text-xs text-pf-text-tertiary mt-1">
                    {schedule.intervalDays 
                      ? `Every ${schedule.intervalDays}d`
                      : schedule.intervalHours 
                        ? `Every ${schedule.intervalHours}h`
                        : 'Manual'
                    }
                  </p>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Recent Maintenance */}
      {data.recentLogs.length > 0 && (
        <div>
          <h4 className="font-medium text-pf-text-primary mb-3">
            Recent Maintenance
          </h4>
          <div className="space-y-2">
            {data.recentLogs.map((log) => (
              <div
                key={log.id}
                className="flex items-center justify-between p-3 bg-pf-bg-2 rounded-lg"
              >
                <div>
                  <p className="text-sm font-medium text-pf-text-primary">
                    {log.taskName}
                  </p>
                  {log.notes && (
                    <p className="text-xs text-pf-text-tertiary mt-0.5 truncate max-w-xs">
                      {log.notes}
                    </p>
                  )}
                </div>
                <div className="text-right">
                  <p className="text-xs text-pf-text-secondary">
                    {format(new Date(log.performedAt), 'MMM d, yyyy')}
                  </p>
                  {log.cost && (
                    <p className="text-xs text-pf-text-tertiary">
                      ${log.cost.toFixed(2)}
                    </p>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Empty State */}
      {data.schedules.length === 0 && data.recentLogs.length === 0 && (
        <div className="text-center py-8">
          <AlertIcon className="h-10 w-10 text-pf-text-tertiary mx-auto mb-3" />
          <p className="text-pf-text-secondary">No maintenance data for this component</p>
          <p className="text-xs text-pf-text-tertiary mt-1">
            Add schedules to start tracking
          </p>
        </div>
      )}
    </div>
  );
}

/**
 * Component maintenance tracker with selectable component cards
 */
export function ComponentMaintenanceTracker({
  componentData,
  isLoading,
  onComponentSelect,
  selectedComponent,
  className = '',
}: ComponentMaintenanceTrackerProps) {
  const [internalSelected, setInternalSelected] = useState<string | undefined>(selectedComponent);
  
  const selected = selectedComponent ?? internalSelected;
  const selectedData = componentData.find(c => c.component === selected);

  const handleSelect = (component: string) => {
    setInternalSelected(component);
    onComponentSelect?.(component);
  };

  if (isLoading) {
    return (
      <div className={`grid grid-cols-1 lg:grid-cols-3 gap-6 ${className}`}>
        <div className="lg:col-span-1 space-y-3">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="h-32 bg-pf-border/50 rounded-xl animate-pulse" />
          ))}
        </div>
        <div className="lg:col-span-2">
          <div className="h-96 bg-pf-border/50 rounded-xl animate-pulse" />
        </div>
      </div>
    );
  }

  if (componentData.length === 0) {
    return (
      <div className={`text-center py-12 ${className}`}>
        <GearIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-3" />
        <h3 className="font-medium text-pf-text-primary">No Component Data</h3>
        <p className="text-sm text-pf-text-tertiary mt-1">
          Add maintenance schedules with component categories to start tracking
        </p>
      </div>
    );
  }

  return (
    <div className={`grid grid-cols-1 lg:grid-cols-3 gap-6 ${className}`}>
      {/* Component List */}
      <div className="lg:col-span-1 space-y-3 max-h-[600px] overflow-y-auto pr-2">
        {componentData.map((data) => (
          <ComponentCard
            key={data.component}
            data={data}
            isSelected={selected === data.component}
            onSelect={() => handleSelect(data.component)}
          />
        ))}
      </div>

      {/* Detail Panel */}
      <div className="lg:col-span-2">
        {selectedData ? (
          <ComponentDetailPanel data={selectedData} />
        ) : (
          <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-8 text-center h-full flex flex-col items-center justify-center">
            <GearIcon className="h-12 w-12 text-pf-text-tertiary mb-3" />
            <h3 className="font-medium text-pf-text-primary">Select a Component</h3>
            <p className="text-sm text-pf-text-tertiary mt-1">
              Choose a component from the list to view details
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
