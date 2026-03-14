import { 
  mdiPrinter3dNozzleAlert, 
  mdiPrinter3dNozzle, 
  mdiPrinter3d,
  mdiRadiator, 
  mdiRadiatorDisabled, 
  mdiEngineOff,
  mdiFolderOpen,
  mdiTools,
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
  mdiKey,
  mdiServer,
  mdiHistory,
  mdiTag,
  mdiCheckCircle,
  mdiAlertCircle,
  mdiLoading,
  mdiContentCopy,
  mdiViewGrid,
  mdiViewList,
  mdiFile,
  mdiFolder,
  mdiCube,
  mdiOpenInNew,
  mdiCamera,
  mdiNfc,
  mdiMinus,
  mdiWrench,
  mdiViewDashboard,
  mdiTrendingUp,
  mdiPageLayoutSidebarRight,
  mdiImage,
  mdiVideo,
  mdiMenu,
  mdiAccountCheck,
  mdiAccount,
  mdiLogout,
  mdiLogin,
  mdiAccountMultiple,
  mdiLayers,
  mdiLayersTripleOutline,
  mdiPulse,
  mdiBattery,
  mdiAccountPlus,
  mdiWeatherSunny,
  mdiMoonWaningCrescent,
  mdiDesktopTower,
  mdiClockOutline,
  mdiCloseCircle,
  mdiChevronDoubleRight,
  mdiChevronDoubleLeft,
  mdiDotsVertical,
  mdiCircleOutline,
  mdiDatabase,
  mdiFlask,
  mdiPackageVariant,
  mdiFilterOutline,
  mdiTableLargeRemove,
  mdiFileDocument,
  mdiArrowUpDown,
  mdiWifi,
  mdiThermometer,
  mdiListBoxOutline,
  mdiNetwork,
  mdiCalendar,
  mdiChartBox,
  mdiTimerOutline,
  mdiFileImportOutline,
  mdiFileExportOutline,
  mdiPrinterSearch,
  mdiSkipForward,
  mdiSnowflake,
  mdiArrowAll,
  mdiTimerSand,
  mdiMapMarker,
  mdiTrayArrowDown,
  mdiTrayArrowUp,
  mdiSwapVertical,
  mdiEject,
  mdiClipboardListOutline,
} from '@mdi/js';

interface IconProps {
  className?: string;
  ariaLabel?: string;
  isOn?: boolean;
}

/**
 * Nozzle temperature icon
 * 
 * Material Design icon component that renders an SVG element.
 * Shows filled/unfilled state based on nozzle temperature status.
 * 
 * @component
 * @preview ![printer-3d-nozzle-alert](https://unpkg.com/@mdi/svg@7.4.47/svg/printer-3d-nozzle-alert.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/printer-3d-nozzle-alert.svg
 * @param {IconProps} props - Icon properties including className, ariaLabel, and isOn status
 * @returns {JSX.Element} SVG element with appropriate styling
 * @example
 * <NozzleIcon className="w-5 h-5" ariaLabel="Hotend" isOn={true} />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Bed temperature icon
 * 
 * Material Design icon component that renders an SVG element.
 * Shows filled/unfilled state based on bed temperature status.
 * 
 * @component
 * @preview ![radiator](https://unpkg.com/@mdi/svg@7.4.47/svg/radiator.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/radiator.svg
 * @param {IconProps} props - Icon properties including className, ariaLabel, and isOn status
 * @returns {JSX.Element} SVG element with appropriate styling
 * @example
 * <BedIcon className="w-5 h-5" ariaLabel="Bed" isOn={true} />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Disable motors icon
 * 
 * Material Design icon component for motor control actions.
 * 
 * @component
 * @preview ![engine-off](https://unpkg.com/@mdi/svg@7.4.47/svg/engine-off.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/engine-off.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <DisableMotorsIcon className="w-5 h-5" ariaLabel="Disable motors" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Home icon
 * 
 * Material Design icon component for home/homing actions.
 * 
 * @component
 * @preview ![home](https://unpkg.com/@mdi/svg@7.4.47/svg/home.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/home.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <HomeIcon className="w-5 h-5" ariaLabel="Home" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Play icon
 * 
 * Material Design icon component for play/resume actions.
 * 
 * @component
 * @preview ![play](https://unpkg.com/@mdi/svg@7.4.47/svg/play.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/play.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PlayIcon className="w-5 h-5" ariaLabel="Play" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Pause icon
 * 
 * Material Design icon component for pause actions.
 * 
 * @component
 * @preview ![pause](https://unpkg.com/@mdi/svg@7.4.47/svg/pause.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/pause.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PauseIcon className="w-5 h-5" ariaLabel="Pause" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Skip forward icon
 * 
 * Material Design icon component for skipping forward or jumping to the end.
 * 
 * @component
 * @preview ![skip-forward](https://unpkg.com/@mdi/svg@7.4.47/svg/skip-forward.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/skip-forward.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SkipForwardIcon className="w-5 h-5" ariaLabel="Skip forward" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function SkipForwardIcon({ className = 'w-4 h-4', ariaLabel = 'Skip forward' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiSkipForward} />
    </svg>
  );
}

/**
 * Snowflake icon
 * 
 * Material Design icon component for temperature cooling, cooldown operations, or ice-related actions.
 * 
 * @component
 * @preview ![snowflake](https://unpkg.com/@mdi/svg@7.4.47/svg/snowflake.svg)
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SnowflakeIcon className="w-5 h-5" ariaLabel="Cooldown" />
 * @see — https://materialdesignicons.com
 */
export function SnowflakeIcon({ className = 'w-4 h-4', ariaLabel = 'Snowflake' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiSnowflake} />
    </svg>
  );
}

/**
 * Location / Map marker icon
 *
 * @component
 */
