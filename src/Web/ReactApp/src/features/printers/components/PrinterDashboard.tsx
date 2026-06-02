/**
 * PrinterDashboard Component
 * 
 * Main dashboard page showing printer farm overview with stats,
 * active jobs, recent prints, and actionable alerts.
 */

import React from 'react';
import { Link } from 'react-router';
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
import { PageTemplate } from '@/common/components/PageTemplate';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { TasksWidget } from '@/features/tasks';
import { ActiveJobsWidget } from './ActiveJobsWidget';
import { RecentPrintsWidget } from './RecentPrintsWidget';
import { CriticalAlertsBanner } from './CriticalAlertsBanner';
import { BackgroundServicesWidget } from '@/features/admin/components/BackgroundServicesWidget';
import { MaintenanceAlertsWidget } from '@/features/maintenance/components/MaintenanceAlertsWidget';
import { FarmCostSummaryWidget } from './FarmCostSummaryWidget';
import { usePageTour } from '@/common/hooks/usePageTour';
import { dashboardTour } from '@/features/printers/tours/dashboard.tour';
import { HelpButton } from '@/common/components/HelpButton';

interface StatsCardProps {
  title: string;
  value: number;
  icon: React.ComponentType<{ className?: string }>;
  color: 'total' | 'online' | 'printing' | 'paused' | 'gray';
  /** Optional link — wraps the card in a clickable link */
  linkTo?: string;
}

function StatsCard({ title, value, icon: Icon, color, linkTo }: StatsCardProps) {
  const isActive = value > 0;
  const inactiveClasses = 'bg-pf-status-idle-bg text-pf-status-idle-text border border-pf-status-idle-border';
  const colorClasses: Record<StatsCardProps['color'], string> = {
    total: isActive
      ? 'bg-pf-success-bg text-pf-success border border-pf-success'
      : inactiveClasses,
    online: isActive
      ? 'bg-pf-status-online-bg text-pf-status-online-text border border-pf-status-online-border'
      : inactiveClasses,
    printing: isActive
      ? 'bg-pf-status-printing-bg text-pf-status-printing-text border border-pf-status-printing-border'
      : inactiveClasses,
    paused: isActive
      ? 'bg-pf-status-paused-bg text-pf-status-paused-text border border-pf-status-paused-border'
      : inactiveClasses,
    gray: 'bg-pf-bg-2 text-pf-text-secondary border border-pf-border',
  };

  const card = (
    <div className={`bg-pf-bg-1 overflow-hidden border border-pf-border rounded-xl shadow-lg ${linkTo ? 'hover:border-pf-accent transition-colors cursor-pointer' : ''}`}>
      <div className="p-5">
        <div className="flex items-center">
          <div className="shrink-0">
            <div className={`dashboard-stat-icon dashboard-stat-icon-${color} p-3 rounded-md ${colorClasses[color]}`}>
              <Icon className="h-6 w-6" />
            </div>
          </div>
          <div className="ml-5 min-w-0 flex-1">
            <dl>
              <dt className="text-sm font-medium text-pf-text-tertiary uppercase tracking-wide">{title}</dt>
              <dd className="text-lg font-bold text-pf-text-primary">{value}</dd>
            </dl>
          </div>
        </div>
      </div>
    </div>
  );

  if (linkTo) {
    return <Link to={linkTo} aria-label={`${title}: ${value}`}>{card}</Link>;
  }

  return card;
}

export const PrinterDashboard: React.FC = () => {
  const { data: printers, isLoading, error } = usePrinters();
  const displayPrinters = usePrinterDisplays(printers || []);
  const { startTour } = usePageTour({ tourId: 'dashboard', steps: dashboardTour });

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
      titleActions={<HelpButton onClick={startTour} />}
    >
      {/* Stats Cards */}
      <div data-tour="stats-cards" className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4 mb-8">
        <StatsCard title="Total Printers" value={stats.total} color="total" icon={PrinterIcon} />
        <StatsCard title="Online" value={stats.online} color="online" icon={CheckCircleIcon} />
        <StatsCard title="Printing" value={stats.printing} color="printing" icon={PlayIcon} />
        <StatsCard title="Paused" value={stats.paused} color="paused" icon={PauseIcon} />
        <StatsCard title="Offline" value={stats.offline} color="gray" icon={SettingsIcon} />
        <StatsCard
          title="In Maintenance"
          value={stats.maintenance}
          color="gray"
          icon={WrenchIcon}
          linkTo={stats.maintenance > 0 ? '/maintenance' : undefined}
        />
      </div>

      {/* Critical Alerts — only shows when actionable items exist */}
      <CriticalAlertsBanner />

      {/* Loading State */}
      {isLoading ? (
        <div role="status" aria-label="Printers loading" aria-busy="true">
          <Skeleton lines={3} height="1.5rem" aria-label="Loading printers" />
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
          {/* Row 1: Tasks */}
          <div data-tour="tasks-widget">
            <TasksWidget />
          </div>

          {/* Row 2: Active Jobs + Recent Prints + Farm Cost */}
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            <div data-tour="active-jobs">
              <ActiveJobsWidget />
            </div>
            <div data-tour="recent-prints">
              <RecentPrintsWidget />
            </div>
            <FarmCostSummaryWidget />
          </div>

          {/* Row 3: Maintenance Alerts + Background Services */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            <div data-tour="services-widget">
              <BackgroundServicesWidget maxServices={8} />
            </div>
            <MaintenanceAlertsWidget />
          </div>
        </div>
      )}
    </PageTemplate>
  );
};

export default PrinterDashboard;
