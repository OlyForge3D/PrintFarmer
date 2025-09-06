import { useState, useEffect } from 'react';
import { Filter, RefreshCw, ExternalLink, Package, Pencil, LayoutGrid, Table as TableIcon } from 'lucide-react';
import { classifyColor, getRepresentativeHex } from '@/utils/colorFamilies';
import { ColorSwatch } from '@/components/ColorSwatch';
import { SpoolUsageBar } from '@/components/SpoolUsageBar';
import { Skeleton } from '@/components/Skeleton';
import '@/components/spool-components.css';

// Matches backend SpoolmanController (SpoolmanSpoolDto) serialized with camelCase
interface SpoolmanSpoolDto {
  id: number;
  name: string;
  material: string;
  remainingWeightG?: number | null;
  colorHex?: string | null;
  inUse: boolean;
  filamentName?: string | null;
  vendor?: string | null;
  registeredAt?: string | null;
  firstUsedAt?: string | null;
  lastUsedAt?: string | null;
  initialWeightG?: number | null;
  usedWeightG?: number | null;
  spoolWeightG?: number | null;
  remainingLengthMm?: number | null;
  usedLengthMm?: number | null;
  location?: string | null;
  lotNumber?: string | null;
  archived?: boolean | null;
  usedPercent?: number | null; // from server (UsedPercent)
  remainingPercent?: number | null; // from server (RemainingPercent)
}

interface FilterState {
  material: string;
  vendor: string;
  color: string;
  pageSize: string;
  location: string;
  showArchived: string; // 'all' | 'active' | 'archived'
}

