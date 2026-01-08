import React from 'react';
import { usePrinter, usePrinterHistory, usePrinterHistoryTotals } from '@/common/hooks/useApi';
import type { HistoryListResponse } from '@/types/api';
import { Button } from '@/common/components/ui';
import { toast } from 'sonner';

interface Props {
  printerId: string;
}

function formatDuration(seconds: number | undefined | null): string {
  if (seconds == null || isNaN(seconds) || seconds <= 0) return '0s';
  const hrs = Math.floor(seconds / 3600);
  const mins = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);
  if (hrs > 0) return `${hrs}h ${mins}m ${secs}s`;
  if (mins > 0) return `${mins}m ${secs}s`;
  return `${secs}s`;
}

function formatFilament(mm?: number | null) {
  if (!mm || isNaN(mm)) return '0mm';
  if (mm > 1000) return `${(mm / 1000).toFixed(1)}m`;
  return `${Math.round(mm)}mm`;
}

export const PrinterQueueHistory: React.FC<Props> = ({ printerId }) => {
  const { data: printer, refetch: refetchPrinter } = usePrinter(printerId);

  const { data: historyData, isLoading: historyLoading, error: historyError } = usePrinterHistory(printerId, { limit: 20, order: 'desc' });

  const { data: totalsData, isLoading: totalsLoading } = usePrinterHistoryTotals(printerId);

  const handleToggleMaintenance = async () => {
    try {
      const inMaintenance = !!printer?.inMaintenance;
      const resp = await fetch(`/api/printers/${printerId}/maintenance`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(!inMaintenance),
      });
      if (!resp.ok) {
        const txt = await resp.text();
        throw new Error(txt || `Status ${resp.status}`);
      }
      toast.success(`Maintenance ${!inMaintenance ? 'enabled' : 'disabled'}`);
      await refetchPrinter();
    } catch (e) {
      toast.error(`Failed to update maintenance: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  return (
    <div className="bg-white p-4 rounded border">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-semibold">Recent History</h3>
        <div className="flex items-center gap-2">
          {printer && (
            <div className="text-sm text-gray-600">Maintenance: {printer.inMaintenance ? 'On' : 'Off'}</div>
          )}
          <Button size="sm" onClick={handleToggleMaintenance}>
            {printer?.inMaintenance ? 'Disable Maintenance' : 'Enable Maintenance'}
          </Button>
        </div>
      </div>

      {totalsLoading ? (
        <div className="text-sm text-gray-600 mb-3">Loading statistics…</div>
      ) : totalsData && totalsData.jobTotals ? (
        <div className="mb-3 grid grid-cols-3 gap-2 text-sm text-gray-700">
          <div>
            <div className="text-xs text-gray-500">Total Jobs</div>
            <div className="font-medium">{(totalsData.jobTotals.totalJobs || 0).toLocaleString()}</div>
          </div>
          <div>
            <div className="text-xs text-gray-500">Total Print Time</div>
            <div className="font-medium">{formatDuration(totalsData.jobTotals.totalPrintTime)}</div>
          </div>
          <div>
            <div className="text-xs text-gray-500">Total Filament</div>
            <div className="font-medium">{formatFilament(totalsData.jobTotals.totalFilament)}</div>
          </div>
        </div>
      ) : null}

      {historyLoading ? (
        <div>Loading history…</div>
      ) : historyError ? (
        <div className="text-red-600">Failed to load history</div>
      ) : !historyData || (historyData as HistoryListResponse).jobs.length === 0 ? (
        <div className="text-sm text-gray-600">No history available</div>
      ) : (
        <ul className="space-y-2">
          {(historyData as HistoryListResponse).jobs.map(h => (
            <li key={h.jobId ?? h.id} className="p-2 border rounded">
              <div className="font-medium">{h.jobName ?? h.fileName ?? h.jobId}</div>
              <div className="text-xs text-gray-500">Completed: {h.completedAt ? new Date(h.completedAt).toLocaleString() : '—'}</div>
              <div className="text-xs text-gray-600">Duration: {formatDuration(h.printDuration)}</div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};

export default PrinterQueueHistory;
