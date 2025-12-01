import { 
  mdiPrinter3dNozzleAlert, 
  mdiPrinter3dNozzle, 
  mdiRadiator, 
  mdiRadiatorDisabled, 
  mdiEngineOff,
  mdiHome,
  mdiPlay,
  mdiPause,
  mdiAlertOctagonOutline,
  mdiStop,
  mdiChevronUp,
  mdiChevronDown,
  mdiChevronLeft,
  mdiChevronRight
} from '@mdi/js';

interface IconProps {
  className?: string;
  ariaLabel?: string;
  isOn?: boolean;
}

export function NozzleIcon({ className = 'w-4 h-4', ariaLabel = 'Hotend temperature', isOn = true }: IconProps) {
  const iconPath = isOn ? mdiPrinter3dNozzleAlert : mdiPrinter3dNozzle;
  return (
    <svg
      className={`${className} transition-opacity ${isOn ? 'opacity-100' : 'opacity-40'}`}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={iconPath} />
    </svg>
  );
}

export function BedIcon({ className = 'w-4 h-4', ariaLabel = 'Bed temperature', isOn = true }: IconProps) {
  const iconPath = isOn ? mdiRadiator : mdiRadiatorDisabled;
  return (
    <svg
      className={`${className} transition-opacity ${isOn ? 'opacity-100' : 'opacity-40'}`}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={iconPath} />
    </svg>
  );
}

export function DisableMotorsIcon({ className = 'w-4 h-4', ariaLabel = 'Disable motors' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiEngineOff} />
    </svg>
  );
}

export function HomeIcon({ className = 'w-4 h-4', ariaLabel = 'Home' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiHome} />
    </svg>
  );
}

export function PlayIcon({ className = 'w-4 h-4', ariaLabel = 'Play' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPlay} />
    </svg>
  );
}

export function PauseIcon({ className = 'w-4 h-4', ariaLabel = 'Pause' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPause} />
    </svg>
  );
}

export function EmergencyStopIcon({ className = 'w-4 h-4', ariaLabel = 'Emergency stop' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAlertOctagonOutline} />
    </svg>
  );
}

export function StopIcon({ className = 'w-4 h-4', ariaLabel = 'Stop' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiStop} />
    </svg>
  );
}

export function ArrowUpIcon({ className = 'w-4 h-4', ariaLabel = 'Move up' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronUp} />
    </svg>
  );
}

export function ArrowDownIcon({ className = 'w-4 h-4', ariaLabel = 'Move down' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronDown} />
    </svg>
  );
}

export function ArrowLeftIcon({ className = 'w-4 h-4', ariaLabel = 'Move left' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronLeft} />
    </svg>
  );
}

export function ArrowRightIcon({ className = 'w-4 h-4', ariaLabel = 'Move right' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronRight} />
    </svg>
  );
}