export function LocationIcon({ className = 'w-4 h-4', ariaLabel = 'Location' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiMapMarker} />
    </svg>
  );
}

/**
 * Arrows all directions icon
 * 
 * Material Design icon component for multi-directional movement/control.
 * 
 * @component
 * @preview ![arrows-all-directions](https://unpkg.com/@mdi/svg@7.4.47/svg/arrows-all.svg)
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ArrowsAllDirectionsIcon className="w-5 h-5" ariaLabel="Move" />
 * @see — https://materialdesignicons.com
 */
export function ArrowsAllDirectionsIcon({ className = 'w-4 h-4', ariaLabel = 'Arrows All Directions' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiArrowAll} />
    </svg>
  );
}

/**
 * Emergency stop icon
 * 
 * Material Design icon component for emergency stop/cancel actions.
 * 
 * @component
 * @preview ![alert-octagon-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/alert-octagon-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/alert-octagon-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <EmergencyStopIcon className="w-5 h-5" ariaLabel="Emergency stop" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Stop icon
 * 
 * Material Design icon component for stop actions.
 * 
 * @component
 * @preview ![stop](https://unpkg.com/@mdi/svg@7.4.47/svg/stop.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/stop.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <StopIcon className="w-5 h-5" ariaLabel="Stop" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Arrow up icon
 * 
 * Material Design icon component for upward movement/increase actions.
 * 
 * @component
 * @preview ![chevron-up](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-up.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-up.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ArrowUpIcon className="w-5 h-5" ariaLabel="Move up" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Chevron up icon
 * 
 * Material Design icon component for upward movement/collapse actions.
 * 
 * @component
 * @preview ![chevron-up](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-up.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-up.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ChevronUpIcon className="w-5 h-5" ariaLabel="Collapse" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ChevronUpIcon({ className = 'w-4 h-4', ariaLabel = 'Chevron up' }: Omit<IconProps, 'isOn'>) {
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

/**
 * Arrow down icon
 * 
 * Material Design icon component for downward movement/decrease actions.
 * 
 * @component
 * @preview ![chevron-down](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-down.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-down.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ArrowDownIcon className="w-5 h-5" ariaLabel="Move down" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Arrow left icon
 * 
 * Material Design icon component for leftward movement actions.
 * 
 * @component
 * @preview ![chevron-left](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-left.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-left.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ArrowLeftIcon className="w-5 h-5" ariaLabel="Move left" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Arrow right icon
 * 
 * Material Design icon component for rightward movement actions.
 * 
 * @component
 * @preview ![chevron-right](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-right.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-right.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ArrowRightIcon className="w-5 h-5" ariaLabel="Move right" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Delete icon
 * 
 * Material Design icon component for delete/remove actions.
 * 
 * @component
 * @preview ![delete](https://unpkg.com/@mdi/svg@7.4.47/svg/delete.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/delete.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <DeleteIcon className="w-5 h-5" ariaLabel="Delete" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Edit icon
 * 
 * Material Design icon component for edit/modify actions.
 * 
 * @component
 * @preview ![pencil](https://unpkg.com/@mdi/svg@7.4.47/svg/pencil.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/pencil.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <EditIcon className="w-5 h-5" ariaLabel="Edit" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Check icon
 * 
 * Material Design icon component for confirmation/success states.
 * 
 * @component
 * @preview ![check](https://unpkg.com/@mdi/svg@7.4.47/svg/check.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/check.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CheckIcon className="w-5 h-5" ariaLabel="Check" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Close icon
 * 
 * Material Design icon component for close/dismiss actions.
 * 
 * @component
 * @preview ![close](https://unpkg.com/@mdi/svg@7.4.47/svg/close.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/close.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CloseIcon className="w-5 h-5" ariaLabel="Close" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Download icon
 * 
 * Material Design icon component for download actions.
 * 
 * @component
 * @preview ![download](https://unpkg.com/@mdi/svg@7.4.47/svg/download.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/download.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <DownloadIcon className="w-5 h-5" ariaLabel="Download" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Upload icon
 * 
 * Material Design icon component for upload actions.
 * 
 * @component
 * @preview ![upload](https://unpkg.com/@mdi/svg@7.4.47/svg/upload.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/upload.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <UploadIcon className="w-5 h-5" ariaLabel="Upload" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Plus icon
 * 
 * Material Design icon component for add/create actions.
 * 
 * @component
 * @preview ![plus](https://unpkg.com/@mdi/svg@7.4.47/svg/plus.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/plus.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PlusIcon className="w-5 h-5" ariaLabel="Add" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Settings icon
 * 
 * Material Design icon component for settings/configuration actions.
 * 
 * @component
 * @preview ![cog](https://unpkg.com/@mdi/svg@7.4.47/svg/cog.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/cog.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SettingsIcon className="w-5 h-5" ariaLabel="Settings" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Layers triple outline icon
 * 
 * Material Design icon component for slicing/layering operations.
 * 
 * @component
 * @preview ![layers-triple-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/layers-triple-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/layers-triple-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <LayersTripleOutlineIcon className="w-5 h-5" ariaLabel="Slice Model" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function LayersTripleOutlineIcon({ className = 'w-4 h-4', ariaLabel = 'Layers' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLayersTripleOutline} />
    </svg>
  );
}

/**
 * Alert icon
 * 
 * Material Design icon component for warning/alert states.
 * 
 * @component
 * @preview ![alert](https://unpkg.com/@mdi/svg@7.4.47/svg/alert.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/alert.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <AlertIcon className="w-5 h-5" ariaLabel="Alert" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Info icon
 * 
 * Material Design icon component for information/help states.
 * 
 * @component
 * @preview ![information](https://unpkg.com/@mdi/svg@7.4.47/svg/information.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/information.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <InfoIcon className="w-5 h-5" ariaLabel="Information" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Search icon
 * 
 * Material Design icon component for search actions.
 * 
 * @component
 * @preview ![magnify](https://unpkg.com/@mdi/svg@7.4.47/svg/magnify.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/magnify.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SearchIcon className="w-5 h-5" ariaLabel="Search" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Refresh icon
 * 
 * Material Design icon component for refresh/reload actions.
 * 
 * @component
 * @preview ![refresh](https://unpkg.com/@mdi/svg@7.4.47/svg/refresh.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/refresh.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <RefreshIcon className="w-5 h-5" ariaLabel="Refresh" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Timer sand icon (hourglass)
 * 
 * Material Design icon component that displays a sand timer/hourglass icon.
 * Used to indicate loading or waiting states.
 * 
 * @component
 * @preview ![timer-sand](https://unpkg.com/@mdi/svg@7.4.47/svg/timer-sand.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/timer-sand.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TimerSandIcon className="w-5 h-5" ariaLabel="Loading" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TimerSandIcon({ className = 'w-4 h-4', ariaLabel = 'Loading' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTimerSand} />
    </svg>
  );
}

/**
 * Save icon
 * 
 * Material Design icon component for save/persist actions.
 * 
 * @component
 * @preview ![content-save](https://unpkg.com/@mdi/svg@7.4.47/svg/content-save.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/content-save.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SaveIcon className="w-5 h-5" ariaLabel="Save" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Email icon
 * 
 * Material Design icon component for email/messaging actions.
 * 
 * @component
 * @preview ![email](https://unpkg.com/@mdi/svg@7.4.47/svg/email.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/email.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <EmailIcon className="w-5 h-5" ariaLabel="Email" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Eye icon
 * 
 * Material Design icon component for show/visibility actions.
 * 
 * @component
 * @preview ![eye](https://unpkg.com/@mdi/svg@7.4.47/svg/eye.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/eye.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <EyeIcon className="w-5 h-5" ariaLabel="Show" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Eye off icon
 * 
 * Material Design icon component for hide/visibility toggle actions.
 * 
 * @component
 * @preview ![eye-off](https://unpkg.com/@mdi/svg@7.4.47/svg/eye-off.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/eye-off.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <EyeOffIcon className="w-5 h-5" ariaLabel="Hide" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Lock icon
 * 
 * Material Design icon component for lock/security states.
 * 
 * @component
 * @preview ![lock](https://unpkg.com/@mdi/svg@7.4.47/svg/lock.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/lock.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <LockIcon className="w-5 h-5" ariaLabel="Lock" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Key icon
 * 
 * Material Design icon component for API keys or security keys.
 * 
 * @component
 * @preview ![key](https://unpkg.com/@mdi/svg@7.4.47/svg/key.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/key.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <KeyIcon className="w-5 h-5" ariaLabel="API Key" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function KeyIcon({ className = 'w-4 h-4', ariaLabel = 'Key' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiKey} />
    </svg>
  );
}

/**
 * Server icon
 * 
 * Material Design icon component for server/network resources.
 * 
 * @component
 * @preview ![server](https://unpkg.com/@mdi/svg@7.4.47/svg/server.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/server.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ServerIcon className="w-5 h-5" ariaLabel="Server" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * History icon
 * 
 * Material Design icon component for history/timeline views.
 * 
 * @component
 * @preview ![history](https://unpkg.com/@mdi/svg@7.4.47/svg/history.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/history.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <HistoryIcon className="w-5 h-5" ariaLabel="History" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
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

/**
 * Tag icon
 * 
 * Material Design icon component for tag/label management.
 * 
 * @component
 * @preview ![tag](https://unpkg.com/@mdi/svg@7.4.47/svg/tag.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/tag.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TagIcon className="w-5 h-5" ariaLabel="Tag" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TagIcon({ className = 'w-4 h-4', ariaLabel = 'Tag' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTag} />
    </svg>
  );
}

/**
 * Check circle icon
 * 
 * Material Design icon component for completion/success states.
 * 
 * @component
 * @preview ![check-circle](https://unpkg.com/@mdi/svg@7.4.47/svg/check-circle.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/check-circle.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CheckCircleIcon className="w-5 h-5" ariaLabel="Complete" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function CheckCircleIcon({ className = 'w-4 h-4', ariaLabel = 'Complete' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCheckCircle} />
    </svg>
  );
}

/**
 * Alert circle icon
 * 
 * Material Design icon component for alert/error states.
 * 
 * @component
 * @preview ![alert-circle](https://unpkg.com/@mdi/svg@7.4.47/svg/alert-circle.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/alert-circle.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <AlertCircleIcon className="w-5 h-5" ariaLabel="Alert" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function AlertCircleIcon({ className = 'w-4 h-4', ariaLabel = 'Alert' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAlertCircle} />
    </svg>
  );
}

/**
 * Loading icon
 * 
 * Material Design icon component for loading/busy states.
 * Automatically includes spin animation via animate-spin class.
 * 
 * @component
 * @preview ![loading](https://unpkg.com/@mdi/svg@7.4.47/svg/loading.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/loading.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element with spin animation
 * @example
 * <LoadingIcon className="w-5 h-5" ariaLabel="Loading" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function LoadingIcon({ className = 'w-4 h-4', ariaLabel = 'Loading' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={`${className} animate-spin`}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLoading} />
    </svg>
  );
}

/**
 * Copy icon
 * 
 * Material Design icon component for copy/duplicate actions.
 * 
 * @component
 * @preview ![content-copy](https://unpkg.com/@mdi/svg@7.4.47/svg/content-copy.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/content-copy.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CopyIcon className="w-5 h-5" ariaLabel="Copy" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function CopyIcon({ className = 'w-4 h-4', ariaLabel = 'Copy' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiContentCopy} />
    </svg>
  );
}

/**
 * Grid view icon
 * 
 * Material Design icon component for grid layout toggle.
 * 
 * @component
 * @preview ![view-grid](https://unpkg.com/@mdi/svg@7.4.47/svg/view-grid.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/view-grid.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <GridViewIcon className="w-5 h-5" ariaLabel="Grid View" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function GridViewIcon({ className = 'w-4 h-4', ariaLabel = 'Grid View' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiViewGrid} />
    </svg>
  );
}

/**
 * List view icon
 * 
 * Material Design icon component for list layout toggle.
 * 
 * @component
 * @preview ![view-list](https://unpkg.com/@mdi/svg@7.4.47/svg/view-list.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/view-list.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ListViewIcon className="w-5 h-5" ariaLabel="List View" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ListViewIcon({ className = 'w-4 h-4', ariaLabel = 'List View' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiViewList} />
    </svg>
  );
}

/**
 * File icon
 * 
 * Material Design icon component for file/document references.
 * 
 * @component
 * @preview ![file](https://unpkg.com/@mdi/svg@7.4.47/svg/file.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/file.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <FileIcon className="w-5 h-5" ariaLabel="File" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FileIcon({ className = 'w-4 h-4', ariaLabel = 'File' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFile} />
    </svg>
  );
}

/**
 * FileJsonIcon - JSON file icon (from @mdi/js mdiFile)
 * @preview https://cdn.jsdelivr.net/npm/@mdi/js/mdiFile.js
 */
