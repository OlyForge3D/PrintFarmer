/**
 * MaintenanceDashboardPage
 * 
 * Main maintenance dashboard showing fleet overview, printer grid, and priority alerts.
 * Provides comprehensive view of maintenance status across all printers.
 */

import React from 'react';
import { useNavigate } from 'react-router-dom';
import { PageTemplate } from '@/common/components/PageTemplate';
import { WrenchIcon, RefreshIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { FleetMaintenanceOverview } from '../components/FleetMaintenanceOverview';
import { MaintenanceStatusGrid } from '../components/MaintenanceStatusGrid';
import { MaintenancePriorityList } from '../components/MaintenancePriorityList';
import { useMaintenanceStats } from '../hooks/useMaintenanceStats';
import { useMaintenanceAlerts } from '../hooks/useMaintenanceAlerts';

/**
 * Main maintenance dashboard page component
 */
export function MaintenanceDashboardPage() {
  const navigate = useNavigate();
  
  // Fetch maintenance statistics
  const { stats, isLoading: statsLoading, error: statsError, refetch: refetchStats } = useMaintenanceStats();
  
  // Fetch active alerts
  const { 
    alerts, 
    isLoading: alertsLoading, 
    error: alertsError, 
    refetch: refetchAlerts 
  } = useMaintenanceAlerts({ activeOnly: true });

  const handleRefresh = () => {
    refetchStats();
    refetchAlerts();
  };

  const handlePrinterClick = (printerId: string) => {
    // Navigate to printer detail or maintenance history
    // For now, navigate to printers page
    navigate(`/printers?selected=${printerId}`);
  };

  return (
    <PageTemplate
      title="Maintenance Dashboard"
      subtitle="Monitor and manage maintenance across your printer fleet"
      icon={WrenchIcon}
      actions={
        <Button
          variant="secondary"
          size="sm"
          onClick={handleRefresh}
          disabled={statsLoading || alertsLoading}
          className="gap-2"
        >
          <RefreshIcon 
            className={`h-4 w-4 ${(statsLoading || alertsLoading) ? 'animate-spin' : ''}`} 
            aria-hidden="true"
          />
          Refresh
        </Button>
      }
    >
      <div className="space-y-8">
        {/* Fleet Overview Section */}
        <section aria-labelledby="fleet-overview-heading">
          <h2 id="fleet-overview-heading" className="sr-only">
            Fleet Overview
          </h2>
          <FleetMaintenanceOverview
            stats={stats}
            isLoading={statsLoading}
            error={statsError}
          />
        </section>

        {/* Main Content Grid */}
        <div className="grid grid-cols-1 xl:grid-cols-3 gap-8">
          {/* Priority Alerts - Takes 1 column on XL, full width on smaller */}
          <section 
            className="xl:col-span-1"
            aria-labelledby="priority-alerts-heading"
          >
            <div className="bg-pf-panel border border-pf-border rounded-xl">
              <div className="px-5 py-4 border-b border-pf-border">
                <h2 
                  id="priority-alerts-heading" 
                  className="text-lg font-semibold text-pf-text-primary"
                >
                  Priority Alerts
                </h2>
                <p className="text-sm text-pf-text-tertiary mt-1">
                  {alerts.length > 0 
                    ? `${alerts.length} alert${alerts.length !== 1 ? 's' : ''} requiring attention`
                    : 'No active alerts'
                  }
                </p>
              </div>
              <div className="p-5 max-h-[600px] overflow-y-auto">
                <MaintenancePriorityList
                  alerts={alerts}
                  isLoading={alertsLoading}
                  onActionComplete={handleRefresh}
                  maxItems={10}
                  compact
                />
              </div>
            </div>
          </section>

          {/* Printer Status Grid - Takes 2 columns on XL */}
          <section 
            className="xl:col-span-2"
            aria-labelledby="printer-status-heading"
          >
            <div className="bg-pf-panel border border-pf-border rounded-xl">
              <div className="px-5 py-4 border-b border-pf-border">
                <h2 
                  id="printer-status-heading" 
                  className="text-lg font-semibold text-pf-text-primary"
                >
                  Printer Status
                </h2>
                <p className="text-sm text-pf-text-tertiary mt-1">
                  {stats 
                    ? `${stats.totalPrinters} printer${stats.totalPrinters !== 1 ? 's' : ''} in fleet`
                    : 'Loading...'
                  }
                </p>
              </div>
              <div className="p-5">
                <MaintenanceStatusGrid
                  printers={stats?.printerStatuses || []}
                  isLoading={statsLoading}
                  onPrinterClick={handlePrinterClick}
                />
              </div>
            </div>
          </section>
        </div>

        {/* Error display */}
        {(statsError || alertsError) && (
          <div className="bg-red-500/10 border border-red-500/30 rounded-xl p-4">
            <p className="text-sm text-red-400">
              {statsError?.message || alertsError?.message || 'An error occurred loading maintenance data'}
            </p>
          </div>
        )}
      </div>
    </PageTemplate>
  );
}
