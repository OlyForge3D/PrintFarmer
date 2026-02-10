import { useState, useEffect, useMemo, useTransition, useCallback, useRef } from 'react';
import {
  FilterIcon,
  RefreshIcon,
  PackageIcon,
  GridIcon,
  TableIcon,
  ArrowUpIcon,
  ArrowDownIcon,
  DownloadIcon,
  UploadIcon,
  DatabaseIcon,
} from '@/common/components/icons/MdiIcons';
import { Button, Select, FileUpload } from '@/common/components/ui';
import { ColorSwatch } from '@/features/catalog/components/ColorSwatch';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import { SpoolmanDbBrowserModal } from '@/features/catalog/components/SpoolmanDbBrowserModal';
import { apiClient } from '@/services/api';
import { useExportFilamentTypesCsv, useImportFilamentTypesCsv } from '@/common/hooks/useApi';
import type { SpoolmanFilament } from '@/types/api';
import { toast } from 'sonner';

interface FilterState {
  material: string;
  vendor: string;
  search: string;
}

type SortField = 'name' | 'vendor' | 'material' | 'diameter' | 'weight' | 'extruderTemp' | 'bedTemp' | 'price';

/**
 * FilamentsTab — Displays Spoolman filament product definitions (not physical spools).
 * Supports card/table views, search, and filtering by material/vendor.
 */