export function FileJsonIcon({ className = 'w-4 h-4', ariaLabel = 'JSON file' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFile} />
    </svg>
  );
}

/**
 * Folder icon
 * 
 * Material Design icon component for folder/directory references.
 * 
 * @component
 * @preview ![folder](https://unpkg.com/@mdi/svg@7.4.47/svg/folder.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/folder.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <FolderIcon className="w-5 h-5" ariaLabel="Folder" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FolderIcon({ className = 'w-4 h-4', ariaLabel = 'Folder' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFolder} />
    </svg>
  );
}

/**
 * Folder open icon
 * 
 * Material Design icon component for open file storage references.
 * 
 * @component
 * @preview ![folder-open](https://unpkg.com/@mdi/svg@7.4.47/svg/folder-open.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/folder-open.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <FolderOpenIcon className="w-5 h-5" ariaLabel="Files" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FolderOpenIcon({ className = 'w-4 h-4', ariaLabel = 'Files' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFolderOpen} />
    </svg>
  );
}

/**
 * Cube icon
 * 
 * Material Design icon component for 3D model/content references.
 * 
 * @component
 * @preview ![cube](https://unpkg.com/@mdi/svg@7.4.47/svg/cube.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/cube.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CubeIcon className="w-5 h-5" ariaLabel="3D Model" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function CubeIcon({ className = 'w-4 h-4', ariaLabel = '3D Model' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCube} />
    </svg>
  );
}

/**
 * Chevron down icon
 * 
 * Material Design icon component for dropdown/expand actions.
 * 
 * @component
 * @preview ![chevron-down](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-down.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-down.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ChevronDownIcon className="w-5 h-5" ariaLabel="Expand" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ChevronDownIcon({ className = 'w-4 h-4', ariaLabel = 'Chevron down' }: Omit<IconProps, 'isOn'>) {
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

/**
 * ChevronRightIcon - Chevron right icon (from @mdi/js mdiChevronRight)
 * @preview https://cdn.jsdelivr.net/npm/@mdi/js/mdiChevronRight.js
 */
