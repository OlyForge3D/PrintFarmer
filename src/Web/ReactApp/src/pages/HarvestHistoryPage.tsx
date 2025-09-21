import { apiClient } from '@/services/api';
import { GcodeHarvestOperation, GcodeHarvestStatus } from '@/types/api';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { HarvestOperationDetails } from '@/components/harvest/HarvestOperationDetails';
import { Link } from 'react-router-dom';


export function HarvestHistoryPage() {
  const [statusFilter, setStatusFilter] = useState<string>('');
  const [printerFilter, setPrinterFilter] = useState<string>('');
  const [detailsOperation, setDetailsOperation] = useState<GcodeHarvestOperation | null>(null);

  const { data: operations = [], isLoading, error, refetch } = useQuery({
    queryKey: ['harvest-operations', printerFilter, statusFilter],
    queryFn: () => apiClient.getHarvestOperations(
      printerFilter || undefined, 
      statusFilter || undefined, 
      100, // limit
      0 // offset
    ),
    refetchInterval: 5000, // Refetch every 5 seconds for real-time updates
  });

  const handleStatusFilterChange = (status: string) => {
    setStatusFilter(status);
  };

  const handlePrinterFilterChange = (printerId: string) => {
    setPrinterFilter(printerId);
  };

  const getStatusBadgeClass = (status: GcodeHarvestStatus) => {
    switch (status) {
      case GcodeHarvestStatus.Running:
        return 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300';
      case GcodeHarvestStatus.Completed:
        return 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300';
      case GcodeHarvestStatus.Failed:
        return 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300';
      case GcodeHarvestStatus.Cancelled:
        return 'bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-300';
      default:
        return 'bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-300';
    }
  };

  const getStatusString = (status: GcodeHarvestStatus): string => {
    switch (status) {
      case GcodeHarvestStatus.Running:
        return 'Running';
      case GcodeHarvestStatus.Completed:
        return 'Completed';
      case GcodeHarvestStatus.Failed:
        return 'Failed';
      case GcodeHarvestStatus.Cancelled:
        return 'Cancelled';
      default:
        return 'Unknown';
    }
  };

  const formatDuration = (startedAt: Date, completedAt?: Date) => {
    const start = startedAt instanceof Date ? startedAt : new Date(startedAt);
    const end = completedAt ? (completedAt instanceof Date ? completedAt : new Date(completedAt)) : new Date();
    const durationMs = end.getTime() - start.getTime();
    const minutes = Math.floor(durationMs / (1000 * 60));
    const seconds = Math.floor((durationMs % (1000 * 60)) / 1000);
    
    if (minutes > 0) {
      return `${minutes}m ${seconds}s`;
    } else {
      return `${seconds}s`;
    }
  };

  const uniquePrinters = [...new Set(operations.map(op => op.printerName))];

  if (isLoading) {
    return (
      <div className="space-y-6">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-bold text-pf-text-0">Harvest History</h1>
        </div>
        <div className="flex items-center justify-center py-12">
          <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-6">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-bold text-pf-text-0">Harvest History</h1>
        </div>
        <div className="bg-red-50 dark:bg-red-900/50 border border-red-200 dark:border-red-800 rounded-lg p-4">
          <div className="flex">
            <div className="ml-3">
              <h3 className="text-sm font-medium text-red-800 dark:text-red-200">
                Error Loading Harvest History
              </h3>
              <div className="mt-2 text-sm text-red-700 dark:text-red-300">
                {error instanceof Error ? error.message : 'An unknown error occurred'}
              </div>
              <div className="mt-4">
                <button
                  onClick={() => refetch()}
                  className="bg-red-100 hover:bg-red-200 dark:bg-red-800 dark:hover:bg-red-700 text-red-800 dark:text-red-200 px-3 py-2 rounded-md text-sm font-medium"
                >
                  Try Again
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-pf-text-0">Harvest History</h1>
        <div className="flex space-x-3">
          <Link
            to="/harvest"
            className="bg-pf-accent hover:bg-pf-accent-dark text-white px-4 py-2 rounded-lg font-medium"
          >
            Back to Harvest
          </Link>
        </div>
      </div>

      {/* Filters */}
      <div className="bg-pf-bg-1 rounded-lg p-4 border border-pf-border">
        <div className="flex flex-wrap gap-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-1 mb-1">
              Status
            </label>
            <select
              value={statusFilter}
              onChange={(e) => handleStatusFilterChange(e.target.value)}
              className="bg-pf-bg-0 border border-pf-border rounded-md px-3 py-2 text-pf-text-0 focus:outline-none focus:ring-2 focus:ring-pf-accent"
              aria-label="Filter by harvest status"
            >
              <option value="">All Status</option>
              <option value="Running">Running</option>
              <option value="Completed">Completed</option>
              <option value="Failed">Failed</option>
              <option value="Cancelled">Cancelled</option>
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-pf-text-1 mb-1">
              Printer
            </label>
            <select
              value={printerFilter}
              onChange={(e) => handlePrinterFilterChange(e.target.value)}
              className="bg-pf-bg-0 border border-pf-border rounded-md px-3 py-2 text-pf-text-0 focus:outline-none focus:ring-2 focus:ring-pf-accent"
              aria-label="Filter by printer"
            >
              <option value="">All Printers</option>
              {uniquePrinters.map((printerName) => (
                <option key={printerName} value={printerName}>
                  {printerName}
                </option>
              ))}
            </select>
          </div>
        </div>
      </div>

      {/* Operations List */}
      <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden">
        {operations.length === 0 ? (
          <div className="p-8 text-center">
            <div className="text-pf-text-1 mb-2">No harvest operations found</div>
            <Link
              to="/harvest"
              className="text-pf-accent hover:text-pf-accent-dark font-medium"
            >
              Start your first harvest →
            </Link>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full">
              <thead className="bg-pf-bg-2 border-b border-pf-border">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Printer
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Status
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Started
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Duration
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Files Found
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Files Added
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-1 uppercase tracking-wider">
                    Actions
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-pf-border">
                {operations.map((operation: GcodeHarvestOperation) => (
                  <tr key={operation.id} className="hover:bg-pf-bg-2">
                    <td className="px-6 py-4 whitespace-nowrap">
                      <div className="text-sm font-medium text-pf-text-0">
                        {operation.printerName}
                      </div>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap">
                      <span
                        className={`inline-flex px-2 py-1 text-xs font-semibold rounded-full ${getStatusBadgeClass(
                          operation.status
                        )}`}
                      >
                        {getStatusString(operation.status)}
                      </span>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-pf-text-1">
                      {new Date(operation.startedAt).toLocaleString()}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-pf-text-1">
                      {formatDuration(new Date(operation.startedAt), operation.completedAt ? new Date(operation.completedAt) : undefined)}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-pf-text-1">
                      {operation.filesFound}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-pf-text-1">
                      {operation.filesAdded}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm">
                      <button
                        type="button"
                        className="text-pf-accent hover:text-pf-accent-dark font-medium underline"
                        onClick={() => setDetailsOperation(operation)}
                      >
                        View Details
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Summary */}
      {operations.length > 0 && (
        <div className="bg-pf-bg-1 rounded-lg p-4 border border-pf-border">
          <h3 className="text-lg font-medium text-pf-text-0 mb-3">Summary</h3>
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <div>
              <div className="text-2xl font-bold text-pf-text-0">
                {operations.length}
              </div>
              <div className="text-sm text-pf-text-1">Total Operations</div>
            </div>
            <div>
              <div className="text-2xl font-bold text-pf-text-0">
                {operations.filter(op => op.status === GcodeHarvestStatus.Running).length}
              </div>
              <div className="text-sm text-pf-text-1">Currently Running</div>
            </div>
            <div>
              <div className="text-2xl font-bold text-pf-text-0">
                {operations.reduce((sum, op) => sum + op.filesFound, 0)}
              </div>
              <div className="text-sm text-pf-text-1">Total Files Found</div>
            </div>
            <div>
              <div className="text-2xl font-bold text-pf-text-0">
                {operations.reduce((sum, op) => sum + op.filesAdded, 0)}
              </div>
              <div className="text-sm text-pf-text-1">Total Files Added</div>
            </div>
          </div>
        </div>
      )}
    {/* Details Modal */}
    {detailsOperation && (
      <HarvestOperationDetails
        operation={detailsOperation}
        onClose={() => setDetailsOperation(null)}
      />
    )}
  </div>
  );
}