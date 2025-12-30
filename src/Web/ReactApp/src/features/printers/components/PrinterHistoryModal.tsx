if (!window.PrintFarmerDebug) {
  window.PrintFarmerDebug = {};
}
import { usePrinterHistory, usePrinterHistoryTotals } from '@/common/hooks/useApi';
import type { HistoryJob, Printer } from '@/types/api';
import { CalendarIcon, ClockIcon, XCircleIcon, AccountIcon, LayersIcon, ChartIcon, TimerIcon, PackageIcon } from '@/common/components/icons/MdiIcons';
import { PauseIcon, PlayIcon, CloseIcon, CheckCircleIcon, FileIcon } from '@/common/components/icons/MdiIcons';
import { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button, Select } from '@/common/components/ui';

interface PrinterHistoryModalProps {
  isOpen: boolean;
  onClose: () => void;
  printer: Printer;
}

function formatDuration(seconds: number | undefined | null): string {
  if (seconds == null || isNaN(seconds) || seconds <= 0) {
    return '0s';
  }
  
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);
  
  if (hours > 0) {
    return `${hours}h ${minutes}m ${secs}s`;
  } else if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  } else {
    return `${secs}s`;
  }
}

function formatFilamentUsed(mm: number | undefined | null): string {
  if (mm == null || isNaN(mm)) {
    return '0mm';
  }
  if (mm > 1000) {
    return `${(mm / 1000).toFixed(1)}m`;
  }
  return `${mm.toFixed(0)}mm`;
}

function formatDate(timestamp: number): string {
  const date = new Date(timestamp * 1000);
  return date.toLocaleDateString() + ' ' + date.toLocaleTimeString();
}

function getStatusIcon(status: string) {
  switch (status.toLowerCase()) {
    case 'completed':
      return <CheckCircleIcon className="h-4 w-4 text-green-500" />;
    case 'cancelled':
      return <XCircleIcon className="h-4 w-4 text-red-500" />;
    case 'paused':
      return <PauseIcon className="h-4 w-4" ariaLabel="Paused" />;
    case 'printing':
      return <PlayIcon className="h-4 w-4" ariaLabel="Printing" />;
    default:
      return <FileIcon className="h-4 w-4 text-gray-500" />;
  }
}

function getStatusColor(status: string): string {
  switch (status.toLowerCase()) {
    case 'completed':
      return 'bg-green-100 text-green-800';
    case 'cancelled':
      return 'bg-red-100 text-red-800';
    case 'paused':
      return 'bg-yellow-100 text-yellow-800';
    case 'printing':
      return 'bg-blue-100 text-blue-800';
    default:
      return 'bg-gray-100 text-gray-800';
  }
}

