import { useMemo, useState } from 'react';
import { usePrinters } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { useAuth } from '@/contexts/AuthContext';
import { PrinterCard } from './PrinterCard';
import { EnhancedPrinterCard } from './EnhancedPrinterCard';
import { AddPrinterButton } from './AddPrinterButton';
import { PrinterDiscoveryModal } from './PrinterDiscoveryModal';
import { SystemHealth } from './SystemHealth';
import { 
  Printer, 
  CheckCircle, 
  Play, 
  Pause,
  Search,
  List,
  Grid3X3,
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
    blue: 'bg-blue-50 text-blue-700',
    green: 'bg-green-50 text-green-700',
    yellow: 'bg-yellow-50 text-yellow-700',
    gray: 'bg-gray-50 text-gray-700',
  };

  return (
    <div className="bg-white overflow-hidden shadow rounded-lg">
      <div className="p-5">
        <div className="flex items-center">
          <div className="flex-shrink-0">
            <div className={`p-3 rounded-md ${colorClasses[color]}`}>
              <Icon className="h-6 w-6" />
            </div>
          </div>
          <div className="ml-5 w-0 flex-1">
            <dl>
              <dt className="text-sm font-medium text-gray-500 truncate">{title}</dt>
              <dd className="text-lg font-medium text-gray-900">{value}</dd>
            </dl>
          </div>
        </div>
      </div>
    </div>
  );
}

