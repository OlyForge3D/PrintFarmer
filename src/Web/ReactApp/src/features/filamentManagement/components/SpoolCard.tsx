import { Button } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { EditIcon, CopyIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { classifyColor, getRepresentativeHex } from '@/common/utils/colorFamilies';
import { ColorSwatch } from '@/features/filamentManagement/components/ColorSwatch';
import { SpoolUsageBar } from '@/features/filamentManagement/components/SpoolUsageBar';
import { formatSpoolWeight, getUsagePercentage, getRemainingPercentage, weightTooltip } from '@/features/filamentManagement/utils/formatters';
import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';

interface SpoolCardProps {
  spool: SpoolmanSpoolDto;
  isSelected: boolean;
  onToggleSelect: () => void;
  onEdit: () => void;
  onClone: () => void;
  onDelete: () => void;
}

/** Card view for a single physical Spoolman spool. */
export function SpoolCard({ spool, isSelected, onToggleSelect, onEdit, onClone, onDelete }: SpoolCardProps) {
  const spoolLabel = spool.filamentName || spool.name || 'spool';

  return (
    <div
      className={`bg-pf-bg-1 border rounded-xl p-4 hover:bg-pf-bg-secondary transition-colors ${
        isSelected ? 'border-pf-accent ring-1 ring-pf-accent/30' : (spool.remainingWeightG ?? Infinity) <= 10 ? 'border-pf-error' : (spool.remainingWeightG ?? Infinity) <= 50 ? 'border-pf-warning' : 'border-pf-border'
      }`}
    >
      <div className="flex items-center gap-2 mb-1">
        <Checkbox
          checked={isSelected}
          onChange={onToggleSelect}
          aria-label={`Select ${spoolLabel}`}
        />
        <div className="text-xs text-pf-text-secondary truncate flex-1">
          {spool.vendor || 'Unknown Vendor'}
        </div>
        <Button variant="subtle" size="sm" onClick={onEdit} aria-label={`Edit ${spoolLabel}`} title="Edit spool">
          <EditIcon className="h-3.5 w-3.5" />
        </Button>
        <Button variant="subtle" size="sm" onClick={onClone} aria-label={`Clone ${spoolLabel}`} title="Clone spool">
          <CopyIcon className="h-3.5 w-3.5" />
        </Button>
        <Button variant="subtle" size="sm" onClick={onDelete} aria-label={`Delete ${spoolLabel}`} title="Delete spool">
          <DeleteIcon className="h-3.5 w-3.5" />
        </Button>
        {spool.archived && (
          <span className="inline-block px-2 py-0.5 text-[10px] rounded-sm bg-pf-error/20 text-pf-error border border-pf-error/30 uppercase tracking-wide">Archived</span>
        )}
      </div>
      <div className="flex items-center gap-2 mb-3">
        <ColorSwatch color={getRepresentativeHex(classifyColor(spool.colorHex))} label={classifyColor(spool.colorHex)} />
        <div className="text-sm font-medium text-pf-text-primary truncate flex-1">
          {spool.filamentName || spool.name || 'Unnamed'}
        </div>
        <div className="text-xs text-pf-text-secondary whitespace-nowrap">#{spool.id}</div>
      </div>

      <div className="space-y-2">
        <div className="flex flex-wrap gap-x-3 gap-y-1">
          {spool.material && (
            <span className="inline-block px-2 py-0.5 text-[10px] rounded-sm bg-pf-accent-bg/20 text-pf-accent border border-pf-accent/30 uppercase tracking-wide">
              {spool.material}
            </span>
          )}
        </div>

        <div className="space-y-1">
          <div className="flex justify-between text-xs">
            <span className="text-pf-text-secondary">Weight</span>
            <span className={`font-medium ${
              (spool.remainingWeightG ?? Infinity) <= 50 ? 'text-pf-warning' : ''
            } ${
              (spool.remainingWeightG ?? Infinity) <= 10 ? 'text-pf-error' : ''
            }`}>
              {formatSpoolWeight(spool.remainingWeightG)}
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

        {spool.location && (
          <div className="text-xs text-pf-text-secondary">Location: {spool.location}</div>
        )}

        {spool.lotNumber && (
          <div className="text-xs text-pf-text-secondary">Lot: {spool.lotNumber}</div>
        )}
      </div>
    </div>
  );
}
