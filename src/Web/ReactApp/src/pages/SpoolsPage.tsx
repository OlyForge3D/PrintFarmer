import { useState, useEffect } from 'react';
import { 
  FilterIcon, 
  RefreshIcon, 
  ExternalLinkIcon, 
  PackageIcon, 
  EditIcon, 
  GridIcon, 
  TableIcon, 
  GearIcon 
} from '@/components/icons/MdiIcons';
import { classifyColor, getRepresentativeHex } from '@/utils/colorFamilies';
import { normalizeSpoolmanBaseUrl } from '@/utils/validation';
import { Button, Checkbox, Select } from '@/components/ui';
import { ColorFamilySelect } from '@/components/ColorFamilySelect';
import { ColorSwatch } from '@/components/ColorSwatch';
import { SpoolUsageBar } from '@/components/SpoolUsageBar';
import { Skeleton } from '@/components/Skeleton';
import { PageTemplate } from '@/components/PageTemplate';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
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
  showEmpty: boolean; // include empty (0 remaining) spools
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
	showArchived: 'active',
  showEmpty: false
  });
  const [sortField, setSortField] = useState<string>('id');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [viewMode, setViewMode] = useState<'cards' | 'table'>('cards');
  const [showColumnConfig, setShowColumnConfig] = useState(false);
  const [health, setHealth] = useState<{configured: boolean; success: boolean; message?: string} | null>(null);

  interface TableColumn {
    id: string;
    label: string;
    visible: boolean;
    sortable?: boolean;
    render: (spool: SpoolmanSpoolDto) => React.ReactNode;
    sortValue?: (spool: SpoolmanSpoolDto) => string | number;
  }

  const defaultColumns: TableColumn[] = [
    { id: 'id', label: 'ID', visible: true, sortable: true, render: s => s.id, sortValue: s => s.id },
    { id: 'color', label: 'Color', visible: true, sortable: true, render: s => <ColorSwatch color={getRepresentativeHex(classifyColor(s.colorHex))} label={classifyColor(s.colorHex)} />, sortValue: s => classifyColor(s.colorHex).toLowerCase() },
    { id: 'vendor', label: 'Vendor', visible: true, sortable: true, render: s => (s.vendor || '—'), sortValue: s => (s.vendor || '').toLowerCase() },
    { id: 'material', label: 'Material', visible: true, sortable: true, render: s => (s.material || '—'), sortValue: s => (s.material || '').toLowerCase() },
    { id: 'name', label: 'Name', visible: true, sortable: true, render: s => (s.filamentName || s.name || '—'), sortValue: s => (s.filamentName || s.name || '').toLowerCase() },
    { id: 'remaining', label: 'Remaining', visible: true, sortable: true, render: s => formatWeight(s.remainingWeightG), sortValue: s => (s.remainingWeightG ?? -Infinity) },
    { id: 'usedPercent', label: 'Used %', visible: true, sortable: true, render: s => getUsagePercentage(s).toFixed(1), sortValue: s => getUsagePercentage(s) },
    { id: 'location', label: 'Location', visible: true, sortable: true, render: s => (s.location || ''), sortValue: s => (s.location || '').toLowerCase() },
    { id: 'archived', label: 'Archived', visible: true, sortable: true, render: s => (s.archived ? 'Yes' : ''), sortValue: s => (s.archived ? 1 : 0) },
    { id: 'edit', label: 'Edit', visible: true, sortable: false, render: s => {
      const base = normalizeSpoolmanBaseUrl(spoolmanBaseUrl);
      const hasBase = !!base;
      const editUrl = hasBase ? `${base}/spool/edit/${s.id}` : '/settings';
      const title = hasBase ? `Edit spool ${s.id} in Spoolman` : 'Configure Spoolman URL first';
      return (
        <a
          href={editUrl}
          target={hasBase ? '_blank' : undefined}
          rel={hasBase ? 'noopener noreferrer' : undefined}
          className={`text-blue-400 underline inline-flex items-center gap-1 ${hasBase ? 'hover:text-blue-300' : 'opacity-60 hover:opacity-80'}`}
          aria-label={title}
          title={title}
        >
          <EditIcon className="h-3 w-3" />
        </a>
      );
    } }
  ];

  const [tableColumns, setTableColumns] = useState<TableColumn[]>(() => {
    try {
      const raw = localStorage.getItem('spool-table-columns');
      if (raw) {
        const parsed = JSON.parse(raw) as { id: string; visible: boolean }[]; // ordered array
        const used = new Set<string>();
        const result: TableColumn[] = [];
        for (const p of parsed) {
          const def = defaultColumns.find(d => d.id === p.id);
            if (def) {
              result.push({ ...def, visible: p.visible });
              used.add(def.id);
            }
        }
        // Append any new defaults not previously stored
        for (const def of defaultColumns) {
          if (!used.has(def.id)) result.push(def);
        }
        if (result.length) return result;
      }
    } catch { /* ignore */ }
    return defaultColumns;
  });

  // Update edit column render function when Spoolman base URL changes so links reflect latest config
  useEffect(() => {
    setTableColumns(cols => cols.map(c => {
      if (c.id !== 'edit') return c;
      return {
        ...c,
        render: (s: SpoolmanSpoolDto) => {
          const base = normalizeSpoolmanBaseUrl(spoolmanBaseUrl);
          const hasBase = !!base;
          const editUrl = hasBase ? `${base}/spool/edit/${s.id}` : '/settings';
          const title = hasBase ? `Edit spool ${s.id} in Spoolman` : 'Configure Spoolman URL first';
          return (
            <a
              href={editUrl}
              target={hasBase ? '_blank' : undefined}
              rel={hasBase ? 'noopener noreferrer' : undefined}
              className={`text-blue-400 underline inline-flex items-center gap-1 ${hasBase ? 'hover:text-blue-300' : 'opacity-60 hover:opacity-80'}`}
              aria-label={title}
              title={title}
            >
              <EditIcon className="h-3 w-3" />
            </a>
          );
        }
      };
    }));
  }, [spoolmanBaseUrl]);

  // One-time health probe
  useEffect(() => {
    const run = async () => {
      try {
        const r = await fetch(`${getApiBaseUrl()}/spoolman/health`, {
          headers: getAuthHeaders()
        });
        if (!r.ok) return;
        const data = await r.json();
        setHealth(data);
      } catch { /* ignore */ }
    };
    run();
  }, []);

  // Persist visibility/order (order in array) excluding heavy render funcs (just id+visible)
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
        if (c.visible && visibleCount === 1) return c; // prevent hiding last
        return { ...c, visible: !c.visible };
      });
    });
  };
  // Removed gradient hack – replaced by custom ColorFamilySelect component.

  const loadSpools = async () => {
    try {
      setLoading(true);
      setSpoolmanError(null);

  const response = await fetch(`${getApiBaseUrl()}/spoolman/spools`, { 
    headers: { 
      'Accept': 'application/json',
      ...getAuthHeaders()
    } 
  });
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
        const cfgResp = await fetch(`${getApiBaseUrl()}/spoolman/config`, {
          headers: getAuthHeaders()
        });
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
    if (!filters.showEmpty) {
      const remaining = typeof spool.remainingWeightG === 'number' ? spool.remainingWeightG : (spool.initialWeightG != null && spool.usedWeightG != null ? (spool.initialWeightG - spool.usedWeightG) : null);
      if (remaining != null && remaining <= 0) return false;
      // Fallback: if remaining percent available
      if (remaining == null && typeof spool.remainingPercent === 'number' && spool.remainingPercent <= 0) return false;
    }
    return true;
  });

  const getDisplayedSpools = (): SpoolmanSpoolDto[] => {
    let filtered = getFilteredSpools();
    filtered = [...filtered].sort((a, b) => {
      const dir = sortDir === 'asc' ? 1 : -1;
      // Column-based sort if defined
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
      <PageTemplate
        title="Spools"
        subtitle="Manage and monitor your filament spools from Spoolman"
        icon={PackageIcon}
      >
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
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Spools"
      subtitle="Manage and monitor your filament spools from Spoolman"
      icon={PackageIcon}
    >
      {health && (!health.configured || !health.success) && (
        <div className="bg-amber-900/40 border border-amber-700 text-amber-200 px-4 py-3 rounded">
          {!health.configured ? (
            <span>Spoolman is not configured yet. Set a base URL in Settings to enable spool tracking.</span>
          ) : (
            <span>Spoolman connection failed{health.message ? `: ${health.message}` : ''}. You can reconfigure under Settings.</span>
          )}
        </div>
      )}
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Spools</h1>
  <div className="flex gap-2 items-center">
          <div className="relative">
              {showColumnConfig ? (
                <Button
                  size="sm"
                  variant="secondary"
                  aria-label="Configure columns"
                  title="Configure columns"
                  aria-haspopup="dialog"
                  aria-expanded="true"
                  aria-controls="column-config-panel"
                  onClick={() => setShowColumnConfig(false)}
                >
                  <GearIcon className="h-4 w-4" />
                </Button>
              ) : (
                <Button
                  size="sm"
                  variant="secondary"
                  aria-label="Configure columns"
                  title="Configure columns"
                  aria-haspopup="dialog"
                  aria-expanded="false"
                  aria-controls="column-config-panel"
                  onClick={() => setShowColumnConfig(true)}
                >
                  <GearIcon className="h-4 w-4" />
                </Button>
              )}
              {showColumnConfig && (
                <div id="column-config-panel" className="absolute right-0 mt-2 w-72 z-20 bg-pf-bg-1 border border-pf-border rounded shadow-lg p-3 space-y-2" role="dialog" aria-label="Column configuration">
                  <div className="flex justify-between items-center mb-1">
                    <div className="text-xs font-medium text-pf-text-secondary">Visible Columns</div>
                    <Button
                      size="sm"
                      variant="subtle"
                      onClick={() => setShowColumnConfig(false)}
                      aria-label="Close column configuration"
                      className="text-xs px-1"
                    >✕</Button>
                  </div>
                  <ul className="space-y-1 max-h-64 overflow-auto" aria-label="Column list">
                    {tableColumns.map((c, i) => (
                      <li
                        key={c.id}
                        className={`flex items-center gap-2 group rounded ${dragColId === c.id ? 'bg-blue-600/20' : 'hover:bg-pf-bg-2'}`}
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
                            className="text-[10px] px-1 py-0.5"
                          >▲</Button>
                          <Button
                            size="sm"
                            variant="subtle"
                            onClick={() => moveColumn(c.id, 1)}
                            disabled={i === tableColumns.length - 1}
                            aria-label={`Move ${c.label} down`}
                            className="text-[10px] px-1 py-0.5"
                          >▼</Button>
                        </div>
                      </li>
                    ))}
                  </ul>
                  <div className="text-[10px] text-pf-text-secondary pt-1 border-t border-pf-border">Reorder with arrows. At least one column must remain visible.</div>
                </div>
              )}
            </div>
          <div className="flex rounded overflow-hidden border border-pf-border">
            <Button
              variant={viewMode === 'cards' ? 'primary' : 'secondary'}
              size="sm"
              aria-label="Card view"
              title="Card view"
              onClick={() => setViewMode('cards')}
              className="flex items-center gap-1"
            >
              <GridIcon className="h-4 w-4" />
            </Button>
            <Button
              variant={viewMode === 'table' ? 'primary' : 'secondary'}
              size="sm"
              aria-label="Table view"
              title="Table view"
              onClick={() => setViewMode('table')}
              className="flex items-center gap-1"
            >
              <TableIcon className="h-4 w-4" />
            </Button>
          </div>
          <Button
            variant="primary"
            onClick={loadSpools}
            disabled={loading || !spoolmanBaseUrl}
            aria-label="Refresh spools"
            title="Refresh spools"
            className="flex items-center gap-2"
          >
            <RefreshIcon className="h-4 w-4" />
          </Button>
          {spoolmanBaseUrl && (
            <a
              href={spoolmanBaseUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 active:bg-pf-bg-3 flex items-center gap-1 transition-colors duration-150 focus:outline-none focus:ring-1 focus:ring-blue-500"
              aria-label="Open Spoolman"
              title="Open Spoolman"
            >
              <ExternalLinkIcon className="h-4 w-4" />
            </a>
          )}
        </div>
      </div>

      {spoolmanError && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded flex items-center gap-3">
          <PackageIcon className="h-5 w-5 flex-shrink-0" />
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
                <FilterIcon className="h-4 w-4 text-pf-text-secondary" />
                <span className="text-sm font-medium text-pf-text-primary">Filters:</span>
              </div>
              
              <Select
                aria-label="Filter by material"
                value={filters.material}
                onChange={(e) => setFilters(prev => ({ ...prev, material: e.target.value }))}
              >
                <option value="">All Materials</option>
                {getMaterialOptions().map(material => (
                  <option key={material} value={material}>{material}</option>
                ))}
              </Select>
              
              <Select
                aria-label="Filter by vendor"
                value={filters.vendor}
                onChange={(e) => setFilters(prev => ({ ...prev, vendor: e.target.value }))}
              >
                <option value="">All Vendors</option>
                {getVendorOptions().map(vendor => (
                  <option key={vendor} value={vendor}>{vendor}</option>
                ))}
              </Select>

              <ColorFamilySelect
                value={filters.color}
                onChange={(val) => setFilters(prev => ({ ...prev, color: val }))}
                options={getColorFamilyOptions()}
                placeholder="All Colors"
              />

              <Select
                aria-label="Select page size"
                value={filters.pageSize}
                onChange={(e) => setFilters(prev => ({ ...prev, pageSize: e.target.value }))}
              >
                <option value="10">10 per page</option>
                <option value="50">50 per page</option>
                <option value="100">100 per page</option>
                <option value="All">Show All</option>
              </Select>

              <Select
                aria-label="Filter by location"
                value={filters.location}
                onChange={(e) => setFilters(prev => ({ ...prev, location: e.target.value }))}
              >
                <option value="">All Locations</option>
                {getLocationOptions().map(loc => (
                  <option key={loc} value={loc}>{loc}</option>
                ))}
              </Select>

              <Select
                aria-label="Filter archived"
                value={filters.showArchived}
                onChange={(e) => setFilters(prev => ({ ...prev, showArchived: e.target.value }))}
              >
                <option value="active">Active Only</option>
                <option value="all">All</option>
                <option value="archived">Archived Only</option>
              </Select>

              <div className="ml-auto flex items-center gap-2 text-sm text-pf-text-secondary">
                <span>Showing {getDisplayedSpools().length} of {getFilteredSpools().length} spools</span>
                <label className="flex items-center gap-1 text-xs cursor-pointer">
                  <Checkbox
                    aria-label="Show empty spools"
                    checked={filters.showEmpty}
                    onChange={e => setFilters(prev => ({ ...prev, showEmpty: e.target.checked }))}
                  />
                  Show empty
                </label>
                <label className="text-xs" htmlFor="sort-field">Sort:</label>
                <Select
                  id="sort-field"
                  aria-label="Sort field"
                  value={sortField}
                  onChange={e => setSortField(e.target.value)}
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
                  className="text-xs px-2 py-1"
                >{sortDir === 'asc' ? '▲' : '▼'}</Button>
                <Button
                  size="sm"
                  variant="success"
                  onClick={handleExportCsv}
                  className="text-xs px-2 py-1"
                >Export CSV</Button>
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
                    <div className="flex items-start justify-between gap-2">
                      <div className="text-sm font-medium text-pf-text-primary truncate">
                        {spool.vendor || 'Unknown Vendor'}
                      </div>
                      <div className="text-xs text-pf-text-secondary text-right whitespace-nowrap">
                        {spool.filamentName || spool.name || 'Unnamed'} [ {(spool.material || 'Unknown Material')} ]
                      </div>
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
                      className="text-xs text-pf-text-secondary flex justify-between items-center gap-2"
                      title={weightTooltip(spool)}
                      aria-label={weightTooltip(spool)}
                    >
                      <span>
                        {getUsagePercentage(spool).toFixed(1)}% used / {getRemainingPercentage(spool).toFixed(1)}% left
                        {spool.initialWeightG ? ` of ${spool.initialWeightG.toFixed(0)}g` : ''}
                      </span>
                      {spool.lastUsedAt && (
                        <span className="whitespace-nowrap text-pf-text-secondary/80" title={`Last used: ${new Date(spool.lastUsedAt).toLocaleDateString()}`}>{`Last used: ${new Date(spool.lastUsedAt).toLocaleDateString()}`}</span>
                      )}
                    </div>
                  </div>

                  {/* Length metrics not available in current DTO */}

                  {spool.location && (
                    <div className="text-xs text-pf-text-secondary">Location: {spool.location}</div>
                  )}

                  {spool.lotNumber && (
                    <div className="text-xs text-pf-text-secondary">Lot: {spool.lotNumber}</div>
                  )}

                  {/* Last used date now shown inline with usage row above */}
                </div>
              </div>
            ))}
          </div>
          )}
          {viewMode === 'table' && (
            <div className="overflow-x-auto relative">
              <table className="min-w-full text-sm">
                <thead>
                  <tr className="text-left bg-pf-bg-2">
                    {tableColumns.filter(c => c.visible).map(c => {
                      const isSorted = sortField === c.id;
                      const ariaSort: 'ascending' | 'descending' | undefined = isSorted ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined;
                      return (
                        <th
                          key={c.id}
                          data-col-id={c.id}
                          className={`px-3 py-2 font-medium ${c.sortable ? 'cursor-pointer select-none' : ''}`}
                          onClick={() => {
                            if (!c.sortable) return; 
                            setSortField(prev => prev === c.id ? prev : c.id);
                            setSortDir(prev => (isSorted ? (prev === 'asc' ? 'desc' : 'asc') : 'asc'));
                          }}
                          {...(ariaSort ? { 'aria-sort': ariaSort } : {})}
                        >
                          <span className="inline-flex items-center gap-1">
                            {c.label}
                            {c.sortable && isSorted && (
                              <span className="text-[10px]">{sortDir === 'asc' ? '▲' : '▼'}</span>
                            )}
                          </span>
                        </th>
                      );
                    })}
                  </tr>
                </thead>
                <tbody>
                  {getDisplayedSpools().map(spool => (
                    <tr key={spool.id} className="border-t border-pf-border hover:bg-pf-bg-1">
                      {tableColumns.filter(c => c.visible).map(c => (
                        <td key={c.id} className="px-3 py-2" data-col-id={c.id}>{c.render(spool)}</td>
                      ))}
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
          <PackageIcon className="h-16 w-16 text-pf-text-secondary mx-auto mb-4" />
          <div className="text-pf-text-secondary">
            No spools found. Make sure your Spoolman instance is running and accessible.
          </div>
        </div>
      )}
    </PageTemplate>
  );
}
