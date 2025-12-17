import React from 'react';
import { usePrintersWithCameraUrls, useDeletePrinter } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { Printer as PrinterIcon, CheckCircle, Play, Pause, Settings, LayoutDashboard, Edit, Trash2, Grid2x2, Grid3x3, List } from 'lucide-react';
import { ImagePlaceholder } from '@/components/icons';
import { toast } from 'sonner';
import type { Printer } from '@/types/api';
import { EditPrinterModal } from '@/components/EditPrinterModal';
import { DeleteConfirmationModal } from '@/components/DeleteConfirmationModal';
import { ExpandablePrinterCard } from '@/components/ExpandablePrinterCard';
import { PrinterCompactCard } from '@/components/PrinterCompactCard';
import { PrinterTableView } from '@/components/PrinterTableView';
import { DetailedSystemHealth } from '@/components/SystemHealth';
import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui';

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
  const deletePrinterMutation = useDeletePrinter();

  const [editPrinterId, setEditPrinterId] = React.useState<string | null>(null);
  const [showEditModal, setShowEditModal] = React.useState(false);
  const [deleteConfirmation, setDeleteConfirmation] = React.useState<{ isOpen: boolean; printers: Printer[] }>({ isOpen: false, printers: [] });
  const [viewMode, setViewMode] = React.useState<'compact' | 'expandable' | 'table'>(() => {
    // Load from localStorage if available
    const saved = localStorage.getItem('printerViewMode');
    return (saved as 'compact' | 'expandable' | 'table') || 'table';
  });

  // Save view mode preference to localStorage
  React.useEffect(() => {
    localStorage.setItem('printerViewMode', viewMode);
  }, [viewMode]);

  const stats = React.useMemo(() => {
    const userPrinters = printers ?? [];
    const total = userPrinters.length;
    const online = userPrinters.filter(p => {
      const status = getPrinterStatus?.(p.id);
      // If we have real-time status from SignalR, use it. Otherwise use cached state.
      if (status) {
        const s = status.state ?? '';
        return (s && (s.toLowerCase().includes('operational') || s.toLowerCase().includes('ready') || s.toLowerCase().includes('idle'))) || status.isOnline;
      }
      // Fallback to cached state if no real-time status yet
      const s = (p.state ?? '') as string;
      return s && (s.toLowerCase().includes('operational') || s.toLowerCase().includes('ready') || s.toLowerCase().includes('idle'));
    }).length;
    const printing = userPrinters.filter(p => ((getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string).toLowerCase().includes('printing')).length;
    const paused = userPrinters.filter(p => ((getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string).toLowerCase().includes('paused')).length;
    return { total, online, printing, paused, offline: total - online };
  }, [printers, getPrinterStatus]);

  const handleEditPrinter = (printerId: string) => {
    setEditPrinterId(printerId);
    setShowEditModal(true);
  };

  const handleDeleteSinglePrinter = (printer: Printer) => {
    setDeleteConfirmation({ isOpen: true, printers: [printer] });
    toast(`Delete: "${printer.name}" — confirm to proceed`, { duration: 3000 });
  };

  const handleDeleteConfirm = async () => {
    try {
      await Promise.all(deleteConfirmation.printers.map((printer) => deletePrinterMutation.mutateAsync(printer.id)));
      setDeleteConfirmation({ isOpen: false, printers: [] });
    } catch (err) {
      // swallow - toast handled in hook
      console.error('Failed to delete printers', err);
    }
  };

  const handleDeleteCancel = () => setDeleteConfirmation({ isOpen: false, printers: [] });

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
            <div className="mt-8">
              <DetailedSystemHealth />
            </div>

            {/* Printers list - accessible list with test ids for tests */}
            {printers && printers.length > 0 && (
              <div className="mt-6">
                {/* View Mode Toggle */}
                <div className="mb-6 flex items-center gap-2">
                  <span className="text-sm font-medium text-pf-text-secondary">View:</span>
                  <div className="flex gap-1 bg-pf-bg-1 p-1 rounded-lg border border-pf-border">
                    <Button
                      variant={viewMode === 'table' ? 'primary' : 'subtle'}
                      size="sm"
                      onClick={() => setViewMode('table')}
                      title="Table View"
                      className="!p-2"
                    >
                      <List className="w-4 h-4" />
                    </Button>
                    <Button
                      variant={viewMode === 'compact' ? 'primary' : 'subtle'}
                      size="sm"
                      onClick={() => setViewMode('compact')}
                      title="Compact Cards"
                      className="!p-2"
                    >
                      <Grid2x2 className="w-4 h-4" />
                    </Button>
                    <Button
                      variant={viewMode === 'expandable' ? 'primary' : 'subtle'}
                      size="sm"
                      onClick={() => setViewMode('expandable')}
                      title="Expandable Cards"
                      className="!p-2"
                    >
                      <Grid3x3 className="w-4 h-4" />
                    </Button>
                  </div>
                </div>

                {/* Expandable Cards View */}
                {viewMode === 'expandable' && (
                  <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
                    {printers.map((p) => (
                      <ExpandablePrinterCard
                        key={p.id}
                        printer={p}
                        onEdit={() => handleEditPrinter(p.id)}
                      />
                    ))}
                  </div>
                )}

                {/* Compact Cards View */}
                {viewMode === 'compact' && (
                  <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
                    {printers.map((p) => (
                      <PrinterCompactCard
                        key={p.id}
                        printer={p}
                        onEdit={(printer) => handleEditPrinter(printer.id)}
                        onDelete={handleDeleteSinglePrinter}
                        getPrinterStatus={getPrinterStatus}
                      />
                    ))}
                  </div>
                )}

                {/* Table View */}
                {viewMode === 'table' && (
                  <PrinterTableView
                    printers={printers}
                    onEdit={(printer) => handleEditPrinter(printer.id)}
                    onDelete={(printersToDelete) => {
                      setDeleteConfirmation({ isOpen: true, printers: printersToDelete });
                      toast(`Delete: ${printersToDelete.length} printer${printersToDelete.length > 1 ? 's' : ''} — confirm to proceed`, { duration: 3000 });
                    }}
                    onBulkSetMaintenance={() => {
                      // Placeholder for maintenance mode functionality
                      toast('Maintenance mode not yet implemented', { duration: 3000 });
                    }}
                  />
                )}
              </div>
            )}

            {/* Edit/Delete Modals for inline actions */}
            {showEditModal && (
              <EditPrinterModal
                printerId={editPrinterId}
                isOpen={showEditModal}
                onClose={() => setShowEditModal(false)}
                onSuccess={() => { setShowEditModal(false); /* optionally refetch printers via parent if available */ }}
              />
            )}
            {deleteConfirmation.isOpen && (
              <DeleteConfirmationModal
                isOpen={deleteConfirmation.isOpen}
                printers={deleteConfirmation.printers}
                onConfirm={handleDeleteConfirm}
                onCancel={handleDeleteCancel}
              />
            )}
          </div>
        )}
    </PageTemplate>
  );
};

export default PrinterDashboard;
