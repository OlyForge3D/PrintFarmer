import { useState, useEffect } from 'react';
import { Filter, RefreshCw, ExternalLink, Package } from 'lucide-react';

interface SpoolmanSpool {
  id: number;
  filament: {
    id: number;
    vendor: {
      name: string;
    };
    material: string;
    color_hex: string;
    name: string;
  };
  remaining_weight: number;
  used_weight: number;
  remaining_length?: number;
  used_length?: number;
  location?: string;
  lot_nr?: string;
  first_used?: string;
  last_used?: string;
  archived: boolean;
}

interface FilterState {
  material: string;
  vendor: string;
  color: string;
  pageSize: string;
}

export function SpoolsPage() {
  const [spools, setSpools] = useState<SpoolmanSpool[]>([]);
  const [loading, setLoading] = useState(true);
  const [spoolmanError, setSpoolmanError] = useState<string | null>(null);
  const [spoolmanBaseUrl, setSpoolmanBaseUrl] = useState('');
  const [filters, setFilters] = useState<FilterState>({
    material: '',
    vendor: '',
    color: '',
    pageSize: '50'
  });

  const loadSpools = async () => {
    if (!spoolmanBaseUrl) {
      setSpoolmanError('Spoolman base URL not configured');
      setLoading(false);
      return;
    }

    try {
      setLoading(true);
      setSpoolmanError(null);
      
      const response = await fetch(`${spoolmanBaseUrl}/api/v1/spool`, {
        method: 'GET',
        headers: {
          'Accept': 'application/json',
        }
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      const data = await response.json();
      setSpools(Array.isArray(data) ? data : data.items || []);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error';
      setSpoolmanError(`Failed to connect to Spoolman: ${message}`);
      setSpools([]);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    const savedUrl = localStorage.getItem('spoolman-base-url') || '';
    setSpoolmanBaseUrl(savedUrl);
    
    if (savedUrl) {
      const loadData = async () => {
        if (!savedUrl) {
          setSpoolmanError('Spoolman base URL not configured');
          setLoading(false);
          return;
        }

        try {
          setLoading(true);
          setSpoolmanError(null);
          
          const response = await fetch(`${savedUrl}/api/v1/spool`, {
            method: 'GET',
            headers: {
              'Accept': 'application/json',
            }
          });

          if (!response.ok) {
            throw new Error(`HTTP ${response.status}: ${response.statusText}`);
          }

          const data = await response.json();
          setSpools(Array.isArray(data) ? data : data.items || []);
        } catch (err) {
          const message = err instanceof Error ? err.message : 'Unknown error';
          setSpoolmanError(`Failed to connect to Spoolman: ${message}`);
          setSpools([]);
        } finally {
          setLoading(false);
        }
      };
      
      loadData();
    } else {
      setSpoolmanError('Spoolman base URL not configured. Please set it in Settings first.');
      setLoading(false);
    }
  }, []);

  const getFilteredSpools = (): SpoolmanSpool[] => {
    return spools.filter(spool => {
      if (filters.material && !spool.filament.material.toLowerCase().includes(filters.material.toLowerCase())) {
        return false;
      }
      if (filters.vendor && !spool.filament.vendor.name.toLowerCase().includes(filters.vendor.toLowerCase())) {
        return false;
      }
      if (filters.color && !spool.filament.color_hex.includes(filters.color)) {
        return false;
      }
      return true;
    });
  };

  const getDisplayedSpools = (): SpoolmanSpool[] => {
    const filtered = getFilteredSpools();
    if (filters.pageSize === 'All') {
      return filtered;
    }
    const pageSize = parseInt(filters.pageSize);
    return filtered.slice(0, pageSize);
  };

  const getMaterialOptions = (): string[] => {
    const materials = [...new Set(spools.map(s => s.filament.material))];
    return materials.sort();
  };

  const getVendorOptions = (): string[] => {
    const vendors = [...new Set(spools.map(s => s.filament.vendor.name))];
    return vendors.sort();
  };

  const getColorDisplayName = (hex: string): string => {
    return hex.replace('#', '').toUpperCase();
  };

  const formatWeight = (weight: number): string => {
    return `${weight.toFixed(0)}g`;
  };

  const formatLength = (length?: number): string => {
    if (!length) return 'Unknown';
    if (length >= 1000) {
      return `${(length / 1000).toFixed(1)}m`;
    }
    return `${length.toFixed(0)}mm`;
  };

  const getUsagePercentage = (spool: SpoolmanSpool): number => {
    const total = spool.remaining_weight + spool.used_weight;
    if (total === 0) return 0;
    return (spool.used_weight / total) * 100;
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading spools...</div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Spools</h1>
        <div className="flex gap-2">
          <button
            onClick={loadSpools}
            disabled={loading || !spoolmanBaseUrl}
            className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2"
          >
            <RefreshCw className="h-4 w-4" />
            Refresh
          </button>
          {spoolmanBaseUrl && (
            <a
              href={spoolmanBaseUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="px-4 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 flex items-center gap-2"
            >
              <ExternalLink className="h-4 w-4" />
              Open Spoolman
            </a>
          )}
        </div>
      </div>

      {spoolmanError && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded flex items-center gap-3">
          <Package className="h-5 w-5 flex-shrink-0" />
          <div>
            <div className="font-medium">Spoolman Connection Error</div>
            <div className="text-sm">{spoolmanError}</div>
            {!spoolmanBaseUrl && (
              <div className="mt-2">
                <a href="/settings" className="text-blue-300 hover:text-blue-200 underline">
                  Configure Spoolman URL in Settings
                </a>
              </div>
            )}
          </div>
        </div>
      )}

      {!spoolmanError && spools.length > 0 && (
        <>
          {/* Filters */}
          <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4">
            <div className="flex items-center gap-4 flex-wrap">
              <div className="flex items-center gap-2">
                <Filter className="h-4 w-4 text-pf-text-secondary" />
                <span className="text-sm font-medium text-pf-text-primary">Filters:</span>
              </div>
              
              <select
                value={filters.material}
                onChange={(e) => setFilters(prev => ({ ...prev, material: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="">All Materials</option>
                {getMaterialOptions().map(material => (
                  <option key={material} value={material}>{material}</option>
                ))}
              </select>
              
              <select
                value={filters.vendor}
                onChange={(e) => setFilters(prev => ({ ...prev, vendor: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="">All Vendors</option>
                {getVendorOptions().map(vendor => (
                  <option key={vendor} value={vendor}>{vendor}</option>
                ))}
              </select>
              
              <select
                value={filters.pageSize}
                onChange={(e) => setFilters(prev => ({ ...prev, pageSize: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="10">10 per page</option>
                <option value="50">50 per page</option>
                <option value="100">100 per page</option>
                <option value="All">Show All</option>
              </select>

              <div className="ml-auto text-sm text-pf-text-secondary">
                Showing {getDisplayedSpools().length} of {getFilteredSpools().length} spools
              </div>
            </div>
          </div>

          {/* Spools Grid */}
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
            {getDisplayedSpools().map((spool) => (
              <div
                key={spool.id}
                className={`bg-pf-bg-1 border border-pf-border rounded-xl p-4 ${
                  spool.archived ? 'opacity-60' : ''
                } ${
                  spool.remaining_weight <= 50 ? 'border-orange-500' : ''
                } ${
                  spool.remaining_weight <= 10 ? 'border-red-500' : ''
                }`}
              >
                <div className="flex items-start justify-between mb-3">
                  <div className="flex items-center gap-2">
                    <div
                      className="w-4 h-4 rounded-full border border-pf-border"
                      style={{ backgroundColor: spool.filament.color_hex }}
                      title={getColorDisplayName(spool.filament.color_hex)}
                    />
                    <div className="text-sm font-medium text-pf-text-primary truncate">
                      #{spool.id}
                    </div>
                  </div>
                  {spool.archived && (
                    <div className="text-xs bg-gray-600 text-white px-2 py-1 rounded">
                      Archived
                    </div>
                  )}
                </div>

                <div className="space-y-2">
                  <div>
                    <div className="text-sm font-medium text-pf-text-primary">
                      {spool.filament.vendor.name}
                    </div>
                    <div className="text-xs text-pf-text-secondary">
                      {spool.filament.material} - {spool.filament.name || 'Unnamed'}
                    </div>
                  </div>

                  <div className="space-y-1">
                    <div className="flex justify-between text-xs">
                      <span className="text-pf-text-secondary">Weight</span>
                      <span className={`font-medium ${
                        spool.remaining_weight <= 50 ? 'text-orange-400' : ''
                      } ${
                        spool.remaining_weight <= 10 ? 'text-red-400' : ''
                      }`}>
                        {formatWeight(spool.remaining_weight)} / {formatWeight(spool.remaining_weight + spool.used_weight)}
                      </span>
                    </div>
                    <div className="w-full bg-pf-bg-0 rounded-full h-2">
                      <div
                        className={`h-2 rounded-full transition-all ${
                          spool.remaining_weight <= 50 ? 'bg-orange-500' : 'bg-blue-500'
                        } ${
                          spool.remaining_weight <= 10 ? 'bg-red-500' : ''
                        }`}
                        style={{
                          width: `${Math.max(5, 100 - getUsagePercentage(spool))}%`
                        }}
                      />
                    </div>
                    <div className="text-xs text-pf-text-secondary">
                      {getUsagePercentage(spool).toFixed(1)}% used
                    </div>
                  </div>

                  {(spool.remaining_length || spool.used_length) && (
                    <div className="text-xs text-pf-text-secondary">
                      Length: {formatLength(spool.remaining_length)} remaining
                    </div>
                  )}

                  {spool.location && (
                    <div className="text-xs text-pf-text-secondary">
                      Location: {spool.location}
                    </div>
                  )}

                  {spool.lot_nr && (
                    <div className="text-xs text-pf-text-secondary">
                      Lot: {spool.lot_nr}
                    </div>
                  )}

                  {spool.last_used && (
                    <div className="text-xs text-pf-text-secondary">
                      Last used: {new Date(spool.last_used).toLocaleDateString()}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>

          {getDisplayedSpools().length === 0 && getFilteredSpools().length > 0 && (
            <div className="text-center py-8 text-pf-text-secondary">
              No spools match the current filters.
            </div>
          )}
        </>
      )}

      {!spoolmanError && spools.length === 0 && !loading && (
        <div className="text-center py-8">
          <Package className="h-16 w-16 text-pf-text-secondary mx-auto mb-4" />
          <div className="text-pf-text-secondary">
            No spools found. Make sure your Spoolman instance is running and accessible.
          </div>
        </div>
      )}
    </div>
  );
}
