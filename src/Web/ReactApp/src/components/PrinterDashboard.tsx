import React from 'react';
import { usePrintersWithCameraUrls, useJobQueue, usePrinterHistory } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { Printer as PrinterIcon, CheckCircle, Play, Pause, Settings, LayoutDashboard, AlertCircle, Wrench, TrendingUp } from 'lucide-react';
import { DetailedSystemHealth } from '@/components/SystemHealth';
import { PageTemplate } from '@/components/PageTemplate';

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
          <div className="flex-shrink-0">
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
  const { data: printers, isLoading, error } = usePrintersWithCameraUrls();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  
  // Fetch global job queue for active jobs
  const { data: globalQueue } = useJobQueue(undefined, { enabled: !isLoading && printers && printers.length > 0 });
  
  // Fetch history for the first printer (or any printer) to show recent prints
  const firstPrinterId = printers?.[0]?.id;
  const { data: recentHistory } = usePrinterHistory(
    firstPrinterId || '',
    { limit: 5, order: 'desc' },
    { enabled: !!firstPrinterId && !isLoading }
  );

  const stats = React.useMemo(() => {
    const userPrinters = printers ?? [];
    const total = userPrinters.length;
    const online = userPrinters.filter(p => {
      const status = getPrinterStatus?.(p.id);
      if (status) {
        const s = status.state ?? '';
        return (s && (s.toLowerCase().includes('operational') || s.toLowerCase().includes('ready') || s.toLowerCase().includes('idle'))) || status.isOnline;
      }
      const s = (p.state ?? '') as string;
      return s && (s.toLowerCase().includes('operational') || s.toLowerCase().includes('ready') || s.toLowerCase().includes('idle'));
    }).length;
    const printing = userPrinters.filter(p => ((getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string).toLowerCase().includes('printing')).length;
    const paused = userPrinters.filter(p => ((getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string).toLowerCase().includes('paused')).length;
    const maintenance = userPrinters.filter(p => p.inMaintenance).length;
    const offline = total - online;
    return { total, online, printing, paused, offline, maintenance };
  }, [printers, getPrinterStatus]);

  return (
    <PageTemplate
      title="Printer Dashboard"
      subtitle="Overview of your 3D printer farm status"
      icon={LayoutDashboard}
      maxWidth="max-w-7xl"
    >
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
          <StatsCard title="Total Printers" value={stats.total} color="blue" icon={PrinterIcon} />
          <StatsCard title="Online" value={stats.online} color="green" icon={CheckCircle} />
          <StatsCard title="Printing" value={stats.printing} color="yellow" icon={Play} />
          <StatsCard title="Paused" value={stats.paused} color="yellow" icon={Pause} />
          <StatsCard title="Offline" value={stats.offline} color="gray" icon={Settings} />
          {stats.maintenance > 0 && (
            <StatsCard title="In Maintenance" value={stats.maintenance} color="gray" icon={Wrench} />
          )}
        </div>

        {isLoading ? (
          <div role="status" aria-label="Printers loading">
            <div aria-label="Loading printer" className="h-6 bg-pf-loading rounded mb-2 w-48" />
            <div aria-label="Loading printer" className="h-6 bg-pf-loading rounded mb-2 w-56" />
            <div aria-label="Loading printer" className="h-6 bg-pf-loading rounded mb-2 w-40" />
          </div>
        ) : error ? (
          <div className="p-4 bg-pf-bg-1 rounded-lg shadow">
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
          <div className="p-8 text-center">
            <h2 className="text-xl font-semibold">No Printers Found</h2>
            <p className="text-sm mt-2">Get started by adding your first 3D printer.</p>
          </div>
        ) : (
          <div className="space-y-6">
            {/* Alerts Panel */}
            {stats.offline > 0 || stats.maintenance > 0 ? (
              <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6 shadow">
                <div className="flex items-center gap-2 mb-4">
                  <AlertCircle className="h-5 w-5 text-pf-error-text" />
                  <h2 className="text-lg font-semibold text-pf-text-primary">Alerts</h2>
                </div>
                <div className="space-y-3">
                  {stats.offline > 0 && (
                    <div className="flex items-start gap-2 p-3 bg-pf-error-bg rounded border border-pf-error-border">
                      <AlertCircle className="h-4 w-4 text-pf-error-text flex-shrink-0 mt-0.5" />
                      <div>
                        <p className="text-sm font-medium text-pf-error-text">{stats.offline} Printer{stats.offline > 1 ? 's' : ''} Offline</p>
                        <p className="text-xs text-pf-error-text opacity-80">Check network connection and printer status</p>
                      </div>
                    </div>
                  )}
                  {stats.maintenance > 0 && (
                    <div className="flex items-start gap-2 p-3 bg-pf-warning-bg rounded border border-pf-warning-border">
                      <Wrench className="h-4 w-4 text-pf-warning-text flex-shrink-0 mt-0.5" />
                      <div>
                        <p className="text-sm font-medium text-pf-warning-text">{stats.maintenance} Printer{stats.maintenance > 1 ? 's' : ''} in Maintenance</p>
                        <p className="text-xs text-pf-warning-text opacity-80">These printers are not available for printing</p>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            ) : null}

            {/* Active Jobs Widget */}
            {globalQueue && globalQueue.length > 0 ? (
              <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6 shadow">
                <div className="flex items-center gap-2 mb-4">
                  <Play className="h-5 w-5 text-pf-loading" />
                  <h2 className="text-lg font-semibold text-pf-text-primary">Active & Queued Jobs</h2>
                </div>
                <div className="space-y-3 max-h-64 overflow-y-auto">
                  {globalQueue.slice(0, 5).map((job, idx) => (
                    <div key={job.id} className="flex items-start justify-between p-3 bg-pf-bg-2 rounded border border-pf-border">
                      <div className="flex-1 min-w-0">
                        <p className="text-sm font-medium text-pf-text-primary truncate">{job.gcodeFileName}</p>
                        <p className="text-xs text-pf-text-tertiary">Queue Position: {idx + 1}</p>
                      </div>
                      <span className="ml-2 inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-pf-loading text-pf-text-primary whitespace-nowrap">
                        Queued
                      </span>
                    </div>
                  ))}
                  {globalQueue.length > 5 && (
                    <p className="text-xs text-pf-text-tertiary text-center py-2">+{globalQueue.length - 5} more in queue</p>
                  )}
                </div>
              </div>
            ) : null}

            {/* Recent Print History Widget */}
            {recentHistory && recentHistory.jobs && recentHistory.jobs.length > 0 ? (
              <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6 shadow">
                <div className="flex items-center gap-2 mb-4">
                  <TrendingUp className="h-5 w-5 text-pf-status-online-text" />
                  <h2 className="text-lg font-semibold text-pf-text-primary">Recent Prints</h2>
                </div>
                <div className="space-y-3 max-h-64 overflow-y-auto">
                  {recentHistory.jobs.slice(0, 5).map((job) => (
                    <div key={job.id} className="flex items-start justify-between p-3 bg-pf-bg-2 rounded border border-pf-border">
                      <div className="flex-1 min-w-0">
                        <p className="text-sm font-medium text-pf-text-primary truncate">{job.jobName}</p>
                        <p className="text-xs text-pf-text-tertiary">
                          {job.status === 'Success' ? '✓ Completed' : job.status === 'Failed' ? '✗ Failed' : job.status}
                        </p>
                      </div>
                      <div className="ml-2 text-right">
                        <p className="text-xs font-medium text-pf-text-secondary">
                          {job.printTime ? Math.floor((job.printTime ?? 0) / 60) : 0}m
                        </p>
                        <span className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium whitespace-nowrap ${
                          job.status === 'Success' 
                            ? 'bg-pf-status-online-bg text-pf-status-online-text' 
                            : job.status === 'Failed' 
                            ? 'bg-pf-error-bg text-pf-error-text'
                            : 'bg-pf-border-medium text-pf-text-secondary'
                        }`}>
                          {job.status}
                        </span>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            ) : null}

            {/* System Health */}
            <div className="mt-8">
              <DetailedSystemHealth />
            </div>
          </div>
        )}
    </PageTemplate>
  );
};

export default PrinterDashboard;
