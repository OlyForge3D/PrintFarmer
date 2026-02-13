import React, { useState, useEffect, useMemo, useRef, useCallback, startTransition } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { RefreshIcon, SearchIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import type { SpoolmanSpool } from '@/types/api';

interface SpoolPickerModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSelect: (spoolId: number, spool: SpoolmanSpool) => void;
  /** Printer ID — spools are fetched through this printer's backend proxy */
  printerId: string;
  /** Currently active spool ID (to highlight it) */
  activeSpoolId?: number;
}

type SortField = 'lastUsed' | 'material' | 'weight' | 'name';
type SortDir = 'asc' | 'desc';

function formatDate(dateStr: string | null | undefined): string {
  if (!dateStr) return '—';
  const d = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - d.getTime();
  const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
  if (diffDays === 0) return 'Today';
  if (diffDays === 1) return 'Yesterday';
  return d.toLocaleDateString(undefined, { month: 'numeric', day: 'numeric', year: 'numeric' });
}

function formatWeight(spool: SpoolmanSpool): string {
  const remaining = spool.remainingWeightG;
  const initial = spool.initialWeightG;
  if (remaining == null) return '—';
  const initStr = initial != null ? ` / ${Math.round(initial / 1000)}kg` : '';
  return `${Math.round(remaining)}g${initStr}`;
}

/**
 * Modal for selecting a spool from the Spoolman inventory.
 * Shows a searchable, sortable list of available spools.
 */
export function SpoolPickerModal({ isOpen, onClose, onSelect, printerId, activeSpoolId }: SpoolPickerModalProps) {
  const [spools, setSpools] = useState<SpoolmanSpool[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [sortField, setSortField] = useState<SortField>('lastUsed');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const requestIdRef = useRef(0);

  const loadSpools = useCallback(async () => {
    const requestId = ++requestIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const data = await apiClient.getPrinterSpools(printerId);

      if (requestId !== requestIdRef.current) {
        return;
      }

      startTransition(() => {
        setSpools(Array.isArray(data) ? data : []);
      });
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setError(err instanceof Error ? err.message : 'Failed to load spools');
      setSpools([]);
    } finally {
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [printerId]);

  useEffect(() => {
    if (isOpen) {
      void loadSpools();
      setSearch('');
    }
  }, [isOpen, loadSpools]);

  const filtered = useMemo(() => {
    const q = search.toLowerCase().trim();
    const list = spools.filter(s => {
      // Exclude archived and empty spools
      if (s.archived) return false;
      if (!q) return true;
      const searchable = [
        `#${String(s.id).padStart(3, '0')}`,
        s.vendor,
        s.filamentName ?? s.name,
        s.material,
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
        case 'material':
          return dir * (a.material ?? '').localeCompare(b.material ?? '');
        case 'weight':
          return dir * ((a.remainingWeightG ?? 0) - (b.remainingWeightG ?? 0));
        case 'name':
          return dir * (a.filamentName ?? a.name ?? '').localeCompare(b.filamentName ?? b.name ?? '');
        default:
          return 0;
      }
    });

    return list;
  }, [spools, search, sortField, sortDir]);

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortDir(field === 'lastUsed' ? 'desc' : 'asc');
    }
  };

  const sortIndicator = (field: SortField) => {
    if (sortField !== field) return null;
    return sortDir === 'asc' ? ' ↑' : ' ↓';
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Change Spool"
      size="lg"
      closeOnBackdrop
      closeOnEscape
      titleIcon={<span className="text-pf-text-secondary text-lg">⊙</span>}
    >
      {/* Search + Refresh row */}
      <div className="flex items-center gap-2 mb-4">
        <div className="relative flex-1 max-w-xs">
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search"
            className="w-full bg-pf-bg-0 border border-pf-border rounded-md px-3 py-1.5 pr-8 text-sm text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:border-pf-accent"
            aria-label="Search spools"
          />
          <SearchIcon className="w-4 h-4 absolute right-2 top-1/2 -translate-y-1/2 text-pf-text-tertiary pointer-events-none" />
        </div>
        <Button
          variant="subtle"
          size="sm"
          onClick={loadSpools}
          disabled={loading}
          aria-label="Refresh spool list"
        >
          <RefreshIcon className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      {error && (
        <div className="text-red-400 text-sm mb-3">{error}</div>
      )}

      {/* Table header */}
      <div className="grid grid-cols-[1fr_auto_auto_auto] gap-2 px-2 pb-2 text-xs text-pf-text-secondary border-b border-pf-border">
        <Button variant="unstyled" onClick={() => handleSort('name')} className="text-left hover:text-pf-text-primary cursor-pointer">
          Filament{sortIndicator('name')}
        </Button>
        <Button variant="unstyled" onClick={() => handleSort('material')} className="text-center hover:text-pf-text-primary cursor-pointer w-16">
          Material{sortIndicator('material')}
        </Button>
        <Button variant="unstyled" onClick={() => handleSort('lastUsed')} className="text-center hover:text-pf-text-primary cursor-pointer w-24">
          Last Used{sortIndicator('lastUsed')}
        </Button>
        <Button variant="unstyled" onClick={() => handleSort('weight')} className="text-right hover:text-pf-text-primary cursor-pointer w-20">
          Weight{sortIndicator('weight')}
        </Button>
      </div>

      {/* Spool list */}
      <div className="max-h-[50vh] overflow-y-auto">
        {loading && spools.length === 0 && (
          <div className="text-center py-8 text-pf-text-tertiary text-sm">Loading spools...</div>
        )}
        {!loading && filtered.length === 0 && (
          <div className="text-center py-8 text-pf-text-tertiary text-sm">
            {search ? 'No spools match your search' : 'No spools available'}
          </div>
        )}
        {filtered.map(spool => {
          const isActive = spool.id === activeSpoolId;
          const displayName = spool.filamentName ?? spool.name ?? 'Unknown';
          const spoolNumber = `#${String(spool.id).padStart(3, '0')}`;

          return (
            <Button
              key={spool.id}
              variant="unstyled"
              onClick={() => onSelect(spool.id, spool)}
              className={`w-full grid grid-cols-[1fr_auto_auto_auto] gap-2 items-center px-2 py-2.5 text-left transition-colors cursor-pointer ${
                isActive
                  ? 'bg-pf-accent/20 border-l-2 border-pf-accent'
                  : 'hover:bg-pf-bg-2 border-l-2 border-transparent'
              }`}
              aria-label={`Select spool ${spoolNumber} ${displayName}`}
            >
              {/* Filament info */}
              <div className="flex items-center gap-2.5 min-w-0">
                {/* Color swatch */}
                <div
                  className="w-8 h-8 rounded-full border border-pf-border shrink-0"
                  style={{ backgroundColor: spool.colorHex ?? '#555' }}
                />
                <div className="min-w-0">
                  <div className="text-[10px] text-pf-text-tertiary">
                    {spoolNumber}{spool.vendor ? ` | ${spool.vendor}` : ''}
                  </div>
                  <div className="text-sm font-medium text-pf-text-primary truncate">
                    {displayName}
                  </div>
                </div>
              </div>

              {/* Material */}
              <div className="text-xs text-pf-text-secondary w-16 text-center">
                {spool.material ?? '—'}
              </div>

              {/* Last used */}
              <div className="text-xs text-pf-text-secondary w-24 text-center">
                {formatDate(spool.lastUsedAt)}
              </div>

              {/* Weight */}
              <div className="text-xs text-pf-text-primary font-medium w-20 text-right">
                {formatWeight(spool)}
              </div>
            </Button>
          );
        })}
      </div>
    </Modal>
  );
}
