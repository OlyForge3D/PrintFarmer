import { ControlPadButton } from '@/common/components/ui';
import {
  FilamentLoadIcon,
  FilamentUnloadIcon,
  FilamentChangeIcon,
} from '@/common/components/icons/MdiIcons';

interface FilamentControlSectionProps {
  filamentActionPending: boolean;
  canFilamentControl: boolean;
  canFilamentChange: boolean;
  onFilamentAction: (action: 'load' | 'unload' | 'change') => Promise<void>;
}

export function FilamentControlSection({
  filamentActionPending,
  canFilamentControl,
  canFilamentChange,
  onFilamentAction,
}: FilamentControlSectionProps) {
  return (
    <div className="flex flex-col gap-1 mt-2">
      <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
        Filament
      </div>
      <div className="grid grid-cols-3 gap-1 w-fit">
        <ControlPadButton
          disabled={filamentActionPending || !canFilamentControl}
          onClick={() => onFilamentAction('load')}
          title="Load Filament"
          padSize="small"
        >
          <FilamentLoadIcon className="w-4 h-4" />
        </ControlPadButton>
        <ControlPadButton
          disabled={filamentActionPending || !canFilamentControl}
          onClick={() => onFilamentAction('unload')}
          title="Unload Filament"
          padSize="small"
        >
          <FilamentUnloadIcon className="w-4 h-4" />
        </ControlPadButton>
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