export function ChevronRightIcon({ className = 'w-4 h-4', ariaLabel = 'Chevron right' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronRight} />
    </svg>
  );
}

/**
 * ChevronLeftIcon - Chevron left icon (from @mdi/js mdiChevronLeft)
 * @preview https://cdn.jsdelivr.net/npm/@mdi/js/mdiChevronLeft.js
 */
export function ChevronLeftIcon({ className = 'w-4 h-4', ariaLabel = 'Chevron left' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronLeft} />
    </svg>
  );
}

/**
 * External link icon
 * 
 * Material Design icon component for opening external links.
 * 
 * @component
 * @preview ![open-in-new](https://unpkg.com/@mdi/svg@7.4.47/svg/open-in-new.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/open-in-new.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ExternalLinkIcon className="w-5 h-5" ariaLabel="Open external link" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ExternalLinkIcon({ className = 'w-4 h-4', ariaLabel = 'External link' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiOpenInNew} />
    </svg>
  );
}

/**
 * Camera icon
 * 
 * Material Design icon component for camera/photo actions.
 * 
 * @component
 * @preview ![camera](https://unpkg.com/@mdi/svg@7.4.47/svg/camera.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/camera.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CameraIcon className="w-5 h-5" ariaLabel="Camera" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function CameraIcon({ className = 'w-4 h-4', ariaLabel = 'Camera' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCamera} />
    </svg>
  );
}

export function NfcIcon({ className = 'w-4 h-4', ariaLabel = 'NFC' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiNfc} />
    </svg>
  );
}

/**
 * Minus icon
 * 
 * Material Design icon component for remove/subtract actions.
 * 
 * @component
 * @preview ![minus](https://unpkg.com/@mdi/svg@7.4.47/svg/minus.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/minus.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <MinusIcon className="w-5 h-5" ariaLabel="Remove" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function MinusIcon({ className = 'w-4 h-4', ariaLabel = 'Remove' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiMinus} />
    </svg>
  );
}

/**
 * Printer icon
 * 
 * Material Design icon component for printer/printing references.
 * 
 * @component
 * @preview ![printer](https://unpkg.com/@mdi/svg@7.4.47/svg/printer.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/printer.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PrinterIcon className="w-5 h-5" ariaLabel="Printer" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function PrinterIcon({ className = 'w-4 h-4', ariaLabel = 'Printer' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPrinter3d} />
    </svg>
  );
}

/**
 * Wrench icon
 * 
 * Material Design icon component for tools/maintenance actions.
 * 
 * @component
 * @preview ![wrench](https://unpkg.com/@mdi/svg@7.4.47/svg/wrench.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/wrench.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <WrenchIcon className="w-5 h-5" ariaLabel="Tools" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function WrenchIcon({ className = 'w-4 h-4', ariaLabel = 'Tools' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiWrench} />
    </svg>
  );
}

/**
 * Tools icon for maintenance mode
 * 
 * Material Design icon component for maintenance mode indicator.
 * 
 * @component
 * @preview ![tools](https://unpkg.com/@mdi/svg@7.4.47/svg/tools.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/tools.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ToolsIcon className="w-5 h-5" ariaLabel="Maintenance Mode" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ToolsIcon({ className = 'w-4 h-4', ariaLabel = 'Maintenance Mode' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTools} />
    </svg>
  );
}

/**
 * Dashboard icon
 * 
 * Material Design icon component for dashboard/overview views.
 * 
 * @component
 * @preview ![view-dashboard](https://unpkg.com/@mdi/svg@7.4.47/svg/view-dashboard.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/view-dashboard.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <DashboardIcon className="w-5 h-5" ariaLabel="Dashboard" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function DashboardIcon({ className = 'w-4 h-4', ariaLabel = 'Dashboard' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiViewDashboard} />
    </svg>
  );
}

/**
 * Trending up icon
 * 
 * Material Design icon component for upward trend/growth indicators.
 * 
 * @component
 * @preview ![trending-up](https://unpkg.com/@mdi/svg@7.4.47/svg/trending-up.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/trending-up.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TrendingUpIcon className="w-5 h-5" ariaLabel="Trending up" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TrendingUpIcon({ className = 'w-4 h-4', ariaLabel = 'Trending up' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTrendingUp} />
    </svg>
  );
}

/**
 * Panel right icon
 * 
 * Material Design icon component for opening side panels/details.
 * 
 * @component
 * @preview ![pan-right](https://unpkg.com/@mdi/svg@7.4.47/svg/pan-right.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/pan-right.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PanelRightIcon className="w-5 h-5" ariaLabel="Open panel" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function PanelRightIcon({ className = 'w-4 h-4', ariaLabel = 'Open panel' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPageLayoutSidebarRight} />
    </svg>
  );
}

/**
 * Image icon
 * 
 * Material Design icon component for image/photo references.
 * 
 * @component
 * @preview ![image](https://unpkg.com/@mdi/svg@7.4.47/svg/image.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/image.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ImageIcon className="w-5 h-5" ariaLabel="Image" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ImageIcon({ className = 'w-4 h-4', ariaLabel = 'Image' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiImage} />
    </svg>
  );
}

/**
 * Video icon
 * 
 * Material Design icon component for video/stream references.
 * 
 * @component
 * @preview ![video](https://unpkg.com/@mdi/svg@7.4.47/svg/video.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/video.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <VideoIcon className="w-5 h-5" ariaLabel="Video" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function VideoIcon({ className = 'w-4 h-4', ariaLabel = 'Video' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiVideo} />
    </svg>
  );
}

/**
 * Menu icon
 * 
 * Material Design icon component for menu/navigation toggles.
 * 
 * @component
 * @preview ![menu](https://unpkg.com/@mdi/svg@7.4.47/svg/menu.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/menu.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <MenuIcon className="w-5 h-5" ariaLabel="Menu" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function MenuIcon({ className = 'w-4 h-4', ariaLabel = 'Menu' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiMenu} />
    </svg>
  );
}

/**
 * Account check icon
 * 
 * Material Design icon component for verified/approved user status.
 * 
 * @component
 * @preview ![account-check](https://unpkg.com/@mdi/svg@7.4.47/svg/account-check.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/account-check.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <AccountCheckIcon className="w-5 h-5" ariaLabel="User verified" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function AccountCheckIcon({ className = 'w-4 h-4', ariaLabel = 'User verified' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAccountCheck} />
    </svg>
  );
}

/**
 * Account icon
 * 
 * Material Design icon component for user/account references.
 * 
 * @component
 * @preview ![account](https://unpkg.com/@mdi/svg@7.4.47/svg/account.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/account.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <AccountIcon className="w-5 h-5" ariaLabel="Account" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function AccountIcon({ className = 'w-4 h-4', ariaLabel = 'Account' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAccount} />
    </svg>
  );
}

/**
 * Logout icon
 * 
 * Material Design icon component for logout/sign out actions.
 * 
 * @component
 * @preview ![logout](https://unpkg.com/@mdi/svg@7.4.47/svg/logout.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/logout.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <LogoutIcon className="w-5 h-5" ariaLabel="Sign out" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function LogoutIcon({ className = 'w-4 h-4', ariaLabel = 'Sign out' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLogout} />
    </svg>
  );
}

/**
 * Login icon
 * 
 * Material Design icon component for login/sign in actions.
 * 
 * @component
 * @preview ![login](https://unpkg.com/@mdi/svg@7.4.47/svg/login.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/login.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <LoginIcon className="w-5 h-5" ariaLabel="Sign in" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function LoginIcon({ className = 'w-4 h-4', ariaLabel = 'Sign in' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLogin} />
    </svg>
  );
}

/**
 * Users/account multiple icon
 * 
 * Material Design icon component for multiple users or team management.
 * 
 * @component
 * @preview ![account-multiple](https://unpkg.com/@mdi/svg@7.4.47/svg/account-multiple.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/account-multiple.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <UsersIcon className="w-5 h-5" ariaLabel="Users" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function UsersIcon({ className = 'w-4 h-4', ariaLabel = 'Users' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAccountMultiple} />
    </svg>
  );
}

/**
 * Gear/cog icon
 * 
 * Material Design icon component for settings or configuration.
 * 
 * @component
 * @preview ![cog](https://unpkg.com/@mdi/svg@7.4.47/svg/cog.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/cog.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <GearIcon className="w-5 h-5" ariaLabel="Settings" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function GearIcon({ className = 'w-4 h-4', ariaLabel = 'Settings' }: Omit<IconProps, 'isOn'>) {
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

/**
 * Layers icon
 * 
 * Material Design icon component for layers, stacks, or queue operations.
 * 
 * @component
 * @preview ![layers](https://unpkg.com/@mdi/svg@7.4.47/svg/layers.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/layers.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <LayersIcon className="w-5 h-5" ariaLabel="Layers" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function LayersIcon({ className = 'w-4 h-4', ariaLabel = 'Layers' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLayers} />
    </svg>
  );
}

/**
 * Activity/pulse icon
 * 
 * Material Design icon component for activity or heartbeat status.
 * 
 * @component
 * @preview ![pulse](https://unpkg.com/@mdi/svg@7.4.47/svg/pulse.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/pulse.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ActivityIcon className="w-5 h-5" ariaLabel="Activity" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ActivityIcon({ className = 'w-4 h-4', ariaLabel = 'Activity' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPulse} />
    </svg>
  );
}

/**
 * Battery icon
 * 
 * Material Design icon component for power/battery status.
 * 
 * @component
 * @preview ![battery](https://unpkg.com/@mdi/svg@7.4.47/svg/battery.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/battery.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <BatteryIcon className="w-5 h-5" ariaLabel="Power" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function BatteryIcon({ className = 'w-4 h-4', ariaLabel = 'Power' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiBattery} />
    </svg>
  );
}

/**
 * User Plus/Add User icon
 * 
 * Material Design icon component for adding users or registration.
 * 
 * @component
 * @preview ![account-plus](https://unpkg.com/@mdi/svg@7.4.47/svg/account-plus.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/account-plus.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <UserPlusIcon className="w-5 h-5" ariaLabel="Register" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function UserPlusIcon({ className = 'w-4 h-4', ariaLabel = 'Add user' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiAccountPlus} />
    </svg>
  );
}

/**
 * Sun icon
 * 
 * Material Design icon component for sunny/light theme indication.
 * 
 * @component
 * @preview ![weather-sunny](https://unpkg.com/@mdi/svg@7.4.47/svg/weather-sunny.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/weather-sunny.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SunIcon className="w-5 h-5" ariaLabel="Light theme" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function SunIcon({ className = 'w-4 h-4', ariaLabel = 'Light theme' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiWeatherSunny} />
    </svg>
  );
}

/**
 * Moon icon
 * 
 * Material Design icon component for dark theme indication.
 * 
 * @component
 * @preview ![moon-waning-crescent](https://unpkg.com/@mdi/svg@7.4.47/svg/moon-waning-crescent.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/moon-waning-crescent.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <MoonIcon className="w-5 h-5" ariaLabel="Dark theme" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function MoonIcon({ className = 'w-4 h-4', ariaLabel = 'Dark theme' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiMoonWaningCrescent} />
    </svg>
  );
}

/**
 * Monitor/Desktop icon
 * 
 * Material Design icon component for system/computer settings.
 * 
 * @component
 * @preview ![desktop-tower](https://unpkg.com/@mdi/svg@7.4.47/svg/desktop-tower.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/desktop-tower.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <MonitorIcon className="w-5 h-5" ariaLabel="System" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function MonitorIcon({ className = 'w-4 h-4', ariaLabel = 'System' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiDesktopTower} />
    </svg>
  );
}

/**
 * Clock icon
 * 
 * Material Design icon component for time or scheduling.
 * 
 * @component
 * @preview ![clock-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/clock-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/clock-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ClockIcon className="w-5 h-5" ariaLabel="Time" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ClockIcon({ className = 'w-4 h-4', ariaLabel = 'Time' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiClockOutline} />
    </svg>
  );
}

/**
 * X or close circle icon
 * 
 * Material Design icon component for cancel/delete operations.
 * 
 * @component
 * @preview ![close-circle](https://unpkg.com/@mdi/svg@7.4.47/svg/close-circle.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/close-circle.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <XCircleIcon className="w-5 h-5" ariaLabel="Cancel" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function XCircleIcon({ className = 'w-4 h-4', ariaLabel = 'Cancel' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCloseCircle} />
    </svg>
  );
}

/**
 * Clear filters icon
 * 
 * Material Design icon component for clearing filters/resetting.
 * Uses the same close-circle icon as XCircleIcon.
 * 
 * @component
 * @preview ![close-circle](https://unpkg.com/@mdi/svg@7.4.47/svg/close-circle.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/close-circle.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ClearFiltersIcon className="w-5 h-5" ariaLabel="Clear filters" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ClearFiltersIcon({ className = 'w-4 h-4', ariaLabel = 'Clear filters' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCloseCircle} />
    </svg>
  );
}

/**
 * Chevrons right icon
 * 
 * Material Design icon component for double chevron/forward navigation.
 * 
 * @component
 * @preview ![chevron-double-right](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-double-right.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-double-right.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ChevronsRightIcon className="w-5 h-5" ariaLabel="Move right" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ChevronsRightIcon({ className = 'w-4 h-4', ariaLabel = 'Move forward' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronDoubleRight} />
    </svg>
  );
}

/**
 * Chevrons left icon
 * 
 * Material Design icon component for double chevron/back navigation.
 * 
 * @component
 * @preview ![chevron-double-left](https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-double-left.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chevron-double-left.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ChevronsLeftIcon className="w-5 h-5" ariaLabel="Move left" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ChevronsLeftIcon({ className = 'w-4 h-4', ariaLabel = 'Move back' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChevronDoubleLeft} />
    </svg>
  );
}

/**
 * More vertical/menu icon
 * 
 * Material Design icon component for additional options menu.
 * 
 * @component
 * @preview ![dots-vertical](https://unpkg.com/@mdi/svg@7.4.47/svg/dots-vertical.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/dots-vertical.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <MoreVerticalIcon className="w-5 h-5" ariaLabel="More options" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function MoreVerticalIcon({ className = 'w-4 h-4', ariaLabel = 'More options' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiDotsVertical} />
    </svg>
  );
}

/**
 * Circle icon
 * 
 * Material Design icon component for circular status indicators.
 * 
 * @component
 * @preview ![circle-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/circle-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/circle-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CircleIcon className="w-5 h-5" ariaLabel="Indicator" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function CircleIcon({ className = 'w-4 h-4', ariaLabel = 'Indicator' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCircleOutline} />
    </svg>
  );
}
/**
 * Database icon
 * 
 * Material Design icon component for database or catalog operations.
 * 
 * @component
 * @preview ![database](https://unpkg.com/@mdi/svg@7.4.47/svg/database.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/database.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <DatabaseIcon className="w-5 h-5" ariaLabel="Database" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function DatabaseIcon({ className = 'w-4 h-4', ariaLabel = 'Database' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiDatabase} />
    </svg>
  );
}

/**
 * Test/Flask icon
 * 
 * Material Design icon component for testing or experimental features.
 * 
 * @component
 * @preview ![flask](https://unpkg.com/@mdi/svg@7.4.47/svg/flask.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/flask.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TestIcon className="w-5 h-5" ariaLabel="Test" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TestIcon({ className = 'w-4 h-4', ariaLabel = 'Test' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFlask} />
    </svg>
  );
}

/**
 * Package icon
 * 
 * Material Design icon component for packages or spools.
 * 
 * @component
 * @preview ![package-variant](https://unpkg.com/@mdi/svg@7.4.47/svg/package-variant.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/package-variant.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PackageIcon className="w-5 h-5" ariaLabel="Package" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function PackageIcon({ className = 'w-4 h-4', ariaLabel = 'Package' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPackageVariant} />
    </svg>
  );
}

/**
 * Filter icon
 * 
 * Material Design icon component for filtering operations.
 * 
 * @component
 * @preview ![filter-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/filter-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/filter-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <FilterIcon className="w-5 h-5" ariaLabel="Filter" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FilterIcon({ className = 'w-4 h-4', ariaLabel = 'Filter' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFilterOutline} />
    </svg>
  );
}

/**
 * Table icon
 * 
 * Material Design icon component for table views or structured data.
 * 
 * @component
 * @preview ![table-large-remove](https://unpkg.com/@mdi/svg@7.4.47/svg/table-large-remove.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/table-large-remove.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TableIcon className="w-5 h-5" ariaLabel="Table" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TableIcon({ className = 'w-4 h-4', ariaLabel = 'Table' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTableLargeRemove} />
    </svg>
  );
}

/**
 * Text/Document icon
 * 
 * Material Design icon component for text files or documents.
 * 
 * @component
 * @preview ![file-document](https://unpkg.com/@mdi/svg@7.4.47/svg/file-document.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/file-document.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TextIcon className="w-5 h-5" ariaLabel="Document" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TextIcon({ className = 'w-4 h-4', ariaLabel = 'Document' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFileDocument} />
    </svg>
  );
}

/**
 * Arrow up/down icon
 * 
 * Material Design icon component for sorting or reordering.
 * 
 * @component
 * @preview ![arrow-up-down](https://unpkg.com/@mdi/svg@7.4.47/svg/arrow-up-down.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/arrow-up-down.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <SortIcon className="w-5 h-5" ariaLabel="Sort" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function SortIcon({ className = 'w-4 h-4', ariaLabel = 'Sort' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiArrowUpDown} />
    </svg>
  );
}

/**
 * WiFi icon
 * 
 * Material Design icon component for wireless connectivity.
 * 
 * @component
 * @preview ![wifi](https://unpkg.com/@mdi/svg@7.4.47/svg/wifi.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/wifi.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <WiFiIcon className="w-5 h-5" ariaLabel="WiFi" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function WiFiIcon({ className = 'w-4 h-4', ariaLabel = 'WiFi' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiWifi} />
    </svg>
  );
}

/**
 * Thermometer icon
 * 
 * Material Design icon component for temperature or thermal monitoring.
 * 
 * @component
 * @preview ![thermometer](https://unpkg.com/@mdi/svg@7.4.47/svg/thermometer.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/thermometer.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ThermometerIcon className="w-5 h-5" ariaLabel="Temperature" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ThermometerIcon({ className = 'w-4 h-4', ariaLabel = 'Temperature' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiThermometer} />
    </svg>
  );
}

/**
 * List/Queue icon
 * 
 * Material Design icon component for lists or queues.
 * 
 * @component
 * @preview ![list-box-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/list-box-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/list-box-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ListIcon className="w-5 h-5" ariaLabel="List" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ListIcon({ className = 'w-4 h-4', ariaLabel = 'List' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiListBoxOutline} />
    </svg>
  );
}

/**
 * Network/Router icon
 * 
 * Material Design icon component for network or connection settings.
 * 
 * @component
 * @preview ![network](https://unpkg.com/@mdi/svg@7.4.47/svg/network.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/network.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <NetworkIcon className="w-5 h-5" ariaLabel="Network" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function NetworkIcon({ className = 'w-4 h-4', ariaLabel = 'Network' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiNetwork} />
    </svg>
  );
}

/**
 * Calendar icon
 * 
 * Material Design icon component for dates or calendar operations.
 * 
 * @component
 * @preview ![calendar](https://unpkg.com/@mdi/svg@7.4.47/svg/calendar.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/calendar.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <CalendarIcon className="w-5 h-5" ariaLabel="Date" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function CalendarIcon({ className = 'w-4 h-4', ariaLabel = 'Date' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCalendar} />
    </svg>
  );
}

/**
 * Chart icon
 * 
 * Material Design icon component for charts, graphs, or analytics.
 * 
 * @component
 * @preview ![chart-box](https://unpkg.com/@mdi/svg@7.4.47/svg/chart-box.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/chart-box.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <ChartIcon className="w-5 h-5" ariaLabel="Chart" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function ChartIcon({ className = 'w-4 h-4', ariaLabel = 'Chart' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiChartBox} />
    </svg>
  );
}

/**
 * Timer icon
 * 
 * Material Design icon component for timers or duration tracking.
 * 
 * @component
 * @preview ![timer-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/timer-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/timer-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <TimerIcon className="w-5 h-5" ariaLabel="Timer" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function TimerIcon({ className = 'w-4 h-4', ariaLabel = 'Timer' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTimerOutline} />
    </svg>
  );
}

/**
 * File import icon
 * 
 * Material Design icon component for import/upload file actions.
 * 
 * @component
 * @preview ![file-import-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/file-import-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/file-import-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <FileImportIcon className="w-5 h-5" ariaLabel="Import file" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FileImportIcon({ className = 'w-4 h-4', ariaLabel = 'Import file' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFileImportOutline} />
    </svg>
  );
}

/**
 * File export icon
 * 
 * Material Design icon component for export/download file actions.
 * 
 * @component
 * @preview ![file-export-outline](https://unpkg.com/@mdi/svg@7.4.47/svg/file-export-outline.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/file-export-outline.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <FileExportIcon className="w-5 h-5" ariaLabel="Export file" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FileExportIcon({ className = 'w-4 h-4', ariaLabel = 'Export file' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiFileExportOutline} />
    </svg>
  );
}

/**
 * Printer search icon
 * 
 * Material Design icon component for printer discovery/search actions.
 * 
 * @component
 * @preview ![printer-search](https://unpkg.com/@mdi/svg@7.4.47/svg/printer-search.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/printer-search.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <PrinterSearchIcon className="w-5 h-5" ariaLabel="Discover printers" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function PrinterSearchIcon({ className = 'w-4 h-4', ariaLabel = 'Discover printers' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiPrinterSearch} />
    </svg>
  );
}

/**
 * Loader icon
 * 
 * Material Design icon component for loading spinners or progress indicators.
 * 
 * @component
 * @preview ![loading](https://unpkg.com/@mdi/svg@7.4.47/svg/loading.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/loading.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @example
 * <LoaderIcon className="w-5 h-5 animate-spin" ariaLabel="Loading" />
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function LoaderIcon({ className = 'w-4 h-4', ariaLabel = 'Loading' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiLoading} />
    </svg>
  );
}

/**
 * GridIcon - Grid layout icon (from @mdi/js mdiViewGrid)
 * @preview https://cdn.jsdelivr.net/npm/@mdi/js/mdiViewGrid.js
 */