export function SpoolsPage() {
  const [spools, setSpools] = useState<SpoolmanSpoolDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [spoolmanError, setSpoolmanError] = useState<string | null>(null);
  const [spoolmanBaseUrl, setSpoolmanBaseUrl] = useState('');
  const [filters, setFilters] = useState<FilterState>({
    material: '',
    vendor: '',
    color: '',
  pageSize: '50',
  location: '',
  showArchived: 'active'
  });
  const [sortField, setSortField] = useState<string>('id');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [viewMode, setViewMode] = useState<'cards' | 'table'>('cards');

  const loadSpools = async () => {
    try {
      setLoading(true);
      setSpoolmanError(null);

  const response = await fetch('/api/spoolman/spools', { headers: { 'Accept': 'application/json' } });
      if (!response.ok) {
        if (response.status === 503) {
          setSpoolmanError('Spoolman not configured or unavailable');
        } else {
          setSpoolmanError(`Backend error: HTTP ${response.status}`);
        }
        setSpools([]);
        return;
      }
  const data = await response.json();
  const list: SpoolmanSpoolDto[] = Array.isArray(data) ? data : (data.items || []);
  setSpools(list);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error';
      setSpoolmanError(`Failed to load spools: ${message}`);
      setSpools([]);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    const init = async () => {
      try {
        const cfgResp = await fetch('/api/spoolman/config');
        if (cfgResp.ok) {
          const cfg = await cfgResp.json();
          if (cfg?.baseUrl) setSpoolmanBaseUrl(cfg.baseUrl);
        } else {
          const saved = localStorage.getItem('spoolman-base-url');
          if (saved) setSpoolmanBaseUrl(saved);
        }
      } catch {
        const saved = localStorage.getItem('spoolman-base-url');
        if (saved) setSpoolmanBaseUrl(saved);
      }
      await loadSpools();
    };
    init();
  }, []);

  const getFilteredSpools = (): SpoolmanSpoolDto[] => spools.filter(spool => {
    if (filters.material && !spool.material?.toLowerCase().includes(filters.material.toLowerCase())) return false;
    if (filters.vendor && !(spool.vendor || '').toLowerCase().includes(filters.vendor.toLowerCase())) return false;
    if (filters.color && classifyColor(spool.colorHex) !== filters.color) return false;
	if (filters.location && !(spool.location || '').toLowerCase().includes(filters.location.toLowerCase())) return false;
	if (filters.showArchived === 'active' && spool.archived) return false;
	if (filters.showArchived === 'archived' && !spool.archived) return false;
    return true;
  });

  const getDisplayedSpools = (): SpoolmanSpoolDto[] => {
    let filtered = getFilteredSpools();
    filtered = [...filtered].sort((a, b) => {
      const dir = sortDir === 'asc' ? 1 : -1;
      const val = (f: SpoolmanSpoolDto): unknown => {
        switch (sortField) {
          case 'vendor': return (f.vendor || '').toLowerCase();
          case 'material': return (f.material || '').toLowerCase();
          case 'remaining': return f.remainingWeightG ?? -Infinity;
          case 'usedPercent': return getUsagePercentage(f);
          case 'color': return classifyColor(f.colorHex).toLowerCase();
          case 'location': return (f.location || '').toLowerCase();
          default: return f.id;
        }
      };
  const av = val(a) as (string | number);
  const bv = val(b) as (string | number);
  if (av < bv) return -1 * dir;
  if (av > bv) return 1 * dir;
      return 0;
    });
    if (filters.pageSize === 'All') return filtered;
    const pageSize = parseInt(filters.pageSize);
    return filtered.slice(0, pageSize);
  };

  const getMaterialOptions = (): string[] => [...new Set(spools.map(s => s.material).filter((m): m is string => !!m))].sort();
  const getVendorOptions = (): string[] => [...new Set(spools.map(s => s.vendor).filter((v): v is string => !!v))].sort();
  const getLocationOptions = (): string[] => [...new Set(spools.map(s => s.location).filter((l): l is string => !!l))].sort();
  const getColorFamilyOptions = (): string[] => [...new Set(spools.map(s => classifyColor(s.colorHex)))]
    .filter(f => f && f !== 'Unknown')
    .sort();

  const formatWeight = (weight?: number | null): string => {
    if (typeof weight === 'number' && isFinite(weight)) return `${Math.max(0, weight).toFixed(0)}g`;
    return '—';
  };

  // Length not provided in DTO.

  const getUsagePercentage = (spool: SpoolmanSpoolDto): number => {
    if (typeof spool.usedPercent === 'number') return spool.usedPercent;
    if (typeof spool.usedWeightG === 'number' && typeof spool.initialWeightG === 'number' && spool.initialWeightG > 0) {
      return (spool.usedWeightG / spool.initialWeightG) * 100;
    }
    if (typeof spool.remainingWeightG === 'number' && typeof spool.initialWeightG === 'number' && spool.initialWeightG > 0) {
      return ((spool.initialWeightG - spool.remainingWeightG) / spool.initialWeightG) * 100;
    }
    return 0;
  };
  const getRemainingPercentage = (spool: SpoolmanSpoolDto): number => {
    if (typeof spool.remainingPercent === 'number') return spool.remainingPercent;
    const used = getUsagePercentage(spool);
    return used > 0 ? 100 - used : 0;
  };
  const handleExportCsv = () => {
    const rows = [
      ['id','name','vendor','material','filamentName','colorHex','initialWeightG','remainingWeightG','usedWeightG','usedPercent','remainingPercent','location','lotNumber','archived'],
      ...getDisplayedSpools().map(s => [
        s.id,
        s.name,
        s.vendor || '',
        s.material || '',
        s.filamentName || '',
        s.colorHex || '',
        s.initialWeightG ?? '',
        s.remainingWeightG ?? '',
        s.usedWeightG ?? (s.initialWeightG && s.remainingWeightG ? s.initialWeightG - s.remainingWeightG : ''),
        getUsagePercentage(s).toFixed(1),
        getRemainingPercentage(s).toFixed(1),
        s.location || '',
        s.lotNumber || '',
        s.archived ? 'true' : 'false'
      ])
    ];
    const csv = rows.map(r => r.map(field => {
      const str = String(field);
      return /[",\n]/.test(str) ? '"' + str.replace(/"/g,'""') + '"' : str;
    }).join(',')).join('\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'spools.csv';
    a.click();
    URL.revokeObjectURL(url);
  };
  const weightTooltip = (spool: SpoolmanSpoolDto) => {
    const parts: string[] = [];
    if (spool.initialWeightG != null) parts.push(`Initial: ${spool.initialWeightG}g`);
    if (spool.remainingWeightG != null) parts.push(`Remaining: ${spool.remainingWeightG}g`);
    const used = spool.usedWeightG ?? (spool.initialWeightG && spool.remainingWeightG != null ? (spool.initialWeightG - spool.remainingWeightG) : undefined);
    if (used != null) parts.push(`Used: ${used}g`);
    parts.push(`Used %: ${getUsagePercentage(spool).toFixed(1)}%`);
    parts.push(`Remaining %: ${getRemainingPercentage(spool).toFixed(1)}%`);
    return parts.join(' | ');
  };

  if (loading) {
    return (
      <div className="space-y-6">
        <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Spools</h1>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4" aria-label="Loading spools">
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className="bg-pf-bg-1 border border-pf-border rounded-xl p-4 space-y-3">
              <div className="flex items-center gap-2">
                <div className="skeleton-base skeleton-pill w-4 h-4" />
                <Skeleton width="40%" />
              </div>
              <Skeleton width="70%" />
              <Skeleton width="90%" />
              <div className="space-y-2">
                <Skeleton width="100%" height={12} />
                <div className="skeleton-base skeleton-pill h-2 w-full" />
                <Skeleton width="30%" />
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Spools</h1>
        <div className="flex gap-2 items-center">
          <div className="flex rounded overflow-hidden border border-pf-border">
            <button
              type="button"
              aria-label="Card view"
              className={`px-3 py-2 text-sm flex items-center gap-1 ${viewMode === 'cards' ? 'bg-blue-600 text-white' : 'bg-pf-bg-0 text-pf-text-secondary hover:bg-pf-bg-2'}`}
              onClick={() => setViewMode('cards')}
            >
              <LayoutGrid className="h-4 w-4" /> Cards
            </button>
            <button
              type="button"
              aria-label="Table view"
              className={`px-3 py-2 text-sm flex items-center gap-1 ${viewMode === 'table' ? 'bg-blue-600 text-white' : 'bg-pf-bg-0 text-pf-text-secondary hover:bg-pf-bg-2'}`}
              onClick={() => setViewMode('table')}
            >
              <TableIcon className="h-4 w-4" /> Table
            </button>
          </div>
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
                aria-label="Filter by material"
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
                aria-label="Filter by vendor"
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
                aria-label="Filter by color family"
                value={filters.color}
                onChange={(e) => setFilters(prev => ({ ...prev, color: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="">All Colors</option>
                {getColorFamilyOptions().map(fam => (
                  <option key={fam} value={fam}>{fam}</option>
                ))}
              </select>

              <select
                aria-label="Select page size"
                value={filters.pageSize}
                onChange={(e) => setFilters(prev => ({ ...prev, pageSize: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="10">10 per page</option>
                <option value="50">50 per page</option>
                <option value="100">100 per page</option>
                <option value="All">Show All</option>
              </select>

              <select
                aria-label="Filter by location"
                value={filters.location}
                onChange={(e) => setFilters(prev => ({ ...prev, location: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="">All Locations</option>
                {getLocationOptions().map(loc => (
                  <option key={loc} value={loc}>{loc}</option>
                ))}
              </select>

              <select
                aria-label="Filter archived"
                value={filters.showArchived}
                onChange={(e) => setFilters(prev => ({ ...prev, showArchived: e.target.value }))}
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm"
              >
                <option value="active">Active Only</option>
                <option value="all">All</option>
                <option value="archived">Archived Only</option>
              </select>

              <div className="ml-auto flex items-center gap-2 text-sm text-pf-text-secondary">
                <span>Showing {getDisplayedSpools().length} of {getFilteredSpools().length} spools</span>
                <label className="text-xs" htmlFor="sort-field">Sort:</label>
                <select
                  id="sort-field"
                  aria-label="Sort field"
                  value={sortField}
                  onChange={e => setSortField(e.target.value)}
                  className="px-2 py-1 bg-pf-bg-0 border border-pf-border rounded text-xs"
                >
                  <option value="id">ID</option>
                  <option value="vendor">Vendor</option>
                  <option value="material">Material</option>
                  <option value="remaining">Remaining (g)</option>
                  <option value="usedPercent">Used %</option>
                  <option value="color">Color</option>
                  <option value="location">Location</option>
                </select>
                <button
                  type="button"
                  aria-label="Toggle sort direction"
                  onClick={() => setSortDir(prev => prev === 'asc' ? 'desc' : 'asc')}
                  className="px-2 py-1 text-xs bg-pf-bg-0 border border-pf-border rounded hover:bg-pf-bg-2"
                >{sortDir === 'asc' ? '▲' : '▼'}</button>
                <button
                  type="button"
                  onClick={handleExportCsv}
                  className="px-2 py-1 text-xs bg-green-600 text-white rounded hover:bg-green-700"
                >Export CSV</button>
              </div>
            </div>
          </div>

          <div className="text-xs text-pf-text-secondary flex flex-wrap gap-4 items-center">
            <div className="flex items-center gap-1"><span className="inline-block px-2 py-0.5 text-[10px] rounded bg-red-600/20 text-red-300 border border-red-600/40 uppercase tracking-wide">Archived</span> = Spool not for active use</div>
            <div>Usage shows Used % / Remaining % (hover for weight details)</div>
          </div>

          {viewMode === 'cards' && (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
            {getDisplayedSpools().map((spool) => (
              <div
                key={spool.id}
                className={`bg-pf-bg-1 border border-pf-border rounded-xl p-4 ${
                  (spool.remainingWeightG ?? Infinity) <= 50 ? 'border-orange-500' : ''
                } ${
                  (spool.remainingWeightG ?? Infinity) <= 10 ? 'border-red-500' : ''
                }`}
              >
                <div className="flex items-start justify-between mb-3">
                  <div className="flex items-center gap-2">
                  <ColorSwatch color={getRepresentativeHex(classifyColor(spool.colorHex))} label={classifyColor(spool.colorHex)} />
                    <div className="text-sm font-medium text-pf-text-primary truncate">
                      #{spool.id}
                    </div>
                  </div>
                  {spool.archived && (
                    <span className="ml-2 inline-block px-2 py-0.5 text-[10px] rounded bg-red-600/20 text-red-300 border border-red-600/40 uppercase tracking-wide">Archived</span>
                  )}
                </div>

                <div className="space-y-2">
                  <div>
                    <div className="text-sm font-medium text-pf-text-primary">
                      {spool.vendor || 'Unknown Vendor'}
                    </div>
                    <div className="text-xs text-pf-text-secondary">
                      {(spool.material || 'Unknown Material')} - {spool.filamentName || spool.name || 'Unnamed'}
                    </div>
                  </div>

                  <div className="space-y-1">
                    <div className="flex justify-between text-xs">
                      <span className="text-pf-text-secondary">Weight</span>
                      <span className={`font-medium ${
                        (spool.remainingWeightG ?? Infinity) <= 50 ? 'text-orange-400' : ''
                      } ${
                        (spool.remainingWeightG ?? Infinity) <= 10 ? 'text-red-400' : ''
                      }`}>
                        {formatWeight(spool.remainingWeightG)}
                      </span>
                    </div>
                    <SpoolUsageBar
                      usedWeight={spool.usedWeightG ?? (spool.initialWeightG && spool.remainingWeightG ? (spool.initialWeightG - spool.remainingWeightG) : 0)}
                      remainingWeight={spool.remainingWeightG ?? 0}
                      label={`Spool ${spool.id} usage`}
                    />
                    <div
                      className="text-xs text-pf-text-secondary"
                      title={weightTooltip(spool)}
                      aria-label={weightTooltip(spool)}
                    >
                      {getUsagePercentage(spool).toFixed(1)}% used / {getRemainingPercentage(spool).toFixed(1)}% left
                      {spool.initialWeightG ? ` of ${spool.initialWeightG.toFixed(0)}g` : ''}
                    </div>
                  </div>

                  {/* Length metrics not available in current DTO */}

                  {spool.location && (
                    <div className="text-xs text-pf-text-secondary">Location: {spool.location}</div>
                  )}

                  {spool.lotNumber && (
                    <div className="text-xs text-pf-text-secondary">Lot: {spool.lotNumber}</div>
                  )}

                  {spool.lastUsedAt && (
                    <div className="text-xs text-pf-text-secondary">
                      Last used: {new Date(spool.lastUsedAt).toLocaleDateString()}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
          )}
          {viewMode === 'table' && (
            <div className="overflow-x-auto">
              <table className="min-w-full text-sm">
                <thead>
                  <tr className="text-left bg-pf-bg-2">
                    <th className="px-3 py-2 font-medium">ID</th>
                    <th className="px-3 py-2 font-medium">Color</th>
                    <th className="px-3 py-2 font-medium">Vendor</th>
                    <th className="px-3 py-2 font-medium">Material</th>
                    <th className="px-3 py-2 font-medium">Name</th>
                    <th className="px-3 py-2 font-medium">Remaining</th>
                    <th className="px-3 py-2 font-medium">Used %</th>
                    <th className="px-3 py-2 font-medium">Location</th>
                    <th className="px-3 py-2 font-medium">Archived</th>
                    <th className="px-3 py-2 font-medium">Edit</th>
                  </tr>
                </thead>
                <tbody>
                  {getDisplayedSpools().map(spool => (
                    <tr key={spool.id} className="border-t border-pf-border hover:bg-pf-bg-1">
                      <td className="px-3 py-2">{spool.id}</td>
                      <td className="px-3 py-2"><ColorSwatch color={getRepresentativeHex(classifyColor(spool.colorHex))} label={classifyColor(spool.colorHex)} /></td>
                      <td className="px-3 py-2">{spool.vendor || '—'}</td>
                      <td className="px-3 py-2">{spool.material || '—'}</td>
                      <td className="px-3 py-2">{spool.filamentName || spool.name || '—'}</td>
                      <td className="px-3 py-2">{formatWeight(spool.remainingWeightG)}</td>
                      <td className="px-3 py-2">{getUsagePercentage(spool).toFixed(1)}</td>
                      <td className="px-3 py-2">{spool.location || ''}</td>
                      <td className="px-3 py-2">{spool.archived ? 'Yes' : ''}</td>
                      <td className="px-3 py-2">
                        <a
                          href={spoolmanBaseUrl ? `${spoolmanBaseUrl.replace(/\/$/, '')}/spools/edit/${spool.id}` : `/spools/edit/${spool.id}`}
                          target={spoolmanBaseUrl ? '_blank' : undefined}
                          rel={spoolmanBaseUrl ? 'noopener noreferrer' : undefined}
                          className="text-blue-400 hover:text-blue-300 underline inline-flex items-center gap-1"
                        >
                          <Pencil className="h-3 w-3" /> Edit
                        </a>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
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
