import { ControlPadButton } from '@/common/components/ui';
import {
  PlayIcon,
  PauseIcon,
  XCircleIcon,
  EmergencyStopIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';

interface PrinterActionBarProps {
  layout?: 'card' | 'sidebar';
  isPaused: boolean;
  isShutdown: boolean;
  controlActionPending: boolean;
  canPauseOrResume: boolean;
  canCancel: boolean;
  canEmergencyStop: boolean;
  onControlAction: (action: 'pause' | 'resume' | 'cancel' | 'stop' | 'firmware-restart') => Promise<void>;
}

export function PrinterActionBar({
  layout = 'card',
  isPaused,
  isShutdown,
  controlActionPending,
  canPauseOrResume,
  canCancel,
  canEmergencyStop,
  onControlAction,
}: PrinterActionBarProps) {
  return (
    <div className={layout === 'sidebar' ? 'flex flex-col gap-1' : 'flex flex-col gap-2 items-start'}>
      <div className={layout === 'sidebar' ? 'text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide' : 'text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1'}>
        Control
      </div>
      <div className="grid grid-cols-3 gap-1 w-fit max-sm:[&>button]:min-h-11 max-sm:[&>button]:min-w-11">
        <ControlPadButton
          disabled={controlActionPending || !canPauseOrResume}
          onClick={() => onControlAction(isPaused ? 'resume' : 'pause')}
          title={isPaused ? 'Resume print' : 'Pause print'}
          aria-label={layout === 'sidebar' ? (isPaused ? 'Resume print' : 'Pause print') : (isPaused ? 'Resume' : 'Pause')}
          padSize="small"
        >
          {isPaused ? <PlayIcon className="h-4 w-4" /> : <PauseIcon className="h-4 w-4" />}
        </ControlPadButton>
        <ControlPadButton
          disabled={controlActionPending || !canCancel}
          onClick={() => onControlAction('cancel')}
          title="Cancel print"
          aria-label={layout === 'sidebar' ? 'Cancel print' : 'Cancel'}
          padSize="small"
        >
          <XCircleIcon className="h-4 w-4" ariaLabel="Cancel" />
        </ControlPadButton>
        <ControlPadButton
          variant={isShutdown ? 'secondary' : 'danger'}
          disabled={controlActionPending || !canEmergencyStop}
          onClick={() => onControlAction(isShutdown ? 'firmware-restart' : 'stop')}
          title={isShutdown ? 'Firmware Restart' : 'Emergency Stop'}
          aria-label={isShutdown ? 'Firmware Restart' : 'Emergency Stop'}
          padSize="small"
        >
          {isShutdown ? <RefreshIcon className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
        </ControlPadButton>
      </div>
    </div>
  );
}