export function PrinterDashboard() {
  const { hasPermission } = useAuth();
  const { data: printers, isLoading, error, refetch } = usePrinters();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  
  const [showDiscovery, setShowDiscovery] = useState(false);
  const [viewMode, setViewMode] = useState<'grid' | 'list' | 'detailed'>('grid');
  const [searchQuery, setSearchQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState<string>('');

  // Calculate printer statistics
  const printerStats = useMemo(() => {
    if (!printers) return { total: 0, online: 0, printing: 0, idle: 0 };
    
    return printers.reduce((stats, printer) => {
      const status = getPrinterStatus(printer.id);
      const isOnline = status?.isOnline ?? printer.isOnline;
      const state = status?.state ?? printer.state;
      
      stats.total++;
      
      if (isOnline) {
        stats.online++;
        
        if (state === 'printing') {
          stats.printing++;
        } else if (state === 'operational' || state === 'ready' || state === 'idle') {
          stats.idle++;
        }
      }
      
      return stats;
    }, { total: 0, online: 0, printing: 0, idle: 0 });
  }, [printers, getPrinterStatus]);

  // Filter printers based on search and status
  const filteredPrinters = useMemo(() => {
    if (!printers) return [];
    
    return printers.filter(printer => {
      const matchesSearch = !searchQuery || 
        printer.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
        printer.manufacturerName?.toLowerCase().includes(searchQuery.toLowerCase()) ||
        printer.modelName?.toLowerCase().includes(searchQuery.toLowerCase());
      
      if (!matchesSearch) return false;
      
      if (!statusFilter) return true;
      
      const status = getPrinterStatus(printer.id);
      const isOnline = status?.isOnline ?? printer.isOnline;
      const state = status?.state ?? printer.state;
      
      switch (statusFilter) {
        case 'online': return isOnline;
        case 'offline': return !isOnline;
        case 'printing': return isOnline && state === 'printing';
        case 'idle': return isOnline && (state === 'operational' || state === 'ready' || state === 'idle');
        default: return true;
      }
    });
  }, [printers, searchQuery, statusFilter, getPrinterStatus]);

  if (isLoading) {
    return (
      <div className="space-y-6">
        {/* Header skeleton */}
        <div className="flex justify-between items-center">
          <div className="space-y-2">
            <div className="h-8 w-32 bg-gray-200 rounded animate-pulse"></div>
            <div className="h-4 w-64 bg-gray-200 rounded animate-pulse"></div>
          </div>
          <div className="h-10 w-32 bg-gray-200 rounded animate-pulse"></div>
        </div>

        {/* Stats skeleton */}
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          {[...Array(4)].map((_, i) => (
            <div key={i} className="h-24 bg-gray-200 rounded animate-pulse"></div>
          ))}
        </div>

        {/* Cards skeleton */}
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
          {[...Array(6)].map((_, i) => (
            <div key={i} className="h-64 bg-gray-200 rounded animate-pulse"></div>
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4">
        <div className="flex">
          <div className="flex-shrink-0">
            <svg className="h-5 w-5 text-red-400" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z" clipRule="evenodd" />
            </svg>
          </div>
          <div className="ml-3">
            <h3 className="text-sm font-medium text-red-800">
              Error loading printers
            </h3>
            <div className="mt-2 text-sm text-red-700">
              <p>{error.message}</p>
            </div>
            <div className="mt-4">
              <button
                onClick={() => refetch()}
                className="bg-red-100 hover:bg-red-200 text-red-800 font-medium py-2 px-4 rounded text-sm transition-colors"
              >
                Try again
              </button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Dashboard</h1>
          <p className="mt-1 text-sm text-gray-500">
            Monitor and manage your 3D printer farm
          </p>
        </div>
        
        <div className="flex flex-col sm:flex-row gap-3">
          <SystemHealth />
          {hasPermission('printers', 'create') && (
            <>
              <button
                onClick={() => setShowDiscovery(true)}
                className="inline-flex items-center px-4 py-2 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
              >
                <Search className="h-4 w-4 mr-2" />
                Discover Printers
              </button>
              <AddPrinterButton onSuccess={() => refetch()} />
            </>
          )}
        </div>
      </div>

      {/* Stats Grid */}
      <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
        <StatsCard
          title="Total Printers"
          value={printerStats.total}
          icon={Printer}
          color="blue"
        />
        <StatsCard
          title="Online"
          value={printerStats.online}
          icon={CheckCircle}
          color="green"
        />
        <StatsCard
          title="Printing"
          value={printerStats.printing}
          icon={Play}
          color="yellow"
        />
        <StatsCard
          title="Idle"
          value={printerStats.idle}
          icon={Pause}
          color="gray"
        />
      </div>

      {/* Filters and Controls */}
      <div className="bg-white shadow rounded-lg p-4">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div className="flex flex-col sm:flex-row gap-4 flex-1">
            {/* Search */}
            <div className="relative flex-1 max-w-md">
              <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 h-4 w-4 text-gray-400" />
              <input
                type="text"
                placeholder="Search printers..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="pl-10 pr-4 py-2 border border-gray-300 rounded-md focus:ring-blue-500 focus:border-blue-500 w-full"
              />
            </div>
            
            {/* Status Filter */}
            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
              className="px-3 py-2 border border-gray-300 rounded-md focus:ring-blue-500 focus:border-blue-500"
            >
              <option value="">All Status</option>
              <option value="online">Online</option>
              <option value="offline">Offline</option>
              <option value="printing">Printing</option>
              <option value="idle">Idle</option>
            </select>
          </div>
          
          {/* View Mode Toggle */}
          <div className="flex border border-gray-300 rounded-md">
            <button
              onClick={() => setViewMode('grid')}
              className={`px-3 py-2 text-sm font-medium rounded-l-md ${
                viewMode === 'grid'
                  ? 'bg-blue-100 text-blue-700 border-r border-blue-200'
                  : 'text-gray-500 hover:text-gray-700 border-r border-gray-300'
              }`}
            >
              <Grid3X3 className="h-4 w-4" />
            </button>
            <button
              onClick={() => setViewMode('list')}
              className={`px-3 py-2 text-sm font-medium ${
                viewMode === 'list'
                  ? 'bg-blue-100 text-blue-700 border-r border-blue-200'
                  : 'text-gray-500 hover:text-gray-700 border-r border-gray-300'
              }`}
            >
              <List className="h-4 w-4" />
            </button>
            <button
              onClick={() => setViewMode('detailed')}
              className={`px-3 py-2 text-sm font-medium rounded-r-md ${
                viewMode === 'detailed'
                  ? 'bg-blue-100 text-blue-700'
                  : 'text-gray-500 hover:text-gray-700'
              }`}
            >
              <Settings className="h-4 w-4" />
            </button>
          </div>
        </div>
      </div>

      {/* Printers Grid/List */}
      {filteredPrinters.length > 0 ? (
        <div className={
          viewMode === 'detailed'
            ? 'space-y-6'
            : viewMode === 'grid'
            ? 'grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-6'
            : 'space-y-4'
        }>
          {filteredPrinters.map((printer) => (
            viewMode === 'detailed' ? (
              <EnhancedPrinterCard 
                key={printer.id} 
                printer={printer}
                viewMode={viewMode}
              />
            ) : (
              <PrinterCard 
                key={printer.id} 
                printer={printer}
                viewMode={viewMode}
              />
            )
          ))}
        </div>
      ) : (
        <div className="text-center py-12">
          <svg
            className="mx-auto h-12 w-12 text-gray-400"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              vectorEffect="non-scaling-stroke"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M9 17v-2m3 2v-4m3 4v-6m2 10H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
            />
          </svg>
          <h3 className="mt-2 text-sm font-medium text-gray-900">
            {searchQuery || statusFilter ? 'No printers match your criteria' : 'No printers found'}
          </h3>
          <p className="mt-1 text-sm text-gray-500">
            {searchQuery || statusFilter 
              ? 'Try adjusting your search or filters'
              : 'Get started by adding your first 3D printer.'
            }
          </p>
          {!searchQuery && !statusFilter && hasPermission('printers', 'create') && (
            <div className="mt-6 flex flex-col sm:flex-row gap-3 justify-center">
              <button
                onClick={() => setShowDiscovery(true)}
                className="inline-flex items-center px-4 py-2 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
              >
                <Search className="h-4 w-4 mr-2" />
                Discover Printers
              </button>
              <AddPrinterButton onSuccess={() => refetch()} />
            </div>
          )}
        </div>
      )}

      {/* Discovery Modal */}
      <PrinterDiscoveryModal
        isOpen={showDiscovery}
        onClose={() => setShowDiscovery(false)}
        onSuccess={() => refetch()}
      />
    </div>
  );
}