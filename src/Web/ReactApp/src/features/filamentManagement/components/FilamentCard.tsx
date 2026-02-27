import { Button } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { EditIcon, CopyIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { ColorSwatch } from '@/features/filamentManagement/components/ColorSwatch';
import { formatTemp, formatFilamentWeight, formatPrice, formatDiameter } from '@/features/filamentManagement/utils/formatters';
import type { SpoolmanFilament } from '@/types/api';

interface FilamentCardProps {
  filament: SpoolmanFilament;
  isSelected: boolean;
  onToggleSelect: () => void;
  onEdit: () => void;
  onClone: () => void;
  onDelete: () => void;
}

/** Card view for a single Spoolman filament product definition. */
export function FilamentCard({ filament: f, isSelected, onToggleSelect, onEdit, onClone, onDelete }: FilamentCardProps) {
  return (
    <div
      className={`bg-pf-bg-1 border rounded-xl p-4 hover:bg-pf-bg-secondary transition-colors ${isSelected ? 'border-blue-500 ring-1 ring-blue-500/30' : 'border-pf-border'}`}
    >
      <div className="flex items-center gap-2 mb-1">
        <Checkbox
          checked={isSelected}
          onChange={onToggleSelect}
          aria-label={`Select ${f.name || 'filament'}`}
        />
        <div className="text-xs text-pf-text-secondary truncate flex-1">
          {f.vendor || 'Unknown Vendor'}
        </div>
        <Button variant="subtle" size="sm" onClick={onEdit} aria-label={`Edit ${f.name || 'filament'}`} title="Edit filament">
          <EditIcon className="h-3.5 w-3.5" />
        </Button>
        <Button variant="subtle" size="sm" onClick={onClone} aria-label={`Clone ${f.name || 'filament'}`} title="Clone filament">
          <CopyIcon className="h-3.5 w-3.5" />
        </Button>
        <Button variant="subtle" size="sm" onClick={onDelete} aria-label={`Delete ${f.name || 'filament'}`} title="Delete filament">
          <DeleteIcon className="h-3.5 w-3.5" />
        </Button>
      </div>
      <div className="flex items-center gap-2 mb-3">
        <ColorSwatch color={f.colorHex || '#888888'} label={f.name || 'Unknown'} />
        <div className="text-sm font-medium text-pf-text-primary truncate">
          {f.name || 'Unnamed'}
        </div>
      </div>
      <div className="space-y-1.5">
        {f.material && (
          <span className="inline-block px-2 py-0.5 text-[10px] rounded-sm bg-blue-600/20 text-blue-300 border border-blue-600/40 uppercase tracking-wide">
            {f.material}
          </span>
        )}
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-pf-text-secondary mt-2">
          <span>Dia: {formatDiameter(f.diameter)}</span>
          <span>Wt: {formatFilamentWeight(f.weight)}</span>
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
  );
}