export function PrinterHistoryModal({ isOpen, onClose, printer }: PrinterHistoryModalProps) {
  const [limit, setLimit] = useState(50);
  const [order, setOrder] = useState<string>('desc');
  
  // Conditional debug logging for PrinterHistoryModal (guarded)
  if (window.PrintFarmerDebug?.printerHistory) {
    console.log('[PrintFarmer] PrinterHistoryModal render:', { isOpen, printerName: printer.name, printerId: printer.id });
  }

  const { 
    data: historyData, 
    isLoading, 
    error,
    refetch 
  } = usePrinterHistory(
    printer.id, 
    { limit, order }
  );

  const { 
    data: totalsData,
    isLoading: totalsLoading 
  } = usePrinterHistoryTotals(printer.id);

  // Handle ESC key to close modal
  useEffect(() => {
    if (!isOpen) return;

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, onClose]);

  if (!isOpen) {
    return null;
  }

  const modalContent = (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex min-h-screen items-center justify-center p-4">
        <div className="fixed inset-0 bg-black bg-opacity-75" />
        
        <div className="relative bg-pf-bg-1 rounded-lg shadow-xl max-w-4xl w-full max-h-[80vh] flex flex-col">
          {/* Header */}
          <div className="flex items-center justify-between p-6 border-b border-pf-border">
            <div>
              <h2 className="text-xl font-semibold text-pf-text-primary">Print History</h2>
              <p className="text-sm text-pf-text-secondary mt-1">
                {printer.name} - Recent print jobs
              </p>
            </div>
            
            <div className="flex items-center space-x-3">
              {/* Controls */}
              <div className="flex items-center space-x-2">
                <label htmlFor="history-limit" className="text-sm text-pf-text-secondary">Show:</label>
                <Select
                  id="history-limit"
                  value={limit.toString()}
                  onChange={(e) => setLimit(Number(e.target.value))}
                  className="text-sm"
                >
                  <option value="25">25 jobs</option>
                  <option value="50">50 jobs</option>
                  <option value="100">100 jobs</option>
                </Select>
                
                <Select
                  id="history-order"
                  value={order}
                  onChange={(e) => setOrder(e.target.value)}
                  className="text-sm ml-2"
                  aria-label="Sort order"
                >
                  <option value="desc">Newest first</option>
                  <option value="asc">Oldest first</option>
                </Select>
              </div>
              
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={onClose}
                className="!p-2 !h-auto"
                title="Close"
              >
                <CloseIcon className="h-5 w-5" />
              </Button>
            </div>
          </div>

          {/* Content */}
          <div className="flex-1 overflow-y-auto p-6">
            {isLoading ? (
              <div className="flex items-center justify-center py-8">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
                <span className="ml-3 text-pf-text-secondary">Loading print history...</span>
              </div>
            ) : error ? (
              <div className="text-center py-8">
                <XCircleIcon className="h-12 w-12 text-red-500 mx-auto mb-4" />
                <h3 className="text-lg font-medium text-pf-text-primary mb-2">Failed to Load History</h3>
                <p className="text-pf-text-secondary mb-4">{error.message}</p>
                <Button
                  type="button"
                  variant="primary"
                  onClick={() => refetch()}
                >
                  Try Again
                </Button>
              </div>
            ) : !historyData || historyData.jobs.length === 0 ? (
              <div className="text-center py-8">
                <FileIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
                <h3 className="text-lg font-medium text-pf-text-primary mb-2">No Print History</h3>
                <p className="text-pf-text-secondary">
                  No print jobs found for this printer yet.
                </p>
              </div>
            ) : (
              <div>
                {/* Summary Statistics */}
                {totalsData && totalsData.jobTotals && !totalsLoading && (
                  <div className="bg-pf-bg-0 border border-pf-border rounded-lg p-4 mb-6">
                    <h3 className="text-lg font-medium text-pf-text-primary mb-4 flex items-center">
                      <ChartIcon className="h-5 w-5 mr-2" />
                      Print Statistics
                    </h3>
                    <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                      {/* Total Jobs */}
                      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
                        <div className="flex items-center space-x-2 mb-2">
                          <FileIcon className="h-4 w-4 text-pf-accent" />
                          <span className="text-sm font-medium text-pf-text-secondary">Total Jobs</span>
                        </div>
                        <div className="text-2xl font-bold text-pf-text-primary">
                          {(totalsData.jobTotals.totalJobs || 0).toLocaleString()}
                        </div>
                      </div>

                      {/* Total Print Time */}
                      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
                        <div className="flex items-center space-x-2 mb-2">
                          <TimerIcon className="h-4 w-4 text-blue-500" />
                          <span className="text-sm font-medium text-pf-text-secondary">Total Print Time</span>
                        </div>
                        <div className="text-2xl font-bold text-pf-text-primary">
                          {formatDuration(totalsData.jobTotals.totalPrintTime)}
                        </div>
                      </div>

                      {/* Total Filament */}
                      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
                        <div className="flex items-center space-x-2 mb-2">
                          <PackageIcon className="h-4 w-4 text-green-500" />
                          <span className="text-sm font-medium text-pf-text-secondary">Total Filament</span>
                        </div>
                        <div className="text-2xl font-bold text-pf-text-primary">
                          {formatFilamentUsed(totalsData.jobTotals.totalFilament)}
                        </div>
                      </div>

                      {/* Additional metrics if available */}
                      {(totalsData.jobTotals.longestPrint || 0) > 0 && (
                        <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
                          <div className="flex items-center space-x-2 mb-2">
                            <ClockIcon className="h-4 w-4 text-purple-500" />
                            <span className="text-sm font-medium text-pf-text-secondary">Longest Print</span>
                          </div>
                          <div className="text-2xl font-bold text-pf-text-primary">
                            {formatDuration(totalsData.jobTotals.longestPrint)}
                          </div>
                        </div>
                      )}

                      {(totalsData.jobTotals.longestJob || 0) > 0 && (totalsData.jobTotals.longestJob || 0) !== (totalsData.jobTotals.longestPrint || 0) && (
                        <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
                          <div className="flex items-center space-x-2 mb-2">
                            <CalendarIcon className="h-4 w-4 text-orange-500" />
                            <span className="text-sm font-medium text-pf-text-secondary">Longest Job</span>
                          </div>
                          <div className="text-2xl font-bold text-pf-text-primary">
                            {formatDuration(totalsData.jobTotals.longestJob)}
                          </div>
                        </div>
                      )}
                    </div>

                    {/* Auxiliary Statistics */}
                    {totalsData.auxiliaryTotals && totalsData.auxiliaryTotals.length > 0 && (
                      <div className="mt-4 pt-4 border-t border-pf-border">
                        <h4 className="text-sm font-medium text-pf-text-secondary mb-3">Additional Metrics</h4>
                        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
                          {totalsData.auxiliaryTotals.map((aux, index) => (
                            <div key={`${aux.provider}-${aux.name}-${index}`} className="bg-pf-bg-2 border border-pf-border rounded p-3">
                              <div className="text-xs text-pf-text-tertiary mb-1">
                                {aux.provider} - {aux.description || aux.name}
                              </div>
                              <div className="font-semibold text-pf-text-primary">
                                {(aux.totalValue || 0).toLocaleString()}{aux.units && ` ${aux.units}`}
                              </div>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                )}

                <div className="space-y-4">
                  <div className="flex items-center justify-between mb-4">
                    <h3 className="text-lg font-medium text-pf-text-primary">
                      {historyData.count} Print Jobs
                    </h3>
                  </div>

                  {/* Jobs List */}
                  <div className="space-y-3">
                  {historyData.jobs.map((job: HistoryJob) => (
                    <div
                      key={job.jobId}
                      className="bg-pf-bg-0 border border-pf-border rounded-lg p-4 hover:shadow-sm transition-shadow"
                    >
                      <div className="flex items-start justify-between">
                        <div className="flex-1 min-w-0">
                          {/* Job Header */}
                          <div className="flex items-center space-x-2 mb-2">
                            {getStatusIcon(job.status)}
                            <h4 className="text-sm font-medium text-pf-text-primary truncate">
                              {job.filename}
                            </h4>
                            <span className={`px-2 py-1 text-xs font-medium rounded-full ${getStatusColor(job.status)}`}>
                              {job.status}
                            </span>
                          </div>

                          {/* Job Details */}
                          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-sm">
                            <div className="flex items-center space-x-1 text-pf-text-secondary">
                              <CalendarIcon className="h-3 w-3" />
                              <span className="text-xs">{formatDate(job.startTime)}</span>
                            </div>
                            
                            <div className="flex items-center space-x-1 text-pf-text-secondary">
                              <ClockIcon className="h-3 w-3" />
                              <span className="text-xs">
                                {formatDuration(job.printDuration)}
                              </span>
                            </div>

                            {job.filamentUsed > 0 && (
                              <div className="flex items-center space-x-1 text-pf-text-secondary">
                                <LayersIcon className="h-3 w-3" />
                                <span className="text-xs">
                                  {formatFilamentUsed(job.filamentUsed)}
                                </span>
                              </div>
                            )}

                            {job.user && (
                              <div className="flex items-center space-x-1 text-pf-text-secondary">
                                <AccountIcon className="h-3 w-3" />
                                <span className="text-xs">{job.user}</span>
                              </div>
                            )}
                          </div>

                          {/* Metadata */}
                          {job.metadata && Object.keys(job.metadata).length > 0 && (
                            <div className="mt-2 pt-2 border-t border-pf-border-light">
                              <details className="group">
                                <summary className="text-xs text-pf-text-tertiary cursor-pointer hover:text-pf-text-secondary">
                                  Show metadata ({Object.keys(job.metadata).length} items)
                                </summary>
                                <div className="mt-1 text-xs text-pf-text-secondary space-y-1">
                                  {Object.entries(job.metadata).map(([key, value]) => (
                                    <div key={key} className="flex justify-between">
                                      <span className="font-medium">{key}:</span>
                                      <span className="text-pf-text-tertiary">{renderUnknown(value)}</span>
                                    </div>
                                  ))}
                                </div>
                              </details>
                            </div>
                          )}
                        </div>

                        {/* Thumbnail */}
                        {job.thumbnailUrl && (
                          <div className="ml-4 flex-shrink-0">
                            <img
                              src={job.thumbnailUrl}
                              alt={`${job.filename} thumbnail`}
                              className="w-16 h-16 object-cover rounded border border-pf-border"
                              onError={(e) => {
                                // Hide broken images
                                e.currentTarget.style.display = 'none';
                              }}
                            />
                          </div>
                        )}
                      </div>
                    </div>
                  ))}
                </div>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );

  // Render modal using React Portal to avoid z-index issues
  return createPortal(modalContent, document.body);
}