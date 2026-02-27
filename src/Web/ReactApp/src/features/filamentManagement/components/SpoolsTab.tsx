import { useState, useEffect, useTransition, useCallback, useMemo, useRef } from 'react';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import {
  FilterIcon,
  RefreshIcon,
  ExternalLinkIcon,
  PackageIcon,
  EditIcon,
  GridIcon,
  TableIcon,
  GearIcon,
  CloseIcon,
  DownloadIcon,
  UploadIcon,
  ArrowUpIcon,
  ArrowDownIcon,
  PlusIcon,
  DeleteIcon,
} from '@/common/components/icons/MdiIcons';
import { classifyColor, getRepresentativeHex } from '@/common/utils/colorFamilies';
import { Button, Checkbox, Select, FileUpload } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { ColorFamilySelect } from '@/features/filamentManagement/components/ColorFamilySelect';
import { ColorSwatch } from '@/features/filamentManagement/components/ColorSwatch';
import { SpoolCard } from '@/features/filamentManagement/components/SpoolCard';
import { SpoolTableView } from '@/features/filamentManagement/components/SpoolTableView';
import { EditSpoolModal } from '@/features/filamentManagement/components/EditSpoolModal';
import { AddSpoolModal } from '@/features/filamentManagement/components/AddSpoolModal';
import { BulkEditSpoolsModal } from '@/features/filamentManagement/components/BulkEditSpoolsModal';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { useDeleteSpool, useBulkDeleteSpools, useImportSpoolmanSpoolsCsv } from '@/common/hooks/useApi';
import { formatSpoolWeight, getUsagePercentage, getRemainingPercentage } from '@/features/filamentManagement/utils/formatters';
import type { SpoolmanSpoolDto, SpoolTableColumn } from '@/features/filamentManagement/types';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import '@/features/filamentManagement/components/spool-components.css';

interface FilterState {
  search: string;
  material: string;
  vendor: string;
  color: string;
  pageSize: string;
  location: string;
  showEmpty: boolean;
}

/**
 * SpoolsTab — Self-contained component displaying Spoolman spool inventory.
 * Supports card/table views, filtering, sorting, CSV export, and column config.
 */
export function SpoolsTab() {
  const [spools, setSpools] = useState<SpoolmanSpoolDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [spoolmanError, setSpoolmanError] = useState<string | null>(null);
  const [spoolmanBaseUrl, setSpoolmanBaseUrl] = useState('');
  const csvFileInputRef = useRef<HTMLInputElement>(null);
  const importCsvMutation = useImportSpoolmanSpoolsCsv();
  const [,startTransition] = useTransition();
  const [filters, setFilters] = useState<FilterState>({
    search: '',
    material: '',
    vendor: '',
    color: '',
    pageSize: '50',
    location: '',
    showEmpty: false
  });
  const [sortField, setSortField] = useState<string>('id');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [viewMode, setViewMode] = useState<'cards' | 'table'>(() => {
    const saved = localStorage.getItem('spools-view-mode');
    return saved === 'table' ? 'table' : 'cards';
  });

  // Persist view mode preference
  useEffect(() => {
    localStorage.setItem('spools-view-mode', viewMode);
  }, [viewMode]);
  const [showColumnConfig, setShowColumnConfig] = useState(false);
  const [health, setHealth] = useState<{configured: boolean; success: boolean; message?: string} | null>(null);

  // CRUD state
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
  const [isBulkEditOpen, setIsBulkEditOpen] = useState(false);
  const [editingSpool, setEditingSpool] = useState<SpoolmanSpoolDto | null>(null);
  const [isAddOpen, setIsAddOpen] = useState(false);
  const [cloningSpool, setCloningSpool] = useState<SpoolmanSpoolDto | null>(null);
  const [deleteConfirm, setDeleteConfirm] = useState<{ type: 'single'; spool: SpoolmanSpoolDto } | { type: 'bulk' } | null>(null);
  const deleteSpoolMutation = useDeleteSpool();
  const bulkDeleteMutation = useBulkDeleteSpools();

  const defaultColumns: SpoolTableColumn[] = [
    { id: 'id', label: 'ID', visible: true, sortable: true, render: s => s.id, sortValue: s => s.id },
    { id: 'color', label: 'Color', visible: true, sortable: true, render: s => <ColorSwatch color={getRepresentativeHex(classifyColor(s.colorHex))} label={classifyColor(s.colorHex)} />, sortValue: s => classifyColor(s.colorHex).toLowerCase() },
    { id: 'vendor', label: 'Vendor', visible: true, sortable: true, render: s => (s.vendor || '—'), sortValue: s => (s.vendor || '').toLowerCase() },
    { id: 'material', label: 'Material', visible: true, sortable: true, render: s => (s.material || '—'), sortValue: s => (s.material || '').toLowerCase() },
    { id: 'name', label: 'Name', visible: true, sortable: true, render: s => (s.filamentName || s.name || '—'), sortValue: s => (s.filamentName || s.name || '').toLowerCase() },
    { id: 'remaining', label: 'Remaining', visible: true, sortable: true, render: s => formatSpoolWeight(s.remainingWeightG), sortValue: s => (s.remainingWeightG ?? -Infinity) },
    { id: 'usedPercent', label: 'Used %', visible: true, sortable: true, render: s => getUsagePercentage(s).toFixed(1), sortValue: s => getUsagePercentage(s) },
    { id: 'location', label: 'Location', visible: true, sortable: true, render: s => (s.location || ''), sortValue: s => (s.location || '').toLowerCase() },
    { id: 'archived', label: 'Archived', visible: true, sortable: true, render: s => (s.archived ? 'Yes' : ''), sortValue: s => (s.archived ? 1 : 0) },
  ];

  const [tableColumns, setTableColumns] = useState<SpoolTableColumn[]>(() => {
    try {
      const raw = localStorage.getItem('spool-table-columns');
      if (raw) {
        const parsed = JSON.parse(raw) as { id: string; visible: boolean }[];
        const used = new Set<string>();
        const result: SpoolTableColumn[] = [];
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

  // One-time health probe
  useEffect(() => {
    const run = async () => {
      try {
        const data = await apiClient.getSpoolmanHealth();
        setHealth(data as { configured: boolean; success: boolean; message?: string });
      } catch { /* ignore */ }
    };
    run();
  }, []);

  // Keyboard shortcuts for spools management
  useKeyboardShortcuts([
    {
      key: 'f',
      handler: () => {
        const filtersElement = document.querySelector('[data-testid="spool-filters"]');
        filtersElement?.scrollIntoView({ behavior: 'smooth' });
      },
      description: 'Focus on filters'
    },
    {
      key: 'v',
      handler: () => setViewMode(viewMode === 'cards' ? 'table' : 'cards'),
      description: 'Toggle view mode (cards/table)'
    }
  ]);

  // Persist column visibility/order
  useEffect(() => {
    try {
      const minimal = tableColumns.map(c => ({ id: c.id, visible: c.visible }));
      localStorage.setItem('spool-table-columns', JSON.stringify(minimal));
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

  // Drag & drop support
  const [dragColId, setDragColId] = useState<string | null>(null);
  const onDragStart = (e: React.DragEvent<HTMLLIElement>, id: string) => {
    setDragColId(id);
    e.dataTransfer.effectAllowed = 'move';
    try { e.dataTransfer.setData('text/plain', id); } catch { /* ignore */ }
  };
  const onDragOver = (e: React.DragEvent<HTMLLIElement>) => {
    e.preventDefault();
    try {
      if (e.dataTransfer) {
        e.dataTransfer.dropEffect = 'move';
      }
    } catch { /* ignore */ }
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

  const hasActiveSpoolFilters = filters.search !== '' || filters.material !== '' || filters.vendor !== '' || filters.color !== '' || filters.location !== '' || filters.showEmpty;
  const resetSpoolFilters = () => setFilters(prev => ({ ...prev, search: '', material: '', vendor: '', color: '', location: '', showEmpty: false }));

  const loadSpools = useCallback(async () => {
    startTransition(async () => {
      try {
        setSpoolmanError(null);
        const parsedPageSize = parseInt(filters.pageSize, 10);
        const serverLimit = filters.pageSize === 'All' || !Number.isFinite(parsedPageSize)
          ? undefined
          : parsedPageSize;
        const data = await apiClient.getSpools(serverLimit);
        const list: SpoolmanSpoolDto[] = Array.isArray(data) ? (data as SpoolmanSpoolDto[]) : ((data as Record<string, unknown>).items as SpoolmanSpoolDto[] || []);
        setSpools(list);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Unknown error';
        setSpoolmanError(`Failed to load spools: ${message}`);
        setSpools([]);
      } finally {
        setLoading(false);
      }
    });
  }, [filters.pageSize, startTransition]);

  useEffect(() => {
    const loadConfig = async () => {
      try {
        const cfg = await apiClient.getSpoolmanConfig();
        if ((cfg as Record<string, unknown>)?.baseUrl) setSpoolmanBaseUrl((cfg as Record<string, unknown>).baseUrl as string);
        else {
          const saved = localStorage.getItem('spoolman-base-url');
          if (saved) setSpoolmanBaseUrl(saved);
        }
      } catch {
        const saved = localStorage.getItem('spoolman-base-url');
        if (saved) setSpoolmanBaseUrl(saved);
      }
    };
    loadConfig();
  }, []);

  useEffect(() => {
    void loadSpools();
  }, [loadSpools]);

  const reload = () => {
    setLoading(true);
    setSelectedIds(new Set());
    loadSpools();
  };

  const toggleSelect = (id: number) => {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const filteredSpools = useMemo((): SpoolmanSpoolDto[] => spools.filter(spool => {
    if (filters.search) {
      const q = filters.search.toLowerCase();
      const matchesSearch =
        (spool.filamentName || spool.name || '').toLowerCase().includes(q) ||
        (spool.vendor || '').toLowerCase().includes(q) ||
        (spool.material || '').toLowerCase().includes(q) ||
        (spool.location || '').toLowerCase().includes(q) ||
        (spool.lotNumber || '').toLowerCase().includes(q) ||
        String(spool.id).includes(q);
      if (!matchesSearch) return false;
    }
    if (filters.material && !spool.material?.toLowerCase().includes(filters.material.toLowerCase())) return false;
    if (filters.vendor && !(spool.vendor || '').toLowerCase().includes(filters.vendor.toLowerCase())) return false;
    if (filters.color && classifyColor(spool.colorHex) !== filters.color) return false;
    if (filters.location && !(spool.location || '').toLowerCase().includes(filters.location.toLowerCase())) return false;
    if (!filters.showEmpty) {
      const remaining = typeof spool.remainingWeightG === 'number' ? spool.remainingWeightG : (spool.initialWeightG != null && spool.usedWeightG != null ? (spool.initialWeightG - spool.usedWeightG) : null);
      if (remaining != null && remaining <= 0) return false;
      if (remaining == null && typeof spool.remainingPercent === 'number' && spool.remainingPercent <= 0) return false;
    }
    return true;
  }), [spools, filters.search, filters.material, filters.vendor, filters.color, filters.location, filters.showEmpty]);

  const displayedSpools = useMemo((): SpoolmanSpoolDto[] => {
    const filtered = [...filteredSpools];
    filtered.sort((a, b) => {
      const dir = sortDir === 'asc' ? 1 : -1;
      const col = tableColumns.find(c => c.id === sortField);
      const val = (f: SpoolmanSpoolDto): string | number => {
        if (col?.sortValue) return col.sortValue(f);
        switch (sortField) {
          case 'vendor': return (f.vendor || '').toLowerCase();
          case 'material': return (f.material || '').toLowerCase();
          case 'remaining': return f.remainingWeightG ?? -Infinity;
          case 'usedPercent': return getUsagePercentage(f);
          case 'color': return classifyColor(f.colorHex).toLowerCase();
          case 'location': return (f.location || '').toLowerCase();
          case 'name': return (f.filamentName || f.name || '').toLowerCase();
          case 'archived': return f.archived ? 1 : 0;
          default: return f.id;
        }
      };
      const av = val(a);
      const bv = val(b);
      if (av < bv) return -1 * dir;
      if (av > bv) return 1 * dir;
      return 0;
    });
    if (filters.pageSize === 'All') return filtered;
    const pageSize = parseInt(filters.pageSize);
    return filtered.slice(0, pageSize);
  }, [filteredSpools, sortField, sortDir, tableColumns, filters.pageSize]);

  const getMaterialOptions = (): string[] => [...new Set(spools.map(s => s.material).filter((m): m is string => !!m))].sort();
  const getVendorOptions = (): string[] => [...new Set(spools.map(s => s.vendor).filter((v): v is string => !!v))].sort();
  const getLocationOptions = (): string[] => [...new Set(spools.map(s => s.location).filter((l): l is string => !!l))].sort();
  const getColorFamilyOptions = (): string[] => [...new Set(spools.map(s => classifyColor(s.colorHex)))]
    .filter(f => f && f !== 'Unknown')
    .sort();

  const handleExportCsv = () => {
    const rows = [
      ['id','name','vendor','material','filamentName','colorHex','initialWeightG','remainingWeightG','usedWeightG','usedPercent','remainingPercent','location','lotNumber','archived'],
      ...displayedSpools.map(s => [
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


  const someSelected = selectedIds.size > 0;
  const allSelected = displayedSpools.length > 0 && displayedSpools.every(s => selectedIds.has(s.id));
  const toggleSelectAll = () => {
    if (allSelected) setSelectedIds(new Set());
    else setSelectedIds(new Set(displayedSpools.map(s => s.id)));
  };

  const handleConfirmDelete = async () => {
    if (!deleteConfirm) return;
    if (deleteConfirm.type === 'single') {
      try {
        await deleteSpoolMutation.mutateAsync(deleteConfirm.spool.id);
        toast.success(`Deleted spool #${deleteConfirm.spool.id}.`);
        reload();
      } catch {
        toast.error('Failed to delete spool.');
      }
    } else {
      try {
        const result = await bulkDeleteMutation.mutateAsync([...selectedIds]);
        if (result.errorCount > 0) {
          toast.error(`Deleted ${result.updatedCount}, failed ${result.errorCount}.`);
        } else {
          toast.success(`Deleted ${result.updatedCount} spool${result.updatedCount !== 1 ? 's' : ''}.`);
        }
        reload();
      } catch {
        toast.error('Bulk delete failed.');
      }
    }
    setDeleteConfirm(null);
  };

  const isDeleting = deleteSpoolMutation.isPending || bulkDeleteMutation.isPending;

  if (loading) {
    return (
      <div className="space-y-4">
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
    <div className="space-y-4">
      <FileUpload
        ref={csvFileInputRef}
        accept=".csv"
        className="hidden"
        label="Import spools from CSV file"
        onChange={(files) => {
          const file = files?.[0];
          if (!file) return;
          importCsvMutation.mutateAsync(file).then((result) => {
            toast.success(`CSV import: ${result.updatedCount} imported, ${result.errorCount} errors.`);
            reload();
          }).catch(() => {
            toast.error('Failed to import spools from CSV.');
          });
          if (csvFileInputRef.current) csvFileInputRef.current.value = '';
        }}
      />

      {health && (!health.configured || !health.success) && (
        <div className="bg-amber-900/40 border border-amber-700 text-amber-200 px-4 py-3 rounded-sm">
          {!health.configured ? (
            <span>Spoolman is not configured yet. Set a base URL in Settings to enable spool tracking.</span>
          ) : (
            <span>Spoolman connection failed{health.message ? `: ${health.message}` : ''}. You can reconfigure under Settings.</span>
          )}
        </div>
      )}
      <div className="flex justify-between items-center">
        <h2 className="text-xl font-bold text-pf-text-primary">Spools ({filteredSpools.length})</h2>
        <div className="flex gap-2 items-center">
          <Button
            size="sm"
            variant="secondary"
            onClick={handleExportCsv}
            aria-label="Export spools to CSV"
            title="Export spools to CSV"
          >
            <DownloadIcon className="h-4 w-4 mr-1" />
          </Button>
          <Button
            size="sm"
            variant="secondary"
            title="Import spools from CSV file"
            disabled={importCsvMutation.isPending}
            onClick={() => csvFileInputRef.current?.click()}
          >
            <UploadIcon className="h-4 w-4 mr-1" />
          </Button>
          <Button
            variant="primary"
            size="sm"
            title="Add a new spool"
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
              aria-controls="column-config-panel"
              onClick={() => setShowColumnConfig(!showColumnConfig)}
              disabled={viewMode !== 'table'}
            >
              <GearIcon className="h-4 w-4" />
            </Button>
            {showColumnConfig && viewMode === 'table' && (
              <div id="column-config-panel" className="absolute right-0 mt-2 w-72 z-20 bg-pf-bg-1 border border-pf-border rounded-sm shadow-lg p-3 space-y-2" role="dialog" aria-label="Column configuration">
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
                        id={`col-${c.id}`}
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
            onClick={loadSpools}
            disabled={!spoolmanBaseUrl}
            aria-label="Refresh spools"
            title="Refresh spools"
          >
            <RefreshIcon className="h-4 w-4" />
          </Button>
          {spoolmanBaseUrl && (
            <a
              href={spoolmanBaseUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded-sm text-pf-text-primary hover:bg-pf-bg-2 active:bg-pf-bg-3 flex items-center gap-1 transition-colors duration-150 focus:outline-hidden focus:ring-1 focus:ring-blue-500"
              aria-label="Open Spoolman"
              title="Open Spoolman"
            >
              <ExternalLinkIcon className="h-4 w-4" />
            </a>
          )}
        </div>
      </div>

      {spoolmanError && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded-sm flex items-center gap-3">
          <PackageIcon className="h-5 w-5 shrink-0" />
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
          <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4" data-testid="spool-filters">
            <div className="flex items-center gap-2 mb-3">
              <FilterIcon className="h-4 w-4 text-pf-text-secondary" />
              <span className="text-sm font-medium text-pf-text-primary">Filters:</span>
              {hasActiveSpoolFilters && (
                <Button
                  size="sm"
                  variant="subtle"
                  onClick={resetSpoolFilters}
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
                <label htmlFor="spool-search" className="text-xs text-pf-text-secondary">Search</label>
                <input
                  id="spool-search"
                  type="search"
                  value={filters.search}
                  onChange={e => setFilters(prev => ({ ...prev, search: e.target.value }))}
                  placeholder="Name, vendor, material..."
                  className="w-56 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded-sm text-sm text-pf-text-primary placeholder:text-pf-text-secondary/60 focus:outline-hidden focus:ring-1 focus:ring-blue-500"
                  aria-label="Search spools"
                />
              </div>
              <div className="flex flex-col gap-1">
                <label className="text-xs text-pf-text-secondary">Material</label>
                <Select
                  aria-label="Filter by material"
                  value={filters.material}
                  onChange={(e) => setFilters(prev => ({ ...prev, material: e.target.value }))}
                  className="w-40"
                >
                  <option value="">All Materials</option>
                  {getMaterialOptions().map(material => (
                    <option key={material} value={material}>{material}</option>
                  ))}
                </Select>
              </div>
              
              <div className="flex flex-col gap-1">
                <label className="text-xs text-pf-text-secondary">Vendor</label>
                <Select
                  aria-label="Filter by vendor"
                  value={filters.vendor}
                  onChange={(e) => setFilters(prev => ({ ...prev, vendor: e.target.value }))}
                  className="w-40"
                >
                  <option value="">All Vendors</option>
                  {getVendorOptions().map(vendor => (
                    <option key={vendor} value={vendor}>{vendor}</option>
                  ))}
                </Select>
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-pf-text-secondary">Color</label>
                <ColorFamilySelect
                  value={filters.color}
                  onChange={(val) => setFilters(prev => ({ ...prev, color: val }))}
                  options={getColorFamilyOptions()}
                  placeholder="All Colors"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-xs text-pf-text-secondary">Location</label>
                <Select
                  aria-label="Filter by location"
                  value={filters.location}
                  onChange={(e) => setFilters(prev => ({ ...prev, location: e.target.value }))}
                  className="w-40"
                >
                  <option value="">All Locations</option>
                  {getLocationOptions().map(loc => (
                    <option key={loc} value={loc}>{loc}</option>
                  ))}
                </Select>
              </div>

              {viewMode === 'cards' && (
                <div className="flex flex-col gap-1">
                  <label className="text-xs text-pf-text-secondary" htmlFor="sort-field">Sort</label>
                  <div className="flex gap-1 items-center">
                    <Select
                      id="sort-field"
                      aria-label="Sort field"
                      value={sortField}
                      onChange={e => setSortField(e.target.value)}
                      className="w-40"
                    >
                      <option value="id">ID</option>
                      <option value="vendor">Vendor</option>
                      <option value="material">Material</option>
                      <option value="remaining">Remaining (g)</option>
                      <option value="usedPercent">Used %</option>
                      <option value="color">Color</option>
                      <option value="location">Location</option>
                      <option value="name">Name</option>
                      <option value="archived">Archived</option>
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

              <div className="ml-auto flex flex-wrap gap-2 items-center text-sm">
                <div className="flex items-center gap-1.5">
                  <label htmlFor="spool-page-size" className="text-xs text-pf-text-secondary">Show</label>
                  <Select
                    id="spool-page-size"
                    aria-label="Page size"
                    value={filters.pageSize}
                    onChange={(e) => setFilters(prev => ({ ...prev, pageSize: e.target.value }))}
                    className="w-20"
                  >
                    <option value="10">10</option>
                    <option value="25">25</option>
                    <option value="50">50</option>
                    <option value="100">100</option>
                    <option value="All">All</option>
                  </Select>
                </div>
                <span className="text-pf-text-secondary">Showing {displayedSpools.length} of {filteredSpools.length}</span>
                
                <label className="flex items-center gap-1 text-xs cursor-pointer">
                  <Checkbox
                    aria-label="Show empty spools"
                    checked={filters.showEmpty}
                    onChange={e => setFilters(prev => ({ ...prev, showEmpty: e.target.checked }))}
                  />
                  Show empty
                </label>
              </div>
            </div>
          </div>

          {viewMode === 'cards' && (
            <div className="text-xs text-pf-text-secondary flex flex-wrap gap-4 items-center">
              <div className="flex items-center gap-1"><span className="inline-block px-2 py-0.5 text-[10px] rounded-sm bg-red-600/20 text-red-300 border border-red-600/40 uppercase tracking-wide">Archived</span> = Spool not for active use</div>
              <div>Usage shows Used % / Remaining % (hover for weight details)</div>
            </div>
          )}

          {/* Bulk selection toolbar */}
          {someSelected && (
            <div className="bg-blue-900/30 border border-blue-700/50 rounded-xl px-4 py-3 flex items-center justify-between">
              <div className="flex items-center gap-3">
                <span className="text-sm font-medium text-blue-200">
                  {selectedIds.size} spool{selectedIds.size !== 1 ? 's' : ''} selected
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

          {viewMode === 'cards' && (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
              {displayedSpools.map((spool) => (
                <SpoolCard
                  key={spool.id}
                  spool={spool}
                  isSelected={selectedIds.has(spool.id)}
                  onToggleSelect={() => toggleSelect(spool.id)}
                  onEdit={() => setEditingSpool(spool)}
                  onClone={() => setCloningSpool(spool)}
                  onDelete={() => setDeleteConfirm({ type: 'single', spool })}
                />
              ))}
            </div>
          )}
          {viewMode === 'table' && (
            <SpoolTableView
              spools={displayedSpools}
              tableColumns={tableColumns}
              selectedIds={selectedIds}
              allSelected={allSelected}
              sortField={sortField}
              sortDir={sortDir}
              onSort={(field, dir) => { setSortField(field); setSortDir(dir); }}
              onToggleSelect={toggleSelect}
              onToggleSelectAll={toggleSelectAll}
              onEdit={(s) => setEditingSpool(s)}
              onClone={(s) => setCloningSpool(s)}
              onDelete={(s) => setDeleteConfirm({ type: 'single', spool: s })}
            />
          )}
          {displayedSpools.length === 0 && filteredSpools.length > 0 && (
            <div className="text-center py-8 text-pf-text-secondary">
              No spools match the current filters.
            </div>
          )}
        </>
      )}

      {!spoolmanError && spools.length === 0 && !loading && (
        <div className="text-center py-8">
          <PackageIcon className="h-16 w-16 text-pf-text-secondary mx-auto mb-4" />
          <div className="text-pf-text-secondary">
            No spools found. Make sure your Spoolman instance is running and accessible.
          </div>
        </div>
      )}

      {/* Edit Spool Modal */}
      <EditSpoolModal
        key={editingSpool?.id ?? 'none'}
        isOpen={editingSpool !== null}
        onClose={() => setEditingSpool(null)}
        spool={editingSpool}
        onSuccess={reload}
      />

      {/* Add / Clone Spool Modal */}
      <AddSpoolModal
        isOpen={isAddOpen || cloningSpool !== null}
        onClose={() => { setIsAddOpen(false); setCloningSpool(null); }}
        sourceSpool={cloningSpool ?? undefined}
        onSuccess={reload}
      />

      {/* Bulk Edit Spools Modal */}
      <BulkEditSpoolsModal
        isOpen={isBulkEditOpen}
        onClose={() => setIsBulkEditOpen(false)}
        selectedIds={[...selectedIds]}
        onSuccess={reload}
      />

      {/* Delete Confirmation Modal */}
      <Modal
        isOpen={deleteConfirm !== null}
        onClose={() => setDeleteConfirm(null)}
        title={deleteConfirm?.type === 'bulk' ? 'Delete Spools?' : 'Delete Spool?'}
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
            Are you sure you want to delete spool <strong>#{deleteConfirm.spool.id}</strong> ({deleteConfirm.spool.filamentName || 'unnamed'})? This action cannot be undone.
          </p>
        ) : deleteConfirm?.type === 'bulk' ? (
          <p className="text-pf-text-secondary">
            Are you sure you want to delete <strong>{selectedIds.size}</strong> spool{selectedIds.size !== 1 ? 's' : ''}? This action cannot be undone.
          </p>
        ) : null}
      </Modal>
    </div>
  );
}
