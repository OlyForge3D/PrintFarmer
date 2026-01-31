/**
 * PrinterDashboard Component
 * 
 * Main dashboard page showing printer farm overview with stats,
 * alerts, jobs, and system health widgets.
 */

import React from 'react';
import { usePrinters } from '@/common/hooks/useApi';
import { usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { 
  SettingsIcon, 
  PlayIcon, 
  PauseIcon, 
  PrinterIcon, 
  WrenchIcon, 
  CheckCircleIcon, 
  DashboardIcon 
} from '@/common/components/icons/MdiIcons';
import { DetailedSystemHealth } from '@/features/printers/components/SystemHealth';
import { PageTemplate } from '@/common/components/PageTemplate';
import { MaintenanceAlertsWidget, MaintenanceOverviewWidget } from '@/features/maintenance/components';
import { BackgroundServicesWidget } from '@/features/admin/components';
import { TasksWidget } from '@/features/tasks';
import { AlertsWidget } from './AlertsWidget';
import { ActiveJobsWidget } from './ActiveJobsWidget';
import { RecentPrintsWidget } from './RecentPrintsWidget';

interface StatsCardProps {
  title: string;
  value: number;
  icon: React.ComponentType<{ className?: string }>;
  color: 'blue' | 'green' | 'yellow' | 'gray';
}

function StatsCard({ title, value, icon: Icon, color }: StatsCardProps) {
  const colorClasses: Record<string, string> = {
    blue: 'bg-pf-loading text-pf-text-primary',
    green: 'bg-pf-status-online-bg text-pf-status-online-text',
    yellow: 'bg-pf-warning text-pf-text-primary',
    gray: 'bg-pf-border-medium text-pf-text-secondary',
  };

  return (
    <div className="bg-pf-bg-1 overflow-hidden border border-pf-border rounded-xl shadow-lg">
      <div className="p-5">
        <div className="flex items-center">
          <div className="shrink-0">
            <div className={`p-3 rounded-md ${colorClasses[color]}`}>
              <Icon className="h-6 w-6" />
            </div>
          </div>
          <div className="ml-5 w-0 flex-1">
            <dl>
              <dt className="text-sm font-medium text-pf-text-tertiary truncate uppercase tracking-wide">{title}</dt>
              <dd className="text-lg font-bold text-pf-text-primary">{value}</dd>
            </dl>
          </div>
        </div>
      </div>
    </div>
  );
}

export const PrinterDashboard: React.FC = () => {
  const { data: printers, isLoading, error } = usePrinters();
  const displayPrinters = usePrinterDisplays(printers || []);

  const stats = React.useMemo(() => {
    const userPrinters = displayPrinters ?? [];
    const total = userPrinters.length;
    const online = userPrinters.filter(p => p.isOnline).length;
    const printing = userPrinters.filter(p => (p.state ?? '').toLowerCase().includes('printing')).length;
    const paused = userPrinters.filter(p => (p.state ?? '').toLowerCase().includes('paused')).length;
    const maintenance = userPrinters.filter(p => p.inMaintenance).length;
    const offline = total - online;
    return { total, online, printing, paused, offline, maintenance };
  }, [displayPrinters]);

  return (
    <PageTemplate
      title="Printer Dashboard"
      subtitle="Overview of your 3D printer farm status"
      icon={DashboardIcon}
    >
      {/* Stats Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
        <StatsCard title="Total Printers" value={stats.total} color="blue" icon={PrinterIcon} />
        <StatsCard title="Online" value={stats.online} color="green" icon={CheckCircleIcon} />
        <StatsCard title="Printing" value={stats.printing} color="yellow" icon={PlayIcon} />
        <StatsCard title="Paused" value={stats.paused} color="yellow" icon={PauseIcon} />
        <StatsCard title="Offline" value={stats.offline} color="gray" icon={SettingsIcon} />
        {stats.maintenance > 0 && (
          <StatsCard title="In Maintenance" value={stats.maintenance} color="gray" icon={WrenchIcon} />
        )}
      </div>

      {/* Loading State */}
      {isLoading ? (
        <div role="status" aria-label="Printers loading">
          <div aria-label="Loading printer" className="h-6 bg-pf-loading rounded-sm mb-2 w-48" />
          <div aria-label="Loading printer" className="h-6 bg-pf-loading rounded-sm mb-2 w-56" />
          <div aria-label="Loading printer" className="h-6 bg-pf-loading rounded-sm mb-2 w-40" />
        </div>
      ) : error ? (
        /* Error State */
        <div className="p-4 bg-pf-bg-1 rounded-lg shadow-sm">
          <h2 className="text-lg font-semibold">Error Loading Printers</h2>
          {(() => {
            const e: unknown = error;
            if (e instanceof Error) return <p className="text-sm text-pf-error-text">{e.message}</p>;
            if (typeof e === 'string') return <p className="text-sm text-pf-error-text">{e}</p>;
            if (e && typeof e === 'object' && 'message' in (e as Record<string, unknown>)) {
              const msg = (e as Record<string, unknown>).message;
              if (typeof msg === 'string') return <p className="text-sm text-pf-error-text">{msg}</p>;
            }
            return <p className="text-sm text-pf-error-text">Unknown error</p>;
          })()}
        </div>
      ) : printers && printers.length === 0 ? (
        /* Empty State */
        <div className="p-8 text-center">
          <h2 className="text-xl font-semibold">No Printers Found</h2>
          <p className="text-sm mt-2">Get started by adding your first 3D printer.</p>
        </div>
      ) : (
        /* Main Dashboard Content */
        <div className="space-y-6">
          {/* Row 1: Alerts and Pending Tasks */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            <AlertsWidget />
            <TasksWidget />
          </div>

          {/* Row 2: Active Jobs and Recent Prints */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            <ActiveJobsWidget />
            <RecentPrintsWidget />
          </div>

          {/* Row 3: Maintenance Widgets */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            <MaintenanceAlertsWidget maxAlerts={3} />
            <MaintenanceOverviewWidget />
          </div>

          {/* Row 4: System Health and Services */}
          <div className="mt-8 grid grid-cols-1 lg:grid-cols-2 gap-6">
            <BackgroundServicesWidget maxServices={5} />
            <DetailedSystemHealth />
          </div>
        </div>
      )}
    </PageTemplate>
  );
};

export default PrinterDashboard;
