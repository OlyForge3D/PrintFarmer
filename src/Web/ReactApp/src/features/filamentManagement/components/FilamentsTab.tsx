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
  EditIcon,
  CloseIcon,
  DeleteIcon,
  PlusIcon,
  GearIcon,
} from '@/common/components/icons/MdiIcons';
import { Button, Select, FileUpload } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { classifyColor } from '@/common/utils/colorFamilies';
import { ColorFamilySelect } from '@/features/filamentManagement/components/ColorFamilySelect';
import { FilamentCard } from '@/features/filamentManagement/components/FilamentCard';
import { FilamentTableView } from '@/features/filamentManagement/components/FilamentTableView';
import { ColorSwatch } from '@/features/filamentManagement/components/ColorSwatch';
import { OpenFilamentDbBrowserModal } from '@/features/filamentManagement/components/OpenFilamentDbBrowserModal';
import { BulkEditFilamentsModal } from '@/features/filamentManagement/components/BulkEditFilamentsModal';
import { EditFilamentModal } from '@/features/filamentManagement/components/EditFilamentModal';
import { AddFilamentModal } from '@/features/filamentManagement/components/AddFilamentModal';
import { Modal } from '@/common/components/modals/Modal';
import { apiClient } from '@/services/api';
import { useExportSpoolmanFilamentsCsv, useImportSpoolmanFilamentsCsv, useDeleteFilament, useBulkDeleteFilaments } from '@/common/hooks/useApi';
import { formatTemp, formatFilamentWeight, formatPrice, formatDiameter } from '@/features/filamentManagement/utils/formatters';
import type { SpoolmanFilament } from '@/types/api';
import type { FilamentTableColumn } from '@/features/filamentManagement/types';
import { toast } from 'sonner';

interface FilterState {
  material: string;
  vendor: string;
  color: string;
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
  const [viewMode, setViewMode] = useState<'cards' | 'table'>(() => {
    const saved = localStorage.getItem('filaments-view-mode');
    return saved === 'table' ? 'table' : 'cards';
  });

  // Persist view mode preference
  useEffect(() => {
    localStorage.setItem('filaments-view-mode', viewMode);
  }, [viewMode]);
  const [filters, setFilters] = useState<FilterState>({ material: '', vendor: '', color: '', search: '' });
  const [sortField, setSortField] = useState<SortField>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState<number>(() => {
    const saved = localStorage.getItem('filaments-page-size');
    return saved ? Number(saved) : 50;
  });

  const [isOfdOpen, setIsOfdOpen] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
  const [isBulkEditOpen, setIsBulkEditOpen] = useState(false);
  const [editingFilament, setEditingFilament] = useState<SpoolmanFilament | null>(null);
  const [isAddOpen, setIsAddOpen] = useState(false);
  const [cloningFilament, setCloningFilament] = useState<SpoolmanFilament | null>(null);
  const [deleteConfirm, setDeleteConfirm] = useState<{ type: 'single'; filament: SpoolmanFilament } | { type: 'bulk' } | null>(null);
  const csvFileInputRef = useRef<HTMLInputElement>(null);
  const exportCsvMutation = useExportSpoolmanFilamentsCsv();
  const importCsvMutation = useImportSpoolmanFilamentsCsv();
  const deleteFilamentMutation = useDeleteFilament();
  const bulkDeleteMutation = useBulkDeleteFilaments();

  // --- Column configuration ---
  const [showColumnConfig, setShowColumnConfig] = useState(false);

  const defaultColumns: FilamentTableColumn[] = useMemo(() => [
    { id: 'color', label: 'Color', visible: true, sortable: false, render: f => <ColorSwatch color={f.colorHex || '#888888'} label={f.name || 'Unknown'} />, sortValue: () => '' },
    { id: 'name', label: 'Name', visible: true, sortable: true, render: f => f.name || '—', sortValue: f => (f.name || '').toLowerCase() },
    { id: 'vendor', label: 'Vendor', visible: true, sortable: true, render: f => f.vendor || '—', sortValue: f => (f.vendor || '').toLowerCase() },
    { id: 'material', label: 'Material', visible: true, sortable: true, render: f => f.material || '—', sortValue: f => (f.material || '').toLowerCase() },
    { id: 'diameter', label: 'Diameter', visible: true, sortable: true, render: f => formatDiameter(f.diameter), sortValue: f => f.diameter ?? -Infinity },
    { id: 'weight', label: 'Weight', visible: true, sortable: true, render: f => formatFilamentWeight(f.weight), sortValue: f => f.weight ?? -Infinity },
    { id: 'extruderTemp', label: 'Extruder Temp', visible: true, sortable: true, render: f => formatTemp(f.settingsExtruderTemp), sortValue: f => f.settingsExtruderTemp ?? -Infinity },
    { id: 'bedTemp', label: 'Bed Temp', visible: true, sortable: true, render: f => formatTemp(f.settingsBedTemp), sortValue: f => f.settingsBedTemp ?? -Infinity },
    { id: 'price', label: 'Price', visible: true, sortable: true, render: f => formatPrice(f.price), sortValue: f => f.price ?? -Infinity },
  ], []);

  const [tableColumns, setTableColumns] = useState<FilamentTableColumn[]>(() => {
    try {
      const raw = localStorage.getItem('filament-table-columns');
      if (raw) {
        const parsed = JSON.parse(raw) as { id: string; visible: boolean }[];
        const used = new Set<string>();
        const result: FilamentTableColumn[] = [];
        for (const p of parsed) {
          const def = defaultColumns.find(d => d.id === p.id);
          if (def) {
            result.push({ ...def, visible: p.visible });
            used.add(def.id);
          }
        }
        for (const def of defaultColumns) {
          if (!used.has(def.id)) result.push(def);
        }
        if (result.length) return result;
      }
    } catch { /* ignore */ }
    return defaultColumns;
  });

  // Persist column visibility/order
  useEffect(() => {
    try {
      const minimal = tableColumns.map(c => ({ id: c.id, visible: c.visible }));
      localStorage.setItem('filament-table-columns', JSON.stringify(minimal));
    } catch { /* ignore */ }
  }, [tableColumns]);

  const moveColumn = (id: string, dir: -1 | 1) => {
    setTableColumns(cols => {
      const idx = cols.findIndex(c => c.id === id);
      if (idx === -1) return cols;
      const newIdx = idx + dir;
      if (newIdx < 0 || newIdx >= cols.length) return cols;
      const copy = [...cols];
      const [item] = copy.splice(idx, 1);
      copy.splice(newIdx, 0, item);
      return copy;
    });
  };

  const toggleColumnVisibility = (id: string) => {
    setTableColumns(cols => {
      const visibleCount = cols.filter(c => c.visible).length;
      return cols.map(c => {
        if (c.id !== id) return c;
        if (c.visible && visibleCount === 1) return c;
        return { ...c, visible: !c.visible };
      });
    });
  };

  // Drag & drop column reorder
  const [dragColId, setDragColId] = useState<string | null>(null);
  const onDragStart = (e: React.DragEvent<HTMLLIElement>, id: string) => {
    setDragColId(id);
    e.dataTransfer.effectAllowed = 'move';
    try { e.dataTransfer.setData('text/plain', id); } catch { /* ignore */ }
  };
  const onDragOver = (e: React.DragEvent<HTMLLIElement>) => {
    e.preventDefault();
    try { if (e.dataTransfer) { e.dataTransfer.dropEffect = 'move'; } } catch { /* ignore */ }
  };
  const onDrop = (e: React.DragEvent<HTMLLIElement>, targetId: string) => {
    e.preventDefault();
    const sourceId = dragColId || (() => { try { return e.dataTransfer.getData('text/plain'); } catch { return ''; } })();
    if (!sourceId || sourceId === targetId) return;
    setTableColumns(cols => {
      const sourceIdx = cols.findIndex(c => c.id === sourceId);
      const targetIdx = cols.findIndex(c => c.id === targetId);
      if (sourceIdx === -1 || targetIdx === -1) return cols;
      const copy = [...cols];
      const [moved] = copy.splice(sourceIdx, 1);
      copy.splice(targetIdx, 0, moved);
      return copy;
    });
    setDragColId(null);
  };

