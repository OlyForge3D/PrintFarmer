import React, { useMemo, useState } from 'react';
import { usePrinters, useDeletePrinter } from '@/common/hooks/useApi';
import { usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { useQueryClient } from '@tanstack/react-query';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { CollapsedPrinterCard } from '@/features/printers/components/CollapsedPrinterCard';
import { PrinterDetailsSidebar } from '@/features/printers/components/PrinterDetailsSidebar';
import { PrinterTableView } from '@/features/printers/components/PrinterTableView';
import { EditPrinterModal } from '@/features/printers/components/EditPrinterModal';
import { AddPrinterButton } from '@/features/printers/components/AddPrinterButton';
import { PrinterDiscoveryModal } from '@/features/printers/components/PrinterDiscoveryModal';
import ImportProgressModal from '@/features/printers/components/ImportProgressModal';
import { DeleteConfirmationModal } from '@/common/components/modals/DeleteConfirmationModal';
import { PrinterCardSkeleton } from '@/common/components/skeletons/PrinterCardSkeleton';
import { DetailedPrinterCard } from '@/features/printers/components/DetailedPrinterCard';
import { PrinterCompactCard } from '@/features/printers/components/PrinterCompactCard';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { ViewModeToggle } from '@/common/components/ViewModeToggle';
import type { Printer } from '@/types/api';
import { PrinterBackend } from '@/types/api';

import { PrinterIcon, FileImportIcon, FileExportIcon, PrinterSearchIcon } from '@/common/components/icons/MdiIcons';


type PrinterStateFilter = 'all' | 'online' | 'printing' | 'paused' | 'offline';
type BackendFilter = 'all' | 'Moonraker' | 'PrusaLink' | 'SDCP' | 'OctoPrint';
type ViewMode = 'collapsed' | 'compact' | 'expandable' | 'table';

// Helper function to get backend name from enum value
function getBackendName(backend: PrinterBackend | string | number): string {
  if (typeof backend === 'string') return backend;
  // Map enum value to name by looking up the enum
  return PrinterBackend[backend as number] || '';
}

export function PrintersPage() {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const { 
    data: printers, 
    isLoading,
    refetch: refetchPrinters
  } = usePrinters();
  
  // Merge with realtime SignalR updates for display
  const displayPrinters = usePrinterDisplays(printers || []);
  
  const deletePrinterMutation = useDeletePrinter();
  const [viewMode, setViewMode] = useState<ViewMode>(() => {
    const saved = localStorage.getItem('printerViewMode');
    return (saved as ViewMode) || 'collapsed';
  });
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);
  const [expandedPrinterId, setExpandedPrinterId] = useState<string | null>(null);
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    isOpen: boolean;
    printers: Printer[];
  }>({ isOpen: false, printers: [] });

  // Discovery and Export state
  const [showDiscovery, setShowDiscovery] = useState(false);
  const [discoveryAvailable, setDiscoveryAvailable] = useState(false);
  const [showExportOptions, setShowExportOptions] = useState(false);
  const [exporting, setExporting] = useState<boolean>(false);
  const [, setExportProgress] = useState<number | null>(null); // exportProgress not currently used in UI

  // Import state
  const [showImportProgress, setShowImportProgress] = useState(false);
  const [importFileName, setImportFileName] = useState('');
  const [importTotalCount, setImportTotalCount] = useState(0);
  const fileInputRef = React.useRef<HTMLInputElement | null>(null);
  const fileInputId = React.useId();

  // Check if discovery service is available
  React.useEffect(() => {
    const checkDiscoveryAvailability = async () => {
      try {
        const settings = await apiClient.getSettings<import('@/types/NetworkDiscoverySettings').NetworkDiscoverySettings>('NetworkDiscovery');
        const isEnabled = settings?.enableDiscovery === true;
        const hasRecentHeartbeat = settings?.lastHeartbeat 
          ? new Date().getTime() - new Date(settings.lastHeartbeat).getTime() < 60000
          : false;
        setDiscoveryAvailable(isEnabled && hasRecentHeartbeat);
      } catch {
        setDiscoveryAvailable(false);
      }
    };

    checkDiscoveryAvailability();
    const interval = setInterval(checkDiscoveryAvailability, 30000);
    return () => clearInterval(interval);
  }, []);

  // Save view mode preference to localStorage
  React.useEffect(() => {
    localStorage.setItem('printerViewMode', viewMode);
  }, [viewMode]);

  // Filter state
  const [stateFilter, setStateFilter] = useState<PrinterStateFilter>('all');
  const [backendFilter, setBackendFilter] = useState<BackendFilter>('all');

  // Filter printers for the current user (for now show all printers since userId isn't on Printer)
  const userPrinters = useMemo(() => {
    let filtered = displayPrinters || [];
    // State filter
    if (stateFilter !== 'all') {
      filtered = filtered.filter(p => {
        const state = (p.state || '').toLowerCase();
        if (stateFilter === 'online') return p.isOnline;
        if (stateFilter === 'printing') return state.includes('printing');
        if (stateFilter === 'paused') return state.includes('paused');
        if (stateFilter === 'offline') return !p.isOnline;
        return true;
      });
    }
    // Backend filter
    if (backendFilter !== 'all') {
      filtered = filtered.filter(p => getBackendName(p.backend) === backendFilter);
    }
    return filtered;
  }, [displayPrinters, stateFilter, backendFilter]);



  const handleDeleteClick = (printers: Printer[]) => {
    setDeleteConfirmation({ isOpen: true, printers });
  };

  const handleDeleteSinglePrinter = (printer: Printer) => {
    setDeleteConfirmation({ isOpen: true, printers: [printer] });
  };

  const handleDeleteConfirm = async () => {
    try {
      await Promise.all(deleteConfirmation.printers.map(printer => 
        deletePrinterMutation.mutateAsync(printer.id)
      ));
      setDeleteConfirmation({ isOpen: false, printers: [] });
    } catch (error) {
      if (window.PrintFarmerDebug?.printers) {
        console.error('Failed to delete printers:', error);
      }
    }
  };

  const handleDeleteCancel = () => {
    setDeleteConfirmation({ isOpen: false, printers: [] });
  };

  const handleExport = () => {
    if (!printers || !printers.length) {
      toast('No printers to export');
      return;
    }
    setShowExportOptions(true);
  };

  const exportSelectedJson = async () => {
    if (!printers || printers.length === 0) return toast('No printers to export');
    const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.json`;
    try {
      setExporting(true);
      setExportProgress(0);
      await apiClient.streamExportFile(printers.map(p => p.id), 'json', filename, (loaded, total) => {
        if (total && total > 0) setExportProgress(Math.round((loaded / total) * 100));
        else setExportProgress(null);
      });
      toast.success('Printers exported (JSON)');
    } catch (err) {
      console.error('Export failed', err);
      toast.error('Export failed');
    } finally {
      setShowExportOptions(false);
      setExportProgress(null);
      setExporting(false);
    }
  };

  const exportSelectedCsv = async () => {
    if (!printers || printers.length === 0) return toast('No printers to export');
    const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.csv`;
    try {
      setExporting(true);
      setExportProgress(0);
      await apiClient.streamExportFile(printers.map(p => p.id), 'csv', filename, (loaded, total) => {
        if (total && total > 0) setExportProgress(Math.round((loaded / total) * 100));
        else setExportProgress(null);
      });
      toast.success('Printers exported (CSV)');
    } catch (err) {
      console.error('Export CSV failed', err);
      toast.error('Export failed');
    } finally {
      setShowExportOptions(false);
      setExportProgress(null);
      setExporting(false);
    }
  };

  const countPrintersInFile = async (file: File): Promise<number> => {
    try {
      const text = await file.text();
      const extension = file.name.split('.').pop()?.toLowerCase();
      
      if (extension === 'json') {
        const data = JSON.parse(text);
        return Array.isArray(data) ? data.length : 0;
      } else if (extension === 'csv') {
        const lines = text.split('\n').filter(line => line.trim());
        return Math.max(0, lines.length - 1);
      }
      return 0;
    } catch {
      return 0;
    }
  };

  const handleImportClick = () => {
    fileInputRef.current?.click();
  };

  const handleFile = async (file?: File) => {
    try {
      const f = file || (fileInputRef.current?.files ? fileInputRef.current.files[0] : undefined);
      if (!f) return;

      const extension = f.name.split('.').pop()?.toLowerCase();
      if (!['csv', 'json'].includes(extension || '')) {
        toast.error('File must be CSV or JSON format');
        return;
      }

      const printerCount = await countPrintersInFile(f);
      if (printerCount === 0) {
        toast.error('No printers found in file');
        return;
      }

      setImportFileName(f.name);
      setImportTotalCount(printerCount);
      setShowImportProgress(true);

      const formData = new FormData();
      formData.append('file', f);

      const response = await fetch(`${getApiBaseUrl()}/printers/import`, {
        method: 'POST',
        headers: getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: 'Unknown error' }));
        throw new Error(error.message || `HTTP ${response.status}`);
      }

      await response.json();
      
      await queryClient.invalidateQueries({ queryKey: ['printers'] });
    } catch (err) {
      console.error('Import failed', err);
      setShowImportProgress(false);
      toast.error(err instanceof Error ? err.message : 'Failed to import file');
    }
  };

  const handleEditPrinter = (printer: Printer) => {
    setEditPrinterId(printer.id);
    setShowEditModal(true);
  };


  const handleBulkSetMaintenance = async (printers: Printer[], inMaintenance: boolean) => {
    try {
      if (window.PrintFarmerDebug?.printers) {
        console.log(`Starting maintenance update for ${printers.length} printer(s), inMaintenance=${inMaintenance}`);
      }
      
      const results = await Promise.all(printers.map(async (printer) => {
        if (window.PrintFarmerDebug?.printers) {
          console.log(`Updating printer ${printer.id} (${printer.name}) to inMaintenance=${inMaintenance}`);
        }
        const response = await fetch(`${getApiBaseUrl()}/printers/${printer.id}/maintenance`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
          body: JSON.stringify(inMaintenance)
        });
        
        if (!response.ok) {
          const errorData = await response.text();
          if (window.PrintFarmerDebug?.printers) {
            console.error(`Failed to update maintenance for ${printer.id}:`, response.status, errorData);
          }
          throw new Error(`HTTP ${response.status}: ${errorData}`);
        }
        
        return response.json();
      }));
      
      if (window.PrintFarmerDebug?.printers) {
        console.log('Maintenance status updated successfully:', results);
        console.log('Refetching printer queries...');
      }
      await queryClient.refetchQueries({ queryKey: ['printers'] });
      if (window.PrintFarmerDebug?.printers) {
        console.log('Printers refetched, UI should update now');
      }
    } catch (error) {
      if (window.PrintFarmerDebug?.printers) {
        console.error('Failed to update maintenance status:', error);
      }
    }
  };

  if (isLoading) {
    return (
      <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
        <div className="mx-auto px-4 sm:px-6 lg:px-8" role="status" aria-busy="true">
          <div className="h-8 w-48 bg-pf-bg-1 rounded mb-6 animate-pulse" />
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="h-24 bg-pf-bg-1 rounded-xl animate-pulse" />
            ))}
          </div>
          <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-6">
            {Array.from({ length: 6 }).map((_, i) => (
              <PrinterCardSkeleton key={i} />
            ))}
          </div>
        </div>
      </div>
    );
  }

  return (
    <PageTemplate
      title="Printers"
      subtitle="Monitor and manage your 3D printer farm"
      icon={PrinterIcon}
    >
      <div className="flex flex-col md:flex-row md:items-center gap-4 mb-8">
        <div className="flex flex-col md:flex-row md:items-center gap-3">
          {/* State Filter */}
          <Select
            value={stateFilter}
            onChange={e => setStateFilter(e.target.value as PrinterStateFilter)}
            aria-label="Filter by printer state"
          >
            <option value="all">All States</option>
            <option value="online">Online</option>
            <option value="printing">Printing</option>
            <option value="paused">Paused</option>
            <option value="offline">Offline</option>
          </Select>
          {/* Backend Filter */}
          <Select
            value={backendFilter}
            onChange={e => setBackendFilter(e.target.value as BackendFilter)}
            aria-label="Filter by backend"
          >
            <option value="all">All Backends</option>
            <option value="Moonraker">Moonraker</option>
            <option value="PrusaLink">PrusaLink</option>
            <option value="SDCP">SDCP</option>
            <option value="OctoPrint">OctoPrint</option>
          </Select>
          {/* View Mode Toggle */}
          <ViewModeToggle viewMode={viewMode} onChange={setViewMode} />
          {hasPermission('printers', 'create') && (
            <AddPrinterButton onSuccess={refetchPrinters} />
          )}
          {hasPermission('printers', 'admin') && discoveryAvailable && (
            <Button
              variant="primary"
              aria-label="Trigger network discovery to find printers on local network"
              onClick={() => setShowDiscovery(true)}
              iconLeft={<PrinterSearchIcon className="w-4 h-4" ariaLabel="Discover" />}
            >
              Discover Printers
            </Button>
          )}
          {hasPermission('printers', 'admin') && (
            <Button 
              variant="primary" 
              aria-label="Export printers as JSON" 
              onClick={handleExport} 
              disabled={exporting}
              iconLeft={!exporting && <FileExportIcon className="w-4 h-4" ariaLabel="Export" />}
            >
              {exporting ? (
                <span className="flex items-center gap-2">
                  <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" /><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"/></svg>
                  Exporting...
                </span>
              ) : 'Export'}
            </Button>
          )}
          {showExportOptions && (
            <div className="flex gap-2">
              <Button
                variant="secondary"
                onClick={exportSelectedJson}
                disabled={exporting}
              >
                Export JSON
              </Button>
              <Button
                variant="secondary"
                onClick={exportSelectedCsv}
                disabled={exporting}
              >
                Export CSV
              </Button>
              <Button
                variant="subtle"
                onClick={() => setShowExportOptions(false)}
                disabled={exporting}
              >
                Cancel
              </Button>
            </div>
          )}
          {hasPermission('printers', 'admin') && (
            <>
              <Button 
                variant="primary" 
                aria-label="Open file picker to import printers" 
                onClick={handleImportClick}
                iconLeft={<FileImportIcon className="w-4 h-4" ariaLabel="Import" />}
              >
                Import
              </Button>
              <input
                ref={fileInputRef}
                id={fileInputId}
                type="file"
                accept=".json,.csv"
                onChange={(e) => handleFile(e.target.files?.[0])}
                style={{ display: 'none' }}
                aria-label="Select CSV or JSON file to import printers"
              />
            </>
          )}
        </div>
      </div>

        {/* Content Area */}
        <div className="space-y-6">
          {userPrinters.length === 0 ? (
            <div className="text-center py-12">
              <PrinterIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
              <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Printers Found</h3>
              <p className="text-pf-text-secondary mb-6">Get started by adding your first 3D printer using the "Add Printer" button above.</p>
            </div>
          ) : viewMode === 'compact' ? (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
              {userPrinters.map((p: Printer) => (
                <PrinterCompactCard
                  key={p.id}
                  printer={p}
                  onEdit={(printer) => handleEditPrinter(printer)}
                  onDelete={handleDeleteSinglePrinter}
                />
              ))}
            </div>
          ) : viewMode === 'collapsed' ? (
            <div className="flex gap-6 items-start">
              <div className="flex-1 grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4 transition-opacity duration-200">
                {userPrinters.map((printer) => (
                  <CollapsedPrinterCard
                    key={printer.id}
                    printer={printer}
                    onExpand={() => setExpandedPrinterId(printer.id)}
                    onEdit={() => handleEditPrinter(printer)}
                    onDelete={() => handleDeleteSinglePrinter(printer)}
                  />
                ))}
              </div>
              {expandedPrinterId && (
                <div className="w-96 flex-shrink-0">
                  <PrinterDetailsSidebar
                    printerId={expandedPrinterId}
                    onClose={() => setExpandedPrinterId(null)}
                  />
                </div>
              )}
            </div>
          ) : viewMode === 'expandable' ? (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
              {userPrinters.map((p) => (
                <DetailedPrinterCard
                  key={p.id}
                  printer={p}
                  onEdit={() => handleEditPrinter(p)}
                />
              ))}
            </div>
          ) : (
            <PrinterTableView
              printers={userPrinters}
              onEdit={handleEditPrinter}
              onDelete={handleDeleteClick}
              onBulkSetMaintenance={handleBulkSetMaintenance}
            />
          )}

        </div>

        {/* Modals */}
        <DeleteConfirmationModal
          isOpen={deleteConfirmation.isOpen}
          printers={deleteConfirmation.printers}
          onConfirm={handleDeleteConfirm}
          onCancel={handleDeleteCancel}
        />
        
        <EditPrinterModal
          printerId={editPrinterId}
          isOpen={showEditModal}
          onClose={() => setShowEditModal(false)}
          onSuccess={() => { setShowEditModal(false); refetchPrinters(); }}
        />

        <PrinterDiscoveryModal
          isOpen={showDiscovery}
          onClose={() => setShowDiscovery(false)}
          onSuccess={() => { setShowDiscovery(false); refetchPrinters(); }}
        />

        <ImportProgressModal
          isOpen={showImportProgress}
          fileName={importFileName}
          totalCount={importTotalCount}
          onClose={() => setShowImportProgress(false)}
        />
    </PageTemplate>
  );
}