/* eslint-disable local/pf-no-raw-html-controls -- Custom spool list items need raw buttons for color swatch styling */
import { useState, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import clsx from 'clsx';
import { Input, Spinner, Badge } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import type { SpoolmanSpool } from '@/types/api';

export interface SpoolPickerProps {
  printerId: string;
  supportedMaterials?: string[];
  currentSpoolId?: number;
  onSelect: (spool: SpoolmanSpool) => void;
  disabled?: boolean;
}

export function SpoolPicker({ printerId, supportedMaterials, currentSpoolId, onSelect, disabled }: SpoolPickerProps) {
  const [search, setSearch] = useState('');

  const { data: spools = [], isLoading, isError } = useQuery({
    queryKey: ['printer-spools', printerId],
    queryFn: () => apiClient.getPrinterSpools(printerId),
    staleTime: 30_000,
  });

  const filtered = useMemo(() => {
    let result = spools.filter((s) => !s.archived);

    if (supportedMaterials && supportedMaterials.length > 0) {
      const lower = supportedMaterials.map((m) => m.toLowerCase());
      result = result.filter((s) => !s.material || lower.includes(s.material.toLowerCase()));
    }

    if (search.trim()) {
      const q = search.toLowerCase();
      result = result.filter(
        (s) =>
          s.name?.toLowerCase().includes(q) ||
          s.material?.toLowerCase().includes(q) ||
          s.vendor?.toLowerCase().includes(q) ||
          s.filamentName?.toLowerCase().includes(q),
      );
    }

    return result;
  }, [spools, supportedMaterials, search]);

  if (isLoading) {
    return (
      <div className="flex justify-center py-4">
        <Spinner size="sm" />
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <Input
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        placeholder="Search spools…"
      />
      <div className="max-h-48 overflow-y-auto -mx-1 px-1 space-y-0.5">
        {isError && (
          <p className="text-xs text-pf-error text-center py-3">Failed to load spools</p>
        )}
        {!isError && filtered.length === 0 && (
          <p className="text-xs text-pf-text-tertiary text-center py-3">No matching spools</p>
        )}
        {filtered.map((spool) => {
          const colorHex = spool.colorHex
            ? `#${spool.colorHex.replace(/^#/, '')}`
            : undefined;
          const isCurrent = spool.id === currentSpoolId;

          return (
            <button
              key={spool.id}
              type="button"
              disabled={disabled}
              onClick={() => onSelect(spool)}
              className={clsx(
                'w-full flex items-center gap-2 px-2 py-1.5 rounded-md text-left text-xs transition',
                isCurrent
                  ? 'bg-pf-accent-bg/15 ring-1 ring-pf-accent'
                  : 'hover:bg-pf-bg-2',
                disabled && 'opacity-50 pointer-events-none',
              )}
              data-pf-button=""
            >
              {colorHex ? (
                <span
                  className="w-4 h-4 rounded-full shrink-0 border border-pf-border"
                  style={{ backgroundColor: colorHex }}
                />
              ) : (
                <span className="w-4 h-4 rounded-full shrink-0 border border-dashed border-pf-border-light" />
              )}
              <div className="min-w-0 flex-1">
                <div className="font-medium text-pf-text-primary truncate">
                  {spool.filamentName || spool.name || `Spool #${spool.id}`}
                </div>
                <div className="text-pf-text-tertiary truncate">
                  {[spool.vendor, spool.material].filter(Boolean).join(' · ')}
                  {spool.remainingWeightG != null && ` · ${Math.round(spool.remainingWeightG)}g`}
                </div>
              </div>
              {isCurrent && <Badge variant="primary" size="sm">Current</Badge>}
            </button>
          );
        })}
      </div>
    </div>
  );
}
