import { useState, useEffect, useMemo, useRef, useCallback, startTransition } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { RefreshIcon, SearchIcon, CheckIcon, CloseIcon, ChevronLeftIcon } from '@/common/components/icons/MdiIcons';
import { SpoolIcon } from '@/common/components/icons/SpoolIcon';
import { apiClient } from '@/services/api';
import type { SpoolmanSpool } from '@/types/api';
import clsx from 'clsx';

interface SpoolPickerModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSelect: (spoolId: number, spool: SpoolmanSpool) => void;
  printerId: string;
  activeSpoolId?: number;
}

type SortField = 'lastUsed' | 'weight' | 'name';
type SortDir = 'asc' | 'desc';

function formatDate(dateStr: string | null | undefined): string {
  if (!dateStr) return '—';
  const d = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - d.getTime();
  const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
  if (diffDays === 0) return 'Today';
  if (diffDays === 1) return 'Yesterday';
  if (diffDays < 30) return `${diffDays}d ago`;
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function formatWeight(spool: SpoolmanSpool): { remaining: string; percentage: number | null } {
  const remaining = spool.remainingWeightG;
  const initial = spool.initialWeightG;
  if (remaining == null) return { remaining: '—', percentage: null };
  const pct = initial != null && initial > 0 ? Math.round((remaining / initial) * 100) : null;
  return { remaining: `${Math.round(remaining)}g`, percentage: pct };
}

function getWeightColor(pct: number | null): string {
  if (pct === null) return 'text-pf-text-tertiary';
  if (pct > 50) return 'text-emerald-400';
  if (pct > 20) return 'text-amber-400';
  return 'text-red-400';
}

function getWeightBarColor(pct: number | null): string {
  if (pct === null) return 'bg-pf-text-tertiary/20';
  if (pct > 50) return 'bg-emerald-500';
  if (pct > 20) return 'bg-amber-500';
  return 'bg-red-500';
}

interface FilterDropdownProps {
  label: string;
  options: string[];
  value: string | null;
  onChange: (value: string | null) => void;
}

function FilterDropdown({ label, options, value, onChange }: FilterDropdownProps) {
  return (
    <div className="flex items-center gap-1.5">
      <span className="text-[10px] uppercase tracking-widest text-pf-text-tertiary">{label}</span>
      <Select
        value={value ?? ''}
        onChange={e => onChange(e.target.value || null)}
        containerClassName="w-auto"
        className={clsx(
          'bg-pf-bg-0 rounded-md px-2 py-1 text-xs transition-all cursor-pointer',
          value
            ? 'border-pf-accent/40 text-pf-accent bg-pf-accent/5'
            : 'border-pf-border text-pf-text-secondary'
        )}
        aria-label={`Filter by ${label}`}
      >
        <option value="">All</option>
        {options.map(opt => (
          <option key={opt} value={opt}>{opt}</option>
        ))}
      </Select>
    </div>
  );
}

/**
 * Two-step spool picker: user first selects a material type, then picks
 * from the filtered list of non-empty spools of that material.
 * Uses the central Spoolman inventory registered with PrintFarmer.
 */
export function SpoolPickerModal({ isOpen, onClose, onSelect, activeSpoolId }: SpoolPickerModalProps) {
  // Step 1: material selection
  const [materials, setMaterials] = useState<string[]>([]);
  const [materialsLoading, setMaterialsLoading] = useState(false);
  const [materialsError, setMaterialsError] = useState<string | null>(null);
  const [selectedMaterial, setSelectedMaterial] = useState<string | null>(null);

  // Step 2: spool list (loaded after material is selected)
  const [spools, setSpools] = useState<SpoolmanSpool[]>([]);
  const [spoolsLoading, setSpoolsLoading] = useState(false);
  const [spoolsError, setSpoolsError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [sortField, setSortField] = useState<SortField>('lastUsed');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const [vendorFilter, setVendorFilter] = useState<string | null>(null);
  const [locationFilter, setLocationFilter] = useState<string | null>(null);

  const requestIdRef = useRef(0);
  const searchInputRef = useRef<HTMLInputElement>(null);

  const loadMaterials = useCallback(async () => {
    setMaterialsLoading(true);
    setMaterialsError(null);
    try {
      const data = await apiClient.getAvailableMaterials();
      setMaterials(data);
    } catch (err) {
      setMaterialsError(err instanceof Error ? err.message : 'Failed to load materials');
      setMaterials([]);
    } finally {
      setMaterialsLoading(false);
    }
  }, []);

  const loadSpoolsForMaterial = useCallback(async (material: string) => {
    const requestId = ++requestIdRef.current;
    setSpoolsLoading(true);
    setSpoolsError(null);
    try {
      const result = await apiClient.getSpools({ material, limit: 500 });
      if (requestId !== requestIdRef.current) return;
      startTransition(() => {
        // Filter out empty spools (0g remaining)
        const nonEmpty = (Array.isArray(result.items) ? result.items : [])
          .filter(s => !s.archived && (s.remainingWeightG == null || s.remainingWeightG > 0));
        setSpools(nonEmpty);
      });
    } catch (err) {
      if (requestId !== requestIdRef.current) return;
      setSpoolsError(err instanceof Error ? err.message : 'Failed to load spools');
      setSpools([]);
    } finally {
      if (requestId === requestIdRef.current) setSpoolsLoading(false);
    }
  }, []);

  // Reset state on open
  useEffect(() => {
    if (isOpen) {
      void loadMaterials();
      setSelectedMaterial(null);
      setSpools([]);
      setSearch('');
      setVendorFilter(null);
      setLocationFilter(null);
    }
  }, [isOpen, loadMaterials]);

  // Load spools when material is selected
  useEffect(() => {
    if (selectedMaterial) {
      void loadSpoolsForMaterial(selectedMaterial);
      setSearch('');
      setVendorFilter(null);
      setLocationFilter(null);
      setTimeout(() => searchInputRef.current?.focus(), 100);
    }
  }, [selectedMaterial, loadSpoolsForMaterial]);

  const availableFilters = useMemo(() => {
    const vendors = [...new Set(spools.map(s => s.vendor).filter((v): v is string => !!v))].sort();
    const locations = [...new Set(spools.map(s => s.location).filter((l): l is string => !!l))].sort();
    return { vendors, locations };
  }, [spools]);

  const activeFilterCount = [vendorFilter, locationFilter].filter(Boolean).length;

  const filtered = useMemo(() => {
    const q = search.toLowerCase().trim();
    const list = spools.filter(s => {
      if (vendorFilter && s.vendor !== vendorFilter) return false;
      if (locationFilter && s.location !== locationFilter) return false;
      if (!q) return true;
      const searchable = [
        `#${String(s.id).padStart(3, '0')}`,
        s.vendor,
        s.filamentName ?? s.name,
      ].filter(Boolean).join(' ').toLowerCase();
      return searchable.includes(q);
    });

    list.sort((a, b) => {
      const dir = sortDir === 'asc' ? 1 : -1;
      switch (sortField) {
        case 'lastUsed': {
          const aDate = a.lastUsedAt ?? '';
          const bDate = b.lastUsedAt ?? '';
          return dir * bDate.localeCompare(aDate);
        }
        case 'weight':
          return dir * ((a.remainingWeightG ?? 0) - (b.remainingWeightG ?? 0));
        case 'name':
          return dir * (a.filamentName ?? a.name ?? '').localeCompare(b.filamentName ?? b.name ?? '');
        default:
          return 0;
      }
    });

    return list;
  }, [spools, search, sortField, sortDir, vendorFilter, locationFilter]);

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortDir(field === 'lastUsed' ? 'desc' : 'asc');
    }
  };

  const sortIndicator = (field: SortField) => {
    if (sortField !== field) return '';
    return sortDir === 'asc' ? ' ↑' : ' ↓';
  };

  const sortOptions: { field: SortField; label: string }[] = [
    { field: 'lastUsed', label: 'Recent' },
    { field: 'name', label: 'Name' },
    { field: 'weight', label: 'Weight' },
  ];

  // === Step 1: Material selection ===
  if (!selectedMaterial) {
    return (
      <Modal
        isOpen={isOpen}
        onClose={onClose}
        title="Select Material Type"
        size="md"
        closeOnBackdrop
        closeOnEscape
        titleIcon={<SpoolIcon size={22} fillColor="#6366f1" />}
      >
        {materialsError && (
          <div className="text-pf-error text-sm mb-3 bg-red-500/10 border border-red-500/20 rounded-lg px-3 py-2">
            {materialsError}
          </div>
        )}

        {materialsLoading && (
          <div className="flex flex-col items-center justify-center py-12 gap-3">
            <RefreshIcon className="w-6 h-6 text-pf-text-tertiary animate-spin" />
            <span className="text-sm text-pf-text-tertiary">Loading materials...</span>
          </div>
        )}

        {!materialsLoading && materials.length === 0 && !materialsError && (
          <div className="flex flex-col items-center justify-center py-12 gap-3">
            <SpoolIcon size={48} className="opacity-20" />
            <span className="text-sm text-pf-text-tertiary">No materials found in Spoolman</span>
          </div>
        )}

        {!materialsLoading && materials.length > 0 && (
          <div className="grid grid-cols-2 sm:grid-cols-3 gap-2">
            {materials.map(name => (
              <Button
                key={name}
                variant="unstyled"
                onClick={() => setSelectedMaterial(name)}
                className="flex items-center gap-2.5 px-4 py-3 rounded-lg text-left transition-all hover:bg-pf-bg-2/80 ring-1 ring-transparent hover:ring-pf-border group"
                aria-label={`Select material ${name}`}
              >
                <span className="text-sm font-medium text-pf-text-primary group-hover:text-pf-accent transition-colors">
                  {name}
                </span>
              </Button>
            ))}
          </div>
        )}

        {/* Footer with eject option */}
        {activeSpoolId && (
          <div className="mt-4 pt-3 border-t border-pf-border/50 flex justify-end">
            <Button
              variant="subtle"
              size="sm"
              onClick={() => onSelect(0, {} as SpoolmanSpool)}
              className="text-xs"
            >
              Eject current spool
            </Button>
          </div>
        )}
      </Modal>
    );
  }

  // === Step 2: Spool selection ===
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Change Spool"
      size="lg"
      closeOnBackdrop
      closeOnEscape
      titleIcon={<SpoolIcon size={22} fillColor="#6366f1" />}
    >
      {/* Back button + material badge */}
      <div className="flex items-center gap-2 mb-4">
        <Button
          variant="unstyled"
          onClick={() => { setSelectedMaterial(null); setSpools([]); }}
          className="flex items-center gap-1 text-xs text-pf-text-tertiary hover:text-pf-text-primary transition-colors"
          aria-label="Back to material selection"
        >
          <ChevronLeftIcon className="w-4 h-4" />
          Back
        </Button>
        <span className="text-[10px] font-semibold uppercase tracking-wider text-pf-accent bg-pf-accent/10 px-2 py-0.5 rounded">
          {selectedMaterial}
        </span>
        <div className="flex-1" />
        <Button
          variant="subtle"
          size="sm"
          onClick={() => loadSpoolsForMaterial(selectedMaterial)}
          disabled={spoolsLoading}
          aria-label="Refresh spool list"
          className="shrink-0"
        >
          <RefreshIcon className={clsx('w-4 h-4', spoolsLoading && 'animate-spin')} />
        </Button>
      </div>

      {/* Search bar */}
      <div className="flex items-center gap-2 mb-3">
        <div className="relative flex-1">
          <input
            ref={searchInputRef}
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search by name, vendor..."
            className="w-full bg-pf-bg-0 border border-pf-border rounded-lg px-3 py-2 pl-9 text-sm text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:border-pf-accent focus:ring-1 focus:ring-pf-accent/30 transition-all"
            aria-label="Search spools"
          />
          <SearchIcon className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary pointer-events-none" />
        </div>
      </div>

      {/* Sort + filters row */}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 mb-3">
        <div className="flex items-center gap-1.5">
          <span className="text-[10px] uppercase tracking-widest text-pf-text-tertiary mr-1">Sort</span>
          {sortOptions.map(opt => (
            <Button
              key={opt.field}
              variant="unstyled"
              onClick={() => handleSort(opt.field)}
              className={clsx(
                'px-2.5 py-1 rounded-md text-xs font-medium transition-all',
                sortField === opt.field
                  ? 'bg-pf-accent/15 text-pf-accent border border-pf-accent/30'
                  : 'text-pf-text-tertiary hover:text-pf-text-secondary hover:bg-pf-bg-2 border border-transparent'
              )}
            >
              {opt.label}{sortIndicator(opt.field)}
            </Button>
          ))}
        </div>

        {availableFilters.vendors.length > 1 && (
          <FilterDropdown
            label="Vendor"
            options={availableFilters.vendors}
            value={vendorFilter}
            onChange={setVendorFilter}
          />
        )}
        {availableFilters.locations.length > 1 && (
          <FilterDropdown
            label="Location"
            options={availableFilters.locations}
            value={locationFilter}
            onChange={setLocationFilter}
          />
        )}

        {activeFilterCount > 0 && (
          <Button
            variant="unstyled"
            onClick={() => { setVendorFilter(null); setLocationFilter(null); }}
            className="flex items-center gap-1 text-[10px] uppercase tracking-wider text-pf-text-tertiary hover:text-pf-error transition-colors"
          >
            <CloseIcon className="w-3 h-3" />
            Clear filters
          </Button>
        )}
      </div>

      {spoolsError && (
        <div className="text-pf-error text-sm mb-3 bg-red-500/10 border border-red-500/20 rounded-lg px-3 py-2">
          {spoolsError}
        </div>
      )}

      {/* Spool list */}
      <div className="max-h-[55vh] overflow-y-auto -mx-1 px-1 space-y-1.5">
        {spoolsLoading && spools.length === 0 && (
          <div className="flex flex-col items-center justify-center py-12 gap-3">
            <RefreshIcon className="w-6 h-6 text-pf-text-tertiary animate-spin" />
            <span className="text-sm text-pf-text-tertiary">Loading spools...</span>
          </div>
        )}
        {!spoolsLoading && filtered.length === 0 && (
          <div className="flex flex-col items-center justify-center py-12 gap-3">
            <SpoolIcon size={48} className="opacity-20" />
            <span className="text-sm text-pf-text-tertiary">
              {search ? 'No spools match your search' : `No ${selectedMaterial} spools with filament remaining`}
            </span>
          </div>
        )}
        {filtered.map(spool => {
          const isActive = spool.id === activeSpoolId;
          const displayName = spool.filamentName ?? spool.name ?? 'Unknown';
          const spoolNumber = `#${String(spool.id).padStart(3, '0')}`;
          const weight = formatWeight(spool);

          return (
            <Button
              key={spool.id}
              variant="unstyled"
              onClick={() => onSelect(spool.id, spool)}
              className={clsx(
                'w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-left transition-all group',
                isActive
                  ? 'bg-pf-accent/10 ring-1 ring-pf-accent/40'
                  : 'hover:bg-pf-bg-2/80 ring-1 ring-transparent hover:ring-pf-border'
              )}
              aria-label={`Select spool ${spoolNumber} ${displayName}`}
            >
              {/* Spool icon with filament color */}
              <div className="relative shrink-0">
                <SpoolIcon size={40} fillColor={spool.colorHex ?? undefined} />
                {isActive && (
                  <div className="absolute -top-1 -right-1 w-4.5 h-4.5 bg-pf-accent rounded-full flex items-center justify-center ring-2 ring-pf-bg-1">
                    <CheckIcon className="w-3 h-3 text-white" />
                  </div>
                )}
              </div>

              {/* Info block */}
              <div className="flex-1 min-w-0">
                <div className="flex items-baseline gap-2">
                  <span className="text-sm font-medium text-pf-text-primary truncate">
                    {displayName}
                  </span>
                </div>
                <div className="flex items-center gap-2 mt-0.5">
                  <span className="text-[10px] text-pf-text-tertiary">
                    {spoolNumber}
                  </span>
                  {spool.vendor && (
                    <>
                      <span className="text-pf-text-tertiary/30 text-[10px]">·</span>
                      <span className="text-[10px] text-pf-text-tertiary truncate">
                        {spool.vendor}
                      </span>
                    </>
                  )}
                  {spool.lastUsedAt && (
                    <>
                      <span className="text-pf-text-tertiary/30 text-[10px]">·</span>
                      <span className="text-[10px] text-pf-text-tertiary">
                        {formatDate(spool.lastUsedAt)}
                      </span>
                    </>
                  )}
                </div>
              </div>

              {/* Weight indicator */}
              <div className="shrink-0 flex flex-col items-end gap-1 min-w-[60px]">
                <span className={clsx('text-xs font-semibold tabular-nums', getWeightColor(weight.percentage))}>
                  {weight.remaining}
                </span>
                {weight.percentage !== null && (
                  <div className="w-12 h-1 rounded-full bg-pf-bg-2 overflow-hidden">
                    <div
                      className={clsx('h-full rounded-full transition-all', getWeightBarColor(weight.percentage))}
                      style={{ width: `${Math.max(2, weight.percentage)}%` }}
                    />
                  </div>
                )}
              </div>
            </Button>
          );
        })}
      </div>

      {/* Footer summary */}
      {!spoolsLoading && filtered.length > 0 && (
        <div className="mt-3 pt-3 border-t border-pf-border/50 flex items-center justify-between">
          <span className="text-[10px] text-pf-text-tertiary uppercase tracking-wider">
            {filtered.length} spool{filtered.length !== 1 ? 's' : ''}
            {search && ` matching "${search}"`}
          </span>
          {activeSpoolId && (
            <Button
              variant="subtle"
              size="sm"
              onClick={() => onSelect(0, {} as SpoolmanSpool)}
              className="text-xs"
            >
              Eject current spool
            </Button>
          )}
        </div>
      )}
    </Modal>
  );
}
