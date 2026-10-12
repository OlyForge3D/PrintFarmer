import { ControlPadButton } from '@/common/components/ui';
import { FilamentChangeIcon } from '@/common/components/icons/MdiIcons';

interface FilamentControlSectionProps {
  filamentActionPending: boolean;
  canFilamentChange: boolean;
  onFilamentAction: (action: 'change') => Promise<void>;
}

/**
 * Printer-level filament change (M600). Load and unload live in the materials
 * slot panel, so there is exactly one place to feed or retract filament.
 */
export function FilamentControlSection({
  filamentActionPending,
  canFilamentChange,
  onFilamentAction,
}: FilamentControlSectionProps) {
  return (
    <div className="flex flex-col gap-1 mt-4">
      <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
        Filament
      </div>
      <div className="grid grid-flow-col gap-1 w-fit">
        <ControlPadButton
          disabled={filamentActionPending || !canFilamentChange}
          onClick={() => onFilamentAction('change')}
          title="Change Filament (M600)"
          padSize="small"
        >
          <FilamentChangeIcon className="w-4 h-4" />
        </ControlPadButton>
      </div>
    </div>
  );
}