  const hasActiveFilters = filters.material !== '' || filters.vendor !== '' || filters.color !== '' || filters.search !== '';
  const resetFilters = () => setFilters({ material: '', vendor: '', color: '', search: '' });

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
    setSelectedIds(new Set());
    loadFilaments();
  };

  const toggleSelect = (id: number) => {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const materialOptions = useMemo(() =>
    [...new Set(filaments.map(f => f.material).filter((m): m is string => !!m))].sort(),
    [filaments]
  );

  const vendorOptions = useMemo(() =>
    [...new Set(filaments.map(f => f.vendor).filter((v): v is string => !!v))].sort(),
    [filaments]
  );

  const colorFamilyOptions = useMemo(() =>
    [...new Set(filaments.map(f => classifyColor(f.colorHex)).filter(Boolean))].sort(),
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
    if (filters.color) {
      result = result.filter(f => classifyColor(f.colorHex) === filters.color);
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

  // Pagination
  const totalPages = pageSize > 0 ? Math.ceil(sortedFilaments.length / pageSize) : 1;
  const pagedFilaments = useMemo(() => {
    if (pageSize <= 0) return sortedFilaments; // "All"
    const start = (page - 1) * pageSize;
    return sortedFilaments.slice(start, start + pageSize);
  }, [sortedFilaments, page, pageSize]);

  // Reset page when filters or sort change
  useEffect(() => { setPage(1); }, [filters, sortField, sortDir]);

  // Persist page size preference
  useEffect(() => { localStorage.setItem('filaments-page-size', String(pageSize)); }, [pageSize]);

  // These MUST be defined after sortedFilaments to avoid TDZ errors in production builds
  const toggleSelectAll = () => {
    if (selectedIds.size === pagedFilaments.length) {
      setSelectedIds(new Set());
    } else {
      setSelectedIds(new Set(pagedFilaments.map(f => f.id)));
    }
  };

  const allSelected = pagedFilaments.length > 0 && pagedFilaments.every(f => selectedIds.has(f.id));
  const someSelected = selectedIds.size > 0;

  const handleConfirmDelete = async () => {
    if (!deleteConfirm) return;
    if (deleteConfirm.type === 'single') {
      try {
        await deleteFilamentMutation.mutateAsync(deleteConfirm.filament.id);
        toast.success(`Deleted "${deleteConfirm.filament.name || 'filament'}".`);
        reload();
      } catch {
        toast.error('Failed to delete filament.');
      }
    } else {
      try {
        const result = await bulkDeleteMutation.mutateAsync([...selectedIds]);
        if (result.errorCount > 0) {
          toast.error(`Deleted ${result.updatedCount}, failed ${result.errorCount}.`);
        } else {
          toast.success(`Deleted ${result.updatedCount} filament${result.updatedCount !== 1 ? 's' : ''}.`);
        }
        reload();
      } catch {
        toast.error('Bulk delete failed.');
      }
    }
    setDeleteConfirm(null);
  };

  const isDeleting = deleteFilamentMutation.isPending || bulkDeleteMutation.isPending;

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
        label="Import filaments from CSV file"
        onChange={(files) => {
          const file = files?.[0];
          if (!file) return;
          importCsvMutation.mutateAsync(file).then((result) => {
            toast.success(`CSV import: ${result.updatedCount} imported, ${result.errorCount} errors.`);
            reload();
          }).catch(() => {
            toast.error('Failed to import filaments from CSV.');
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
            title="Export Spoolman filaments to CSV"
            disabled={exportCsvMutation.isPending || filaments.length === 0}
            onClick={async () => {
              try {
                const blob = await exportCsvMutation.mutateAsync();
                const url = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = url;
                a.download = 'spoolman-filaments.csv';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                URL.revokeObjectURL(url);
                toast.success('Filaments exported to CSV.');
              } catch {
                toast.error('Failed to export filaments.');
              }
            }}
          >
            <DownloadIcon className="h-4 w-4 mr-1" />
          </Button>
          <Button
            variant="secondary"
            size="sm"
            title="Import filaments from CSV file"
            disabled={importCsvMutation.isPending}
            onClick={() => csvFileInputRef.current?.click()}
          >
            <UploadIcon className="h-4 w-4 mr-1" />
          </Button>
          <Button
            variant="secondary"
            size="sm"
            title="Browse and import from Open Filament Database"
            onClick={() => setIsOfdOpen(true)}
          >
            <DatabaseIcon className="h-4 w-4 mr-1" />
          </Button>
          <Button
            variant="primary"
            size="sm"
            title="Add a new filament"
            onClick={() => setIsAddOpen(true)}
          >
            <PlusIcon className="h-4 w-4 mr-1" />
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
          <div className="relative">
            <Button
              size="sm"
              variant="secondary"
              aria-label="Configure columns"
              title="Configure columns"
              aria-haspopup="dialog"
              aria-expanded={showColumnConfig && viewMode === 'table'}
              aria-controls="filament-column-config-panel"
              onClick={() => setShowColumnConfig(!showColumnConfig)}
              disabled={viewMode !== 'table'}
            >
              <GearIcon className="h-4 w-4" />
            </Button>
            {showColumnConfig && viewMode === 'table' && (
              <div id="filament-column-config-panel" className="absolute right-0 mt-2 w-72 z-20 bg-pf-bg-1 border border-pf-border rounded-sm shadow-lg p-3 space-y-2" role="dialog" aria-label="Column configuration">
                <div className="flex justify-between items-center mb-1">
                  <div className="text-xs font-medium text-pf-text-secondary">Visible Columns</div>
                  <Button
                    size="sm"
                    variant="subtle"
                    onClick={() => setShowColumnConfig(false)}
                    aria-label="Close column configuration"
                    iconLeft={<CloseIcon className="h-3 w-3" />}
                  />
                </div>
                <ul className="space-y-1 max-h-64 overflow-auto" aria-label="Column list">
                  {tableColumns.map((c, i) => (
                    <li
                      key={c.id}
                      className={`flex items-center gap-2 group rounded-sm ${dragColId === c.id ? 'bg-blue-600/20' : 'hover:bg-pf-bg-2'}`}
                      draggable
                      onDragStart={(e) => onDragStart(e, c.id)}
                      onDragOver={onDragOver}
                      onDrop={(e) => onDrop(e, c.id)}
                      data-col-id={c.id}
                      data-dragging={dragColId === c.id ? 'true' : 'false'}
                      role="listitem"
                    >
                      <Checkbox
                        id={`filament-col-${c.id}`}
                        checked={c.visible}
                        onChange={() => toggleColumnVisibility(c.id)}
                        aria-label={`Toggle column ${c.label}`}
                      />
                      <span className="text-xs flex-1 truncate">{c.label}</span>
                      <div className="flex gap-1">
                        <Button
                          size="sm"
                          variant="subtle"
                          onClick={() => moveColumn(c.id, -1)}
                          disabled={i === 0}
                          aria-label={`Move ${c.label} up`}
                          iconLeft={<ArrowUpIcon className="h-3 w-3" />}
                        />
                        <Button
                          size="sm"
                          variant="subtle"
                          onClick={() => moveColumn(c.id, 1)}
                          disabled={i === tableColumns.length - 1}
                          aria-label={`Move ${c.label} down`}
                          iconLeft={<ArrowDownIcon className="h-3 w-3" />}
                        />
                      </div>
                    </li>
                  ))}
                </ul>
                <div className="text-[10px] text-pf-text-secondary pt-1 border-t border-pf-border">Reorder with arrows. At least one column must remain visible.</div>
              </div>
            )}
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
            {hasActiveFilters && (
              <Button
                size="sm"
                variant="subtle"
                onClick={resetFilters}
                aria-label="Reset all filters"
                title="Reset all filters"
                iconLeft={<CloseIcon className="h-3 w-3" />}
              >
                Reset
              </Button>
            )}
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
                className="w-56 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded-sm text-sm text-pf-text-primary placeholder:text-pf-text-secondary/60 focus:outline-hidden focus:ring-1 focus:ring-blue-500"
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
            <div className="flex flex-col gap-1">
              <label className="text-xs text-pf-text-secondary">Color</label>
              <ColorFamilySelect
                value={filters.color}
                onChange={val => setFilters(prev => ({ ...prev, color: val }))}
                options={colorFamilyOptions}
                placeholder="All Colors"
              />
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
            <div className="flex items-center gap-3 ml-auto">
              <div className="flex items-center gap-1.5">
                <label htmlFor="filament-page-size" className="text-xs text-pf-text-secondary">Show</label>
                <Select
                  id="filament-page-size"
                  aria-label="Page size"
                  value={String(pageSize)}
                  onChange={e => { const v = Number(e.target.value); setPageSize(v); setPage(1); }}
                  className="w-20"
                >
                  <option value="10">10</option>
                  <option value="25">25</option>
                  <option value="50">50</option>
                  <option value="100">100</option>
                  <option value="0">All</option>
                </Select>
              </div>
              <span className="text-sm text-pf-text-secondary">
                Showing {pagedFilaments.length} of {sortedFilaments.length}{sortedFilaments.length !== filaments.length ? ` (${filaments.length} total)` : ''}
              </span>
            </div>
          </div>
        </div>
      )}

      {/* Bulk edit toolbar */}
      {someSelected && (
        <div className="bg-blue-900/30 border border-blue-700/50 rounded-xl px-4 py-3 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <span className="text-sm font-medium text-blue-200">
              {selectedIds.size} filament{selectedIds.size !== 1 ? 's' : ''} selected
            </span>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setSelectedIds(new Set())}
              iconLeft={<CloseIcon className="h-3 w-3 mr-1" />}
            >
              Clear
            </Button>
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="primary"
              size="sm"
              onClick={() => setIsBulkEditOpen(true)}
              iconLeft={<EditIcon className="h-4 w-4 mr-1" />}
            >
              Bulk Edit
            </Button>
            <Button
              variant="danger"
              size="sm"
              onClick={() => setDeleteConfirm({ type: 'bulk' })}
              iconLeft={<DeleteIcon className="h-4 w-4 mr-1" />}
            >
              Delete
            </Button>
          </div>
        </div>
      )}

      {/* Card view */}
      {viewMode === 'cards' && sortedFilaments.length > 0 && (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {pagedFilaments.map(f => (
            <FilamentCard
              key={f.id}
              filament={f}
              isSelected={selectedIds.has(f.id)}
              onToggleSelect={() => toggleSelect(f.id)}
              onEdit={() => setEditingFilament(f)}
              onClone={() => setCloningFilament(f)}
              onDelete={() => setDeleteConfirm({ type: 'single', filament: f })}
            />
          ))}
        </div>
      )}

      {/* Table view */}
      {viewMode === 'table' && sortedFilaments.length > 0 && (
        <FilamentTableView
          filaments={pagedFilaments}
          selectedIds={selectedIds}
          allSelected={allSelected}
          sortField={sortField}
          sortDir={sortDir}
          tableColumns={tableColumns}
          onToggleSelect={toggleSelect}
          onToggleSelectAll={toggleSelectAll}
          onSort={(field, dir) => { setSortField(field as SortField); setSortDir(dir); }}
          onEdit={f => setEditingFilament(f)}
          onClone={f => setCloningFilament(f)}
          onDelete={f => setDeleteConfirm({ type: 'single', filament: f })}
        />
      )}

      {/* Pagination controls */}
      {totalPages > 1 && (
        <div className="flex items-center justify-center gap-2 pt-2">
          <Button
            variant="secondary"
            size="sm"
            disabled={page <= 1}
            onClick={() => setPage(p => Math.max(1, p - 1))}
            aria-label="Previous page"
          >
            ← Prev
          </Button>
          <span className="text-sm text-pf-text-secondary">
            Page {page} of {totalPages}
          </span>
          <Button
            variant="secondary"
            size="sm"
            disabled={page >= totalPages}
            onClick={() => setPage(p => Math.min(totalPages, p + 1))}
            aria-label="Next page"
          >
            Next →
          </Button>
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

      <OpenFilamentDbBrowserModal
        isOpen={isOfdOpen}
        onClose={() => { setIsOfdOpen(false); reload(); }}
      />

      {/* Bulk Edit Modal */}
      <BulkEditFilamentsModal
        isOpen={isBulkEditOpen}
        onClose={() => setIsBulkEditOpen(false)}
        selectedIds={[...selectedIds]}
        onSuccess={reload}
      />

      {/* Individual Edit Modal */}
      <EditFilamentModal
        isOpen={editingFilament !== null}
        onClose={() => setEditingFilament(null)}
        filament={editingFilament}
        onSuccess={reload}
      />

      {/* Add / Clone Filament Modal */}
      <AddFilamentModal
        isOpen={isAddOpen || cloningFilament !== null}
        onClose={() => { setIsAddOpen(false); setCloningFilament(null); }}
        sourceFilament={cloningFilament ?? undefined}
        onSuccess={reload}
      />

      {/* Delete Confirmation Modal */}
      <Modal
        isOpen={deleteConfirm !== null}
        onClose={() => setDeleteConfirm(null)}
        title={deleteConfirm?.type === 'bulk' ? 'Delete Filaments?' : 'Delete Filament?'}
        width="max-w-sm"
        footer={
          <div className="flex gap-3">
            <Button variant="secondary" onClick={() => setDeleteConfirm(null)} disabled={isDeleting}>
              Cancel
            </Button>
            <Button variant="danger" onClick={handleConfirmDelete} disabled={isDeleting}>
              {isDeleting ? 'Deleting...' : 'Delete'}
            </Button>
          </div>
        }
      >
        {deleteConfirm?.type === 'single' ? (
          <p className="text-pf-text-secondary">
            Are you sure you want to delete <strong>{deleteConfirm.filament.name || 'this filament'}</strong>? This action cannot be undone.
          </p>
        ) : deleteConfirm?.type === 'bulk' ? (
          <p className="text-pf-text-secondary">
            Are you sure you want to delete <strong>{selectedIds.size}</strong> filament{selectedIds.size !== 1 ? 's' : ''}? This action cannot be undone.
          </p>
        ) : null}
      </Modal>
    </div>
  );
}
