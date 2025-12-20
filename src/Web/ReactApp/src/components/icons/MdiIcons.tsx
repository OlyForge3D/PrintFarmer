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
  mdiChevronRight,
  mdiDelete,
  mdiPencil,
  mdiCheck,
  mdiClose,
  mdiDownload,
  mdiUpload,
  mdiPlus,
  mdiCog,
  mdiAlert,
  mdiInformation,
  mdiMagnify,
  mdiRefresh,
  mdiContentSave,
  mdiEmail,
  mdiEye,
  mdiEyeOff,
  mdiLock,
  mdiServer,
  mdiViewGrid,
  mdiViewList,
  mdiViewComfy,
  mdiViewQuilt,
  mdiTable,
  mdiHistory
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

export function DeleteIcon({ className = 'w-4 h-4', ariaLabel = 'Delete' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiDelete} />
    </svg>
  );
}

export function EditIcon({ className = 'w-4 h-4', ariaLabel = 'Edit' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPencil} />
    </svg>
  );
}

export function CheckIcon({ className = 'w-4 h-4', ariaLabel = 'Check' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCheck} />
    </svg>
  );
}

export function CloseIcon({ className = 'w-4 h-4', ariaLabel = 'Close' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiClose} />
    </svg>
  );
}

export function DownloadIcon({ className = 'w-4 h-4', ariaLabel = 'Download' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiDownload} />
    </svg>
  );
}

export function UploadIcon({ className = 'w-4 h-4', ariaLabel = 'Upload' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiUpload} />
    </svg>
  );
}

export function PlusIcon({ className = 'w-4 h-4', ariaLabel = 'Add' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPlus} />
    </svg>
  );
}

export function SettingsIcon({ className = 'w-4 h-4', ariaLabel = 'Settings' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCog} />
    </svg>
  );
}

export function AlertIcon({ className = 'w-4 h-4', ariaLabel = 'Alert' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAlert} />
    </svg>
  );
}

export function InfoIcon({ className = 'w-4 h-4', ariaLabel = 'Information' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiInformation} />
    </svg>
  );
}

export function SearchIcon({ className = 'w-4 h-4', ariaLabel = 'Search' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiMagnify} />
    </svg>
  );
}

export function RefreshIcon({ className = 'w-4 h-4', ariaLabel = 'Refresh' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiRefresh} />
    </svg>
  );
}

export function SaveIcon({ className = 'w-4 h-4', ariaLabel = 'Save' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiContentSave} />
    </svg>
  );
}

export function EmailIcon({ className = 'w-4 h-4', ariaLabel = 'Email' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiEmail} />
    </svg>
  );
}

export function EyeIcon({ className = 'w-4 h-4', ariaLabel = 'Show' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiEye} />
    </svg>
  );
}

export function EyeOffIcon({ className = 'w-4 h-4', ariaLabel = 'Hide' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiEyeOff} />
    </svg>
  );
}

export function LockIcon({ className = 'w-4 h-4', ariaLabel = 'Lock' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLock} />
    </svg>
  );
}

export function ServerIcon({ className = 'w-4 h-4', ariaLabel = 'Server' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiServer} />
    </svg>
  );
}

export function HistoryIcon({ className = 'w-4 h-4', ariaLabel = 'History' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiHistory} />
    </svg>
  );
}
