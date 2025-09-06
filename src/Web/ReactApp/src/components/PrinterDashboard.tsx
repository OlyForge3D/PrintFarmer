import React, { useMemo, useState } from 'react';
import { usePrinters, useDeletePrinter } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { ExpandablePrinterCard } from './ExpandablePrinterCard';
import { EditPrinterModal } from './EditPrinterModal';
import { AddPrinterButton } from './AddPrinterButton';
import { PrinterDiscoveryModal } from './PrinterDiscoveryModal';
import { DeleteConfirmationModal } from './DeleteConfirmationModal';
import { SystemHealth } from './SystemHealth';
import type { Printer } from '@/types/api';
import { 
  Printer as PrinterIcon, 
  CheckCircle, 
  Play, 
  Pause,
  Search,
  Settings
} from 'lucide-react';

interface StatsCardProps {
  title: string;
  value: number;
  icon: React.ComponentType<{ className?: string }>;
  color: 'blue' | 'green' | 'yellow' | 'gray';
}

function StatsCard({ title, value, icon: Icon, color }: StatsCardProps) {
  const colorClasses = {
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

export function PrinterDashboard() {
  const { 
    data: printers, 
    isLoading, 
    error,
    refetch: refetchPrinters
  } = usePrinters();
  const deletePrinterMutation = useDeletePrinter();
  const [showDiscovery, setShowDiscovery] = useState(false);
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    isOpen: boolean;
    printer?: Printer;
  }>({ isOpen: false });
  
  const { getPrinterStatus } = usePrinterStatusUpdates();

  // Filter printers for the current user (for now show all printers since userId isn't on Printer)
  const userPrinters = useMemo(() => {
    return printers || [];
  }, [printers]);

  // Statistics calculations
  const stats = useMemo(() => {
    const total = userPrinters.length;
    const online = userPrinters.filter(p => {
      const status = getPrinterStatus(p.id);
      return status?.state?.toLowerCase().includes('operational') || 
             status?.state?.toLowerCase().includes('ready') ||
             status?.state?.toLowerCase().includes('idle') ||
             p.isOnline;
    }).length;
    const printing = userPrinters.filter(p => {
      const status = getPrinterStatus(p.id);
      return status?.state?.toLowerCase().includes('printing') ||
             p.state?.toLowerCase().includes('printing');
    }).length;
    const paused = userPrinters.filter(p => {
      const status = getPrinterStatus(p.id);
      return status?.state?.toLowerCase().includes('paused') ||
             p.state?.toLowerCase().includes('paused');
    }).length;

    return {
      total,
      online,
      printing,
      paused,
      offline: total - online
    };
  }, [userPrinters, getPrinterStatus]);

  const handleDeleteClick = (printer: Printer) => {
    setDeleteConfirmation({ isOpen: true, printer });
  };

  const handleDeleteConfirm = async () => {
    if (deleteConfirmation.printer) {
      await deletePrinterMutation.mutateAsync(deleteConfirmation.printer.id);
      setDeleteConfirmation({ isOpen: false });
    }
  };

  const handleDeleteCancel = () => {
    setDeleteConfirmation({ isOpen: false });
  };

  if (isLoading) {
    return (
      <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="animate-pulse" role="status" aria-busy="true">
            <span className="sr-only">Loading printers...</span>
            <div className="h-8 bg-pf-bg-1 rounded w-48 mb-4"></div>
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-8">
              {Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="h-24 bg-pf-bg-1 rounded-xl"></div>
              ))}
            </div>
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
              {Array.from({ length: 6 }).map((_, i) => (
                <div key={i} className="h-80 bg-pf-bg-1 rounded-xl"></div>
              ))}
            </div>
          </div>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center">
            <h2 className="text-2xl font-bold text-pf-text-primary mb-4">Error Loading Printers</h2>
            <p className="text-pf-text-secondary mb-4">{error.message}</p>
            <button
              onClick={() => refetchPrinters()}
              className="px-4 py-2 bg-pf-primary-500 text-white rounded-lg hover:bg-pf-primary-600 transition-colors"
            >
              Retry
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between mb-8">
          <div>
            <h1 className="text-3xl font-bold text-pf-text-primary">Printer Dashboard</h1>
            <p className="text-pf-text-secondary mt-1">Monitor and manage your 3D printers</p>
          </div>
          
          <div className="flex items-center space-x-3">
            <button
              onClick={() => {
                console.log('Discover button clicked - setting showDiscovery to true');
                setShowDiscovery(true);
              }}
              className="flex items-center space-x-2 px-4 py-2 bg-pf-bg-1 border border-pf-border text-pf-text-primary rounded-lg hover:bg-pf-bg-2 transition-colors"
            >
              <Search className="h-4 w-4" />
              <span>Discover</span>
            </button>
            
            <AddPrinterButton onSuccess={refetchPrinters} />
          </div>
        </div>

        {/* Statistics Cards */}
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
          <StatsCard title="Total Printers" value={stats.total} color="blue" icon={PrinterIcon} />
          <StatsCard title="Online" value={stats.online} color="green" icon={CheckCircle} />
          <StatsCard title="Printing" value={stats.printing} color="yellow" icon={Play} />
          <StatsCard title="Paused" value={stats.paused} color="yellow" icon={Pause} />
          <StatsCard title="Offline" value={stats.offline} color="gray" icon={Settings} />
        </div>

        <SystemHealth />

        {/* Printer Cards Grid */}
        <div className="space-y-6">
          {userPrinters.length === 0 ? (
            <div className="text-center py-12">
              <PrinterIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
              <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Printers Found</h3>
              <p className="text-pf-text-secondary mb-6">Get started by adding your first 3D printer.</p>
              <div className="flex justify-center space-x-4">
                <AddPrinterButton onSuccess={refetchPrinters} />
                <button
                  onClick={() => setShowDiscovery(true)}
                  className="flex items-center space-x-2 px-4 py-2 bg-pf-bg-1 border border-pf-border text-pf-text-primary rounded-lg hover:bg-pf-bg-2 transition-colors"
                >
                  <Search className="h-4 w-4" />
                  <span>Discover Printers</span>
                </button>
              </div>
            </div>
          ) : (
            <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-6">
              {userPrinters.map((printer) => (
                <ExpandablePrinterCard
                  key={printer.id}
                  printer={printer}
                  onDelete={() => handleDeleteClick(printer)}
                  onEdit={() => { setEditPrinterId(printer.id); setShowEditModal(true); }}
                />
              ))}
            </div>
          )}
        </div>

        {/* Modals */}
        <PrinterDiscoveryModal
          isOpen={showDiscovery}
          onClose={() => setShowDiscovery(false)}
          onSuccess={refetchPrinters}
        />        <DeleteConfirmationModal
          isOpen={deleteConfirmation.isOpen}
          printers={deleteConfirmation.printer ? [deleteConfirmation.printer] : []}
          onConfirm={handleDeleteConfirm}
          onCancel={handleDeleteCancel}
        />
        <EditPrinterModal
          printerId={editPrinterId}
          isOpen={showEditModal}
          onClose={() => setShowEditModal(false)}
          onSuccess={() => { setShowEditModal(false); refetchPrinters(); }}
        />
      </div>
    </div>
  );
}