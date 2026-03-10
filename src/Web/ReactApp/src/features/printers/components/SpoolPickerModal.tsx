import { useState, useEffect, useMemo, useRef, useCallback, startTransition } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { RefreshIcon, SearchIcon, CheckIcon, CloseIcon, ChevronLeftIcon, ChevronUpIcon, ChevronDownIcon, EjectIcon } from '@/common/components/icons/MdiIcons';
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

type SortField = 'lastUsed' | 'weight' | 'name' | 'added';
type SortDir = 'asc' | 'desc';

function formatDate(dateStr: string | null | undefined): string {
  if (!dateStr) return '—';
  const d = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - d.getTime();
  const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
  if (diffDays === 0) return 'Today';
  if (diffDays === 1) return 'Yesterday';
  if (diffDays < 7) return `${diffDays}d ago`;
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
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
  const [colorFilter, setColorFilter] = useState<string | null>(null);

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
      setColorFilter(null);
    }
  }, [isOpen, loadMaterials]);

  // Load spools when material is selected
  useEffect(() => {
    if (selectedMaterial) {
      void loadSpoolsForMaterial(selectedMaterial);
      setSearch('');
      setVendorFilter(null);
      setLocationFilter(null);
      setColorFilter(null);
      setTimeout(() => searchInputRef.current?.focus(), 100);
    }
  }, [selectedMaterial, loadSpoolsForMaterial]);

  const availableFilters = useMemo(() => {
    const vendors = [...new Set(spools.map(s => s.vendor).filter((v): v is string => !!v))].sort();
    const locations = [...new Set(spools.map(s => s.location).filter((l): l is string => !!l))].sort();
    const colorMap = new Map<string, string>();
    for (const s of spools) {
      if (s.colorHex && s.filamentName) {
        const key = s.colorHex.toLowerCase();
        if (!colorMap.has(key)) colorMap.set(key, s.filamentName);
      }
    }
    const colors = [...colorMap.entries()].sort((a, b) => a[1].localeCompare(b[1]));
    return { vendors, locations, colors };
  }, [spools]);

  const activeFilterCount = [vendorFilter, locationFilter, colorFilter].filter(Boolean).length;

  const filtered = useMemo(() => {
    const q = search.toLowerCase().trim();
    const list = spools.filter(s => {
      if (vendorFilter && s.vendor !== vendorFilter) return false;
      if (locationFilter && s.location !== locationFilter) return false;
      if (colorFilter && (s.colorHex ?? '').toLowerCase() !== colorFilter.toLowerCase()) return false;
      if (!q) return true;
      const searchable = [
        `#${String(s.id).padStart(3, '0')}`,
        s.vendor,
        s.filamentName ?? s.name,
      ].filter(Boolean).join(' ').toLowerCase();
      const words = q.split(/\s+/).filter(Boolean);
      return words.every(w => searchable.includes(w));
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
        case 'added': {
          const aReg = a.registeredAt ?? '';
          const bReg = b.registeredAt ?? '';
          return dir * aReg.localeCompare(bReg);
        }
        default:
          return 0;
      }
    });

    return list;
  }, [spools, search, sortField, sortDir, vendorFilter, locationFilter, colorFilter]);

  const sortOptions: { field: SortField; label: string }[] = [
    { field: 'lastUsed', label: 'Recent' },
    { field: 'added', label: 'Added' },
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
              title="Eject current spool"
              iconLeft={<EjectIcon className="w-4 h-4" ariaLabel="" />}
            >
              Eject
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
      size="xl"
      closeOnBackdrop
      closeOnEscape
      titleIcon={<SpoolIcon size={22} fillColor="#6366f1" />}
    >
      {/* Back button + material badge */}
      <div className="flex items-center gap-2 mb-4">
        <Button
          variant="unstyled"
          onClick={() => { setSelectedMaterial(null); setSpools([]); }}
          className="text-xs text-pf-text-tertiary hover:text-pf-text-primary transition-colors"
          aria-label="Back to material selection"
          iconLeft={<ChevronLeftIcon className="w-4 h-4" />}
        >
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
          <span className="text-[10px] uppercase tracking-widest text-pf-text-tertiary">Sort</span>
          <Select
            value={sortField}
            onChange={e => {
              const field = e.target.value as SortField;
              setSortField(field);
              setSortDir(field === 'lastUsed' || field === 'added' ? 'desc' : 'asc');
            }}
            containerClassName="w-auto"
            className="bg-pf-bg-0 rounded-md px-2 py-1 text-xs border-pf-border text-pf-text-secondary cursor-pointer"
            aria-label="Sort by"
          >
            {sortOptions.map(opt => (
              <option key={opt.field} value={opt.field}>{opt.label}</option>
            ))}
          </Select>
          <Button
            variant="unstyled"
            onClick={() => setSortDir(d => d === 'asc' ? 'desc' : 'asc')}
            className="p-1 rounded text-pf-text-tertiary hover:text-pf-accent hover:bg-pf-bg-2 transition-colors"
            aria-label={sortDir === 'asc' ? 'Sort descending' : 'Sort ascending'}
            title={sortDir === 'asc' ? 'Sort descending' : 'Sort ascending'}
          >
            {sortDir === 'asc' ? <ChevronUpIcon className="w-4 h-4" /> : <ChevronDownIcon className="w-4 h-4" />}
          </Button>
        </div>

        {availableFilters.colors.length > 1 && (
          <div className="flex items-center gap-1.5">
            <span className="text-[10px] uppercase tracking-widest text-pf-text-tertiary">Color</span>
            <Select
              value={colorFilter ?? ''}
              onChange={e => setColorFilter(e.target.value || null)}
              containerClassName="w-auto"
              className={clsx(
                'bg-pf-bg-0 rounded-md px-2 py-1 text-xs transition-all cursor-pointer',
                colorFilter
                  ? 'border-pf-accent/40 text-pf-accent bg-pf-accent/5'
                  : 'border-pf-border text-pf-text-secondary'
              )}
              aria-label="Filter by Color"
            >
              <option value="">All</option>
              {availableFilters.colors.map(([hex, name]) => (
                <option key={hex} value={hex}>{name}</option>
              ))}
            </Select>
          </div>
        )}
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
            onClick={() => { setVendorFilter(null); setLocationFilter(null); setColorFilter(null); }}
            className="p-1 rounded text-pf-text-tertiary hover:text-pf-error hover:bg-pf-bg-2 transition-colors"
            aria-label="Clear filters"
            title="Clear filters"
          >
            <CloseIcon className="w-3.5 h-3.5" />
          </Button>
        )}
      </div>

      {spoolsError && (
        <div className="text-pf-error text-sm mb-3 bg-red-500/10 border border-red-500/20 rounded-lg px-3 py-2">
          {spoolsError}
        </div>
      )}

      {/* Spool list */}
      <div className="max-h-[45vh] overflow-y-auto -mx-1 px-1 space-y-1.5">
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
                'w-full px-3 py-2.5 rounded-lg text-left transition-all group',
                isActive
                  ? 'bg-pf-accent/10 ring-1 ring-pf-accent/40'
                  : 'hover:bg-pf-bg-2/80 ring-1 ring-transparent hover:ring-pf-border'
              )}
              aria-label={`Select spool ${spoolNumber} ${displayName}`}
            >
              <div className="flex items-center gap-3">
                {/* Spool icon — vertically centered */}
                <div className="relative shrink-0 self-center">
                  <SpoolIcon size={44} fillColor={spool.colorHex ?? undefined} />
                  {isActive && (
                    <div className="absolute -top-1 -right-1 w-4.5 h-4.5 bg-pf-accent rounded-full flex items-center justify-center ring-2 ring-pf-bg-1">
                      <CheckIcon className="w-3 h-3 text-white" />
                    </div>
                  )}
                </div>

                {/* Grid: 3 rows × 3 columns */}
                <div className="flex-1 min-w-0 grid grid-cols-[1fr_auto_auto] gap-x-4 gap-y-0.5 items-baseline">
                  {/* Row 1: Name (ID) spans all columns */}
                  <span className="text-sm font-medium text-pf-text-primary truncate col-span-3">
                    {displayName} <span className="text-[10px] text-pf-text-tertiary font-normal">({spoolNumber})</span>
                  </span>

                  {/* Row 2: Vendor | Added date | Weight remaining */}
                  <span className="text-[10px] text-pf-text-tertiary truncate">
                    {spool.vendor ?? '—'}
                  </span>
                  <span className="text-[10px] text-pf-text-tertiary whitespace-nowrap">
                    Added {spool.registeredAt ? formatDate(spool.registeredAt) : '—'}
                  </span>
                  <span className={clsx('text-xs font-semibold tabular-nums text-right whitespace-nowrap', getWeightColor(weight.percentage))}>
                    {weight.remaining}
                    {weight.percentage !== null && (
                      <span className="text-[10px] font-normal text-pf-text-tertiary ml-1">
                        ({weight.percentage}%)
                      </span>
                    )}
                  </span>

                  {/* Row 3: — | Used date | Weight bar */}
                  <span />
                  <span className="text-[10px] text-pf-text-tertiary whitespace-nowrap">
                    Used {spool.lastUsedAt ? formatDate(spool.lastUsedAt) : 'Never'}
                  </span>
                  <div className="flex justify-end">
                    {weight.percentage !== null && (
                      <div className="w-14 h-1 rounded-full bg-pf-bg-2 overflow-hidden self-center">
                        <div
                          className={clsx('h-full rounded-full transition-all', getWeightBarColor(weight.percentage))}
                          style={{ width: `${Math.max(2, weight.percentage)}%` }}
                        />
                      </div>
                  )}
                </div>
              </div>
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
              title="Eject current spool"
              iconLeft={<EjectIcon className="w-4 h-4" ariaLabel="" />}
            >
              Eject
            </Button>
          )}
        </div>
      )}
    </Modal>
  );
}
