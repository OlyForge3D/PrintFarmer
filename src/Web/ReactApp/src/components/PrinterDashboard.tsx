import React from 'react';
import { usePrintersWithCameraUrls } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { Printer as PrinterIcon, CheckCircle, Play, Pause, Settings } from 'lucide-react';
import { SystemHealth, DetailedSystemHealth } from '@/components/SystemHealth';
import { TEST_IDS, printerItemId, printerModelId, printerNameId } from '@/test/testIds';

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

  const stats = React.useMemo(() => {
    const userPrinters = printers ?? [];
    const total = userPrinters.length;
    const online = userPrinters.filter(p => {
      const status = getPrinterStatus?.(p.id);
      const s = (status?.state ?? p.state ?? '') as string;
      return (s && (s.toLowerCase().includes('operational') || s.toLowerCase().includes('ready') || s.toLowerCase().includes('idle'))) || !!p.isOnline;
    }).length;
    const printing = userPrinters.filter(p => ((getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string).toLowerCase().includes('printing')).length;
    const paused = userPrinters.filter(p => ((getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string).toLowerCase().includes('paused')).length;
    return { total, online, printing, paused, offline: total - online };
  }, [printers, getPrinterStatus]);

  return (
    <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <h1 className="text-3xl font-bold text-pf-text-primary mb-4">Printer Dashboard</h1>

        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
          <StatsCard title="Total Printers" value={stats.total} color="blue" icon={PrinterIcon} />
          <StatsCard title="Online" value={stats.online} color="green" icon={CheckCircle} />
          <StatsCard title="Printing" value={stats.printing} color="yellow" icon={Play} />
          <StatsCard title="Paused" value={stats.paused} color="yellow" icon={Pause} />
          <StatsCard title="Offline" value={stats.offline} color="gray" icon={Settings} />
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
          <div>
            <SystemHealth />
            <div className="mt-8">
              <DetailedSystemHealth />
            </div>

            {printers && printers.length > 0 && (
              <div data-testid={TEST_IDS.PRINTERS_LIST} role="list" aria-label="Printers list" className="mt-6 space-y-4">
                {printers.map(p => (
                  <div data-testid={printerItemId(p.id)} key={p.id} role="listitem" aria-label={`Printer ${p.name}`} className="p-4 bg-pf-bg-1 rounded shadow">
                    <div data-testid={printerNameId(p.id)} className="font-medium">{p.name}</div>
                    <div data-testid={printerModelId(p.id)} className="text-sm text-pf-text-secondary">{`${p.manufacturerName ?? ''} ${p.modelName ?? ''}`.trim()}</div>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
};

export default PrinterDashboard;