export function FilamentsTab() {
  const [filaments, setFilaments] = useState<SpoolmanFilament[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [, startTransition] = useTransition();
  const [viewMode, setViewMode] = useState<'cards' | 'table'>('cards');
  const [filters, setFilters] = useState<FilterState>({ material: '', vendor: '', search: '' });
  const [sortField, setSortField] = useState<SortField>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [isSpoolmanDbOpen, setIsSpoolmanDbOpen] = useState(false);
  const csvFileInputRef = useRef<HTMLInputElement>(null);
  const exportCsvMutation = useExportFilamentTypesCsv();
  const importCsvMutation = useImportFilamentTypesCsv();

  // Load filaments on mount
  const loadFilaments = useCallback(async () => {
    startTransition(async () => {
      try {
        setError(null);
        const data = await apiClient.getFilaments();
        setFilaments(data);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Unknown error';
        setError(`Failed to load filaments: ${message}`);
        setFilaments([]);
      } finally {
        setLoading(false);
      }
    });
  }, [startTransition]);

  useEffect(() => {
    loadFilaments();
  }, [loadFilaments]);

  const reload = () => {
    setLoading(true);
    loadFilaments();
  };

  const materialOptions = useMemo(() =>
    [...new Set(filaments.map(f => f.material).filter((m): m is string => !!m))].sort(),
    [filaments]
  );

  const vendorOptions = useMemo(() =>
    [...new Set(filaments.map(f => f.vendor).filter((v): v is string => !!v))].sort(),
    [filaments]
  );

  const filteredFilaments = useMemo(() => {
    let result = filaments;
    if (filters.material) {
      result = result.filter(f => f.material?.toLowerCase() === filters.material.toLowerCase());
    }
    if (filters.vendor) {
      result = result.filter(f => f.vendor?.toLowerCase() === filters.vendor.toLowerCase());
    }
    if (filters.search) {
      const q = filters.search.toLowerCase();
      result = result.filter(f =>
        (f.name || '').toLowerCase().includes(q) ||
        (f.vendor || '').toLowerCase().includes(q) ||
        (f.material || '').toLowerCase().includes(q) ||
        (f.articleNumber || '').toLowerCase().includes(q)
      );
    }
    return result;
  }, [filaments, filters]);

  const sortedFilaments = useMemo(() => {
    const dir = sortDir === 'asc' ? 1 : -1;
    return [...filteredFilaments].sort((a, b) => {
      const val = (f: SpoolmanFilament): string | number => {
        switch (sortField) {
          case 'name': return (f.name || '').toLowerCase();
          case 'vendor': return (f.vendor || '').toLowerCase();
          case 'material': return (f.material || '').toLowerCase();
          case 'diameter': return f.diameter ?? -Infinity;
          case 'weight': return f.weight ?? -Infinity;
          case 'extruderTemp': return f.settingsExtruderTemp ?? -Infinity;
          case 'bedTemp': return f.settingsBedTemp ?? -Infinity;
          case 'price': return f.price ?? -Infinity;
        }
      };
      const av = val(a);
      const bv = val(b);
      if (av < bv) return -1 * dir;
      if (av > bv) return 1 * dir;
      return 0;
    });
  }, [filteredFilaments, sortField, sortDir]);

  const formatTemp = (temp?: number | null): string => temp != null ? `${temp}°C` : '—';
  const formatWeight = (w?: number | null): string => w != null ? `${w}g` : '—';
  const formatPrice = (p?: number | null): string => p != null ? `$${p.toFixed(2)}` : '—';
  const formatDiameter = (d?: number | null): string => d != null ? `${d}mm` : '—';

  if (loading) {
    return (
      <div className="space-y-4">
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4" aria-label="Loading filaments">
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className="bg-pf-bg-1 border border-pf-border rounded-xl p-4 space-y-3">
              <div className="flex items-center gap-2">
                <div className="skeleton-base skeleton-pill w-4 h-4 rounded-full" />
                <Skeleton width="60%" />
              </div>
              <Skeleton width="40%" />
              <Skeleton width="80%" />
            </div>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {error && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded-sm flex items-center gap-3">
          <PackageIcon className="h-5 w-5 shrink-0" />
          <div>
            <div className="font-medium">Spoolman Connection Error</div>
            <div className="text-sm">{error}</div>
          </div>
        </div>
      )}

      {/* Hidden file input for CSV import */}
      <FileUpload
        ref={csvFileInputRef}
        accept=".csv"
        className="hidden"
        label="Import filament types from CSV file"
        onChange={(files) => {
          const file = files?.[0];
          if (!file) return;
          importCsvMutation.mutateAsync(file).then((result) => {
            toast.success(`CSV import: ${result.createdCount} created, ${result.updatedCount} updated, ${result.errorCount} errors.`);
            reload();
          }).catch(() => {
            toast.error('Failed to import filament types from CSV.');
          });
          if (csvFileInputRef.current) csvFileInputRef.current.value = '';
        }}
      />

      <div className="flex justify-between items-center">
        <h2 className="text-xl font-bold text-pf-text-primary">Filaments ({sortedFilaments.length})</h2>
        <div className="flex gap-2 items-center">
          <Button
            variant="secondary"
            size="sm"
            title="Export filament types to CSV"
            disabled={exportCsvMutation.isPending || filaments.length === 0}
            onClick={async () => {
              try {
                const blob = await exportCsvMutation.mutateAsync();
                const url = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = url;
                a.download = 'filament-types.csv';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                URL.revokeObjectURL(url);
                toast.success('Filament types exported to CSV.');
              } catch {
                toast.error('Failed to export filament types.');
              }
            }}
            iconLeft={<DownloadIcon className="h-4 w-4 mr-1" />}
          >
            Export CSV
          </Button>
          <Button
            variant="secondary"
            size="sm"
            title="Import filament types from CSV file"
            disabled={importCsvMutation.isPending}
            onClick={() => csvFileInputRef.current?.click()}
            iconLeft={<UploadIcon className="h-4 w-4 mr-1" />}
          >
            {importCsvMutation.isPending ? 'Importing...' : 'Import CSV'}
          </Button>
          <Button
            variant="secondary"
            size="sm"
            title="Browse and import from SpoolmanDB community database"
            onClick={() => setIsSpoolmanDbOpen(true)}
            iconLeft={<DatabaseIcon className="h-4 w-4 mr-1" />}
          >
            SpoolmanDB
          </Button>
          <div className="flex rounded-sm overflow-hidden border border-pf-border">
            <Button
              variant={viewMode === 'cards' ? 'primary' : 'secondary'}
              size="sm"
              aria-label="Card view"
              title="Card view"
              onClick={() => setViewMode('cards')}
            >
              <GridIcon className="h-4 w-4" />
            </Button>
            <Button
              variant={viewMode === 'table' ? 'primary' : 'secondary'}
              size="sm"
              aria-label="Table view"
              title="Table view"
              onClick={() => setViewMode('table')}
            >
              <TableIcon className="h-4 w-4" />
            </Button>
          </div>
          <Button
            variant="primary"
            size="sm"
            onClick={reload}
            disabled={loading}
            aria-label="Refresh filaments"
            title="Refresh filaments"
          >
            <RefreshIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Filters */}
      {filaments.length > 0 && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4">
          <div className="flex items-center gap-2 mb-3">
            <FilterIcon className="h-4 w-4 text-pf-text-secondary" />
            <span className="text-sm font-medium text-pf-text-primary">Filters:</span>
          </div>
          <div className="flex flex-wrap gap-4 items-end">
            <div className="flex flex-col gap-1">
              <label htmlFor="filament-search" className="text-xs text-pf-text-secondary">Search</label>
              <input
                id="filament-search"
                type="search"
                value={filters.search}
                onChange={e => setFilters(prev => ({ ...prev, search: e.target.value }))}
                placeholder="Name, vendor, material..."
                className="w-56 px-3 py-1.5 bg-pf-bg-0 border border-pf-border rounded-sm text-sm text-pf-text-primary placeholder:text-pf-text-secondary/60 focus:outline-hidden focus:ring-1 focus:ring-blue-500"
                aria-label="Search filaments"
              />
            </div>
            <div className="flex flex-col gap-1">
              <label className="text-xs text-pf-text-secondary">Material</label>
              <Select
                aria-label="Filter by material"
                value={filters.material}
                onChange={e => setFilters(prev => ({ ...prev, material: e.target.value }))}
                className="w-40"
              >
                <option value="">All Materials</option>
                {materialOptions.map(m => (
                  <option key={m} value={m}>{m}</option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <label className="text-xs text-pf-text-secondary">Vendor</label>
              <Select
                aria-label="Filter by vendor"
                value={filters.vendor}
                onChange={e => setFilters(prev => ({ ...prev, vendor: e.target.value }))}
                className="w-40"
              >
                <option value="">All Vendors</option>
                {vendorOptions.map(v => (
                  <option key={v} value={v}>{v}</option>
                ))}
              </Select>
            </div>
            {viewMode === 'cards' && (
              <div className="flex flex-col gap-1">
                <label className="text-xs text-pf-text-secondary" htmlFor="filament-sort">Sort</label>
                <div className="flex gap-1 items-center">
                  <Select
                    id="filament-sort"
                    aria-label="Sort field"
                    value={sortField}
                    onChange={e => setSortField(e.target.value as SortField)}
                    className="w-40"
                  >
                    <option value="name">Name</option>
                    <option value="vendor">Vendor</option>
                    <option value="material">Material</option>
                    <option value="diameter">Diameter</option>
                    <option value="weight">Weight</option>
                    <option value="extruderTemp">Extruder Temp</option>
                    <option value="bedTemp">Bed Temp</option>
                    <option value="price">Price</option>
                  </Select>
                  <Button
                    size="sm"
                    variant="subtle"
                    aria-label="Toggle sort direction"
                    onClick={() => setSortDir(prev => prev === 'asc' ? 'desc' : 'asc')}
                    iconLeft={sortDir === 'asc' ? <ArrowUpIcon className="h-3 w-3" /> : <ArrowDownIcon className="h-3 w-3" />}
                  />
                </div>
              </div>
            )}
            <div className="ml-auto text-sm text-pf-text-secondary">
              Showing {sortedFilaments.length} of {filaments.length}
            </div>
          </div>
        </div>
      )}

      {/* Card view */}
      {viewMode === 'cards' && sortedFilaments.length > 0 && (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {sortedFilaments.map(f => (
            <div
              key={f.id}
              className="bg-pf-bg-1 border border-pf-border rounded-xl p-4 hover:bg-pf-bg-secondary transition-colors"
            >
              <div className="flex items-center gap-2 mb-3">
                <ColorSwatch color={f.colorHex || '#888888'} label={f.name || 'Unknown'} />
                <div className="text-sm font-medium text-pf-text-primary truncate">
                  {f.name || 'Unnamed'}
                </div>
              </div>
              <div className="space-y-1.5">
                <div className="text-xs text-pf-text-secondary">{f.vendor || 'Unknown Vendor'}</div>
                {f.material && (
                  <span className="inline-block px-2 py-0.5 text-[10px] rounded-sm bg-blue-600/20 text-blue-300 border border-blue-600/40 uppercase tracking-wide">
                    {f.material}
                  </span>
                )}
                <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-pf-text-secondary mt-2">
                  <span>Dia: {formatDiameter(f.diameter)}</span>
                  <span>Wt: {formatWeight(f.weight)}</span>
                </div>
                {(f.settingsExtruderTemp != null || f.settingsBedTemp != null) && (
                  <div className="flex gap-4 text-xs text-pf-text-secondary">
                    {f.settingsExtruderTemp != null && <span>Extruder: {formatTemp(f.settingsExtruderTemp)}</span>}
                    {f.settingsBedTemp != null && <span>Bed: {formatTemp(f.settingsBedTemp)}</span>}
                  </div>
                )}
                {f.price != null && (
                  <div className="text-xs font-medium text-pf-text-primary">{formatPrice(f.price)}</div>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Table view */}
      {viewMode === 'table' && sortedFilaments.length > 0 && (
        <div className="overflow-x-auto relative">
          <table className="min-w-full text-sm">
            <thead>
              <tr className="text-left bg-pf-bg-2">
                {([
                  { id: 'color' as const, label: 'Color', sortable: false },
                  { id: 'name' as const, label: 'Name', sortable: true },
                  { id: 'vendor' as const, label: 'Vendor', sortable: true },
                  { id: 'material' as const, label: 'Material', sortable: true },
                  { id: 'diameter' as const, label: 'Diameter', sortable: true },
                  { id: 'weight' as const, label: 'Weight', sortable: true },
                  { id: 'extruderTemp' as const, label: 'Extruder Temp', sortable: true },
                  { id: 'bedTemp' as const, label: 'Bed Temp', sortable: true },
                  { id: 'price' as const, label: 'Price', sortable: true },
                ] as const).map(col => {
                  const isSorted = sortField === col.id;
                  const ariaSort: 'ascending' | 'descending' | undefined = isSorted ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined;
                  return (
                    <th
                      key={col.id}
                      className={`px-3 py-2 font-medium ${col.sortable ? 'cursor-pointer select-none' : ''}`}
                      onClick={() => {
                        if (!col.sortable) return;
                        setSortField(col.id);
                        setSortDir(prev => (isSorted ? (prev === 'asc' ? 'desc' : 'asc') : 'asc'));
                      }}
                      {...(ariaSort ? { 'aria-sort': ariaSort } : {})}
                    >
                      <span className="inline-flex items-center gap-1">
                        {col.label}
                        {col.sortable && isSorted && (
                          sortDir === 'asc' ? <ArrowUpIcon className="h-3 w-3" /> : <ArrowDownIcon className="h-3 w-3" />
                        )}
                      </span>
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {sortedFilaments.map(f => (
                <SelectableRow key={f.id} className="border-t border-pf-border" isSelected={false}>
                  <td className="px-3 py-2"><ColorSwatch color={f.colorHex || '#888888'} label={f.name || 'Unknown'} /></td>
                  <td className="px-3 py-2">{f.name || '—'}</td>
                  <td className="px-3 py-2">{f.vendor || '—'}</td>
                  <td className="px-3 py-2">{f.material || '—'}</td>
                  <td className="px-3 py-2">{formatDiameter(f.diameter)}</td>
                  <td className="px-3 py-2">{formatWeight(f.weight)}</td>
                  <td className="px-3 py-2">{formatTemp(f.settingsExtruderTemp)}</td>
                  <td className="px-3 py-2">{formatTemp(f.settingsBedTemp)}</td>
                  <td className="px-3 py-2">{formatPrice(f.price)}</td>
                </SelectableRow>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {sortedFilaments.length === 0 && filaments.length > 0 && (
        <div className="text-center py-8 text-pf-text-secondary">
          No filaments match the current filters.
        </div>
      )}

      {!error && filaments.length === 0 && !loading && (
        <div className="text-center py-8">
          <PackageIcon className="h-16 w-16 text-pf-text-secondary mx-auto mb-4" />
          <div className="text-pf-text-secondary">
            No filaments found. Make sure your Spoolman instance is running and has filament data.
          </div>
        </div>
      )}

      {/* SpoolmanDB Browser Modal */}
      <SpoolmanDbBrowserModal
        isOpen={isSpoolmanDbOpen}
        onClose={() => { setIsSpoolmanDbOpen(false); reload(); }}
      />
    </div>
  );
}
