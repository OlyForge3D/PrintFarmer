import { useState } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { EditIcon, CopyIcon, DeleteIcon, TagIcon } from '@/common/components/icons/MdiIcons';
import { classifyColor, getRepresentativeHex } from '@/common/utils/colorFamilies';
import { ColorSwatch } from '@/features/filamentManagement/components/ColorSwatch';
import { formatSpoolWeight } from '@/features/filamentManagement/utils/formatters';
import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';

interface SpoolCompactViewProps {
  spools: SpoolmanSpoolDto[];
  selectedIds: Set<number>;
  allSelected: boolean;
  onToggleSelect: (id: number) => void;
  onToggleSelectAll: () => void;
  onEdit: (s: SpoolmanSpoolDto) => void;
  onClone: (s: SpoolmanSpoolDto) => void;
  onDelete: (s: SpoolmanSpoolDto) => void;
  onPrintLabel: (s: SpoolmanSpoolDto) => void;
}

/** Dense single-line list view for Spoolman spools. */
export function SpoolCompactView({
  spools,
  selectedIds,
  allSelected,
  onToggleSelect,
  onToggleSelectAll,
  onEdit,
  onClone,
  onDelete,
  onPrintLabel,
}: SpoolCompactViewProps) {
  const [hoveredId, setHoveredId] = useState<number | null>(null);

  return (
    <div className="border border-pf-border rounded-lg overflow-hidden" role="list" aria-label="Spool inventory list">
      {/* Header row */}
      <div className="flex items-center h-8 px-3 bg-pf-bg-2 text-xs font-medium text-pf-text-secondary border-b border-pf-border select-none">
        <div className="w-7 shrink-0">
          <Checkbox
            checked={allSelected}
            onChange={onToggleSelectAll}
            aria-label={allSelected ? 'Deselect all spools' : 'Select all spools'}
          />
        </div>
        <div className="w-6 shrink-0" />
        <div className="w-16 shrink-0">Material</div>
        <div className="flex-1 min-w-0">Filament</div>
        <div className="w-16 shrink-0 text-right">Weight</div>
        <div className="w-28 shrink-0 text-right hidden sm:block">Location</div>
        <div className="w-28 shrink-0" />
      </div>

      {/* Spool rows */}
      {spools.map(spool => {
        const spoolLabel = spool.filamentName || spool.name || 'spool';
        const isSelected = selectedIds.has(spool.id);
        const isHovered = hoveredId === spool.id;
        const lowWeight = (spool.remainingWeightG ?? Infinity) <= 50;
        const emptyWeight = (spool.remainingWeightG ?? Infinity) <= 10;

        return (
          <div
            key={spool.id}
            role="listitem"
            className={clsx(
              'flex items-center h-9 px-3 border-b border-pf-border last:border-b-0 transition-colors',
              isSelected ? 'bg-pf-accent-bg/10' : 'hover:bg-pf-bg-2',
            )}
            onMouseEnter={() => setHoveredId(spool.id)}
            onMouseLeave={() => setHoveredId(null)}
          >
            {/* Checkbox */}
            <div className="w-7 shrink-0">
              <Checkbox
                checked={isSelected}
                onChange={() => onToggleSelect(spool.id)}
                aria-label={`Select ${spoolLabel}`}
              />
            </div>

            {/* Color swatch */}
            <div className="w-6 shrink-0 flex items-center">
              <ColorSwatch
                color={getRepresentativeHex(classifyColor(spool.colorHex))}
                label={classifyColor(spool.colorHex)}
                className="w-4 h-4"
              />
            </div>

            {/* Material */}
            <div className="w-16 shrink-0">
              {spool.material ? (
                <span className="text-xs font-bold text-pf-text-primary uppercase">{spool.material}</span>
              ) : (
                <span className="text-xs text-pf-text-secondary">—</span>
              )}
            </div>

            {/* Filament name / vendor */}
            <div className="flex-1 min-w-0 flex items-center gap-1.5">
              <span className="text-sm text-pf-text-primary truncate">
                {spool.filamentName || spool.name || 'Unnamed'}
              </span>
              {spool.vendor && (
                <span className="text-xs text-pf-text-secondary truncate hidden md:inline">
                  · {spool.vendor}
                </span>
              )}
              {spool.archived && (
                <span className="inline-block px-1.5 py-0 text-[9px] rounded-sm bg-pf-error/20 text-pf-error border border-pf-error/30 uppercase leading-4">
                  Archived
                </span>
              )}
            </div>

            {/* Remaining weight */}
            <div className="w-16 shrink-0 text-right">
              <span className={clsx(
                'text-xs font-medium',
                emptyWeight ? 'text-pf-error' : lowWeight ? 'text-pf-warning' : 'text-pf-text-primary',
              )}>
                {formatSpoolWeight(spool.remainingWeightG)}
              </span>
            </div>

            {/* Location */}
            <div className="w-28 shrink-0 text-right hidden sm:block">
              {spool.location ? (
                <span className="text-xs text-pf-text-secondary truncate inline-block max-w-full">{spool.location}</span>
              ) : (
                <span className="text-xs text-pf-text-secondary/50">—</span>
              )}
            </div>

            {/* Actions — visible on hover */}
            <div className="w-28 shrink-0 flex justify-end gap-0.5">
              <div className={clsx('flex gap-0.5 transition-opacity', isHovered || isSelected ? 'opacity-100' : 'opacity-0')}>
                <Button variant="subtle" size="sm" onClick={() => onEdit(spool)} aria-label={`Edit ${spoolLabel}`} title="Edit">
                  <EditIcon className="h-3.5 w-3.5" />
                </Button>
                <Button variant="subtle" size="sm" onClick={() => onPrintLabel(spool)} aria-label={`Print label for ${spoolLabel}`} title="Print label">
                  <TagIcon className="h-3.5 w-3.5" />
                </Button>
                <Button variant="subtle" size="sm" onClick={() => onClone(spool)} aria-label={`Clone ${spoolLabel}`} title="Clone">
                  <CopyIcon className="h-3.5 w-3.5" />
                </Button>
                <Button variant="subtle" size="sm" onClick={() => onDelete(spool)} aria-label={`Delete ${spoolLabel}`} title="Delete">
                  <DeleteIcon className="h-3.5 w-3.5" />
                </Button>
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}