export function GridIcon({ className = 'w-4 h-4', ariaLabel = 'Grid' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiViewGrid} />
    </svg>
  );
}

/**
 * Perspective view icon - 3D perspective view
 */
export function PerspectiveIcon({ className = 'w-4 h-4', ariaLabel = 'Perspective View' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiCube} />
    </svg>
  );
}

/**
 * Orthographic view icon - 2D flat view
 */
export function OrthographicIcon({ className = 'w-4 h-4', ariaLabel = 'Orthographic View' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiViewGrid} />
    </svg>
  );
}

/**
 * Filament load icon - Arrow down into tray
 *
 * @component
 * @preview ![tray-arrow-down](https://unpkg.com/@mdi/svg@7.4.47/svg/tray-arrow-down.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/tray-arrow-down.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FilamentLoadIcon({ className = 'w-4 h-4', ariaLabel = 'Load filament' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTrayArrowDown} />
    </svg>
  );
}

/**
 * Filament unload icon - Arrow up from tray
 *
 * @component
 * @preview ![tray-arrow-up](https://unpkg.com/@mdi/svg@7.4.47/svg/tray-arrow-up.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/tray-arrow-up.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FilamentUnloadIcon({ className = 'w-4 h-4', ariaLabel = 'Unload filament' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiTrayArrowUp} />
    </svg>
  );
}

/**
 * Filament change icon - Swap vertical arrows
 *
 * @component
 * @preview ![swap-vertical](https://unpkg.com/@mdi/svg@7.4.47/svg/swap-vertical.svg) — https://unpkg.com/@mdi/svg@7.4.47/svg/swap-vertical.svg
 * @param {Omit<IconProps, 'isOn'>} props - Icon properties (className, ariaLabel)
 * @returns {JSX.Element} SVG element
 * @see — https://materialdesignicons.com - Material Design Icons Library
 */
export function FilamentChangeIcon({ className = 'w-4 h-4', ariaLabel = 'Change filament' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiSwapVertical} />
    </svg>
  );
}

/**
 * Recenter camera icon - Reset camera position
 */
export function RecenterIcon({ className = 'w-4 h-4', ariaLabel = 'Recenter View' }: Omit<IconProps, 'isOn'>) {
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

/**
 * Eject icon - used for ejecting/removing active spool
 */
export function EjectIcon({ className = 'w-4 h-4', ariaLabel = 'Eject' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiEject} />
    </svg>
  );
}

/**
 * Clipboard list icon — project management / task tracking
 */
export function ClipboardListIcon({ className = 'w-4 h-4', ariaLabel = 'Projects' }: Omit<IconProps, 'isOn'>) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={mdiClipboardListOutline} />
    </svg>
  );
}

