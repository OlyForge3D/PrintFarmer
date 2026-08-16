import { PrinterBackend, type PrinterBackendCapabilitiesDto } from '@/types/api';

/**
 * Backends whose clients implement the server-side `ISupportsHistory` contract.
 *
 * `PrinterBackendCapabilitiesService` derives the authoritative `supportsHistory`
 * flag from exactly that check, so this mirror is only used as the pre-hydration
 * fallback while `/api/printers/{id}/capabilities` is still in flight. Keep the two
 * in sync — a backend missing here has its History action hidden until capabilities
 * arrive, which is the #1584 regression PrusaLink hit.
 */
const HISTORY_CAPABLE_BACKENDS: readonly PrinterBackend[] = [
  PrinterBackend.Moonraker,
  PrinterBackend.OctoPrint,
  PrinterBackend.PrusaLink,
  PrinterBackend.SDCP,
];

export function backendSupportsHistory(backend: PrinterBackend): boolean {
  return HISTORY_CAPABLE_BACKENDS.includes(backend);
}

export interface PrinterSupport {
  supportsControlOperations: boolean;
  supportsMovement: boolean;
  supportsTemperatureControl: boolean;
  supportsHistory: boolean;
  supportsFileList: boolean;
  supportsFilamentControl: boolean;
  supportsObjectExclusion: boolean;
}

/**
 * Normalize optional backend capabilities to a stable set of boolean flags.
 *
 * Defaulting to `true` preserves existing UX while capabilities are loading
 * (i.e., we prefer not to prematurely disable controls).
 */
export function getPrinterSupport(
  backendCapabilities?: PrinterBackendCapabilitiesDto,
  defaults?: Partial<PrinterSupport>
): PrinterSupport {
  return {
    supportsControlOperations: backendCapabilities?.supportsControlOperations ?? defaults?.supportsControlOperations ?? true,
    supportsMovement: backendCapabilities?.supportsMovement ?? defaults?.supportsMovement ?? true,
    supportsTemperatureControl: backendCapabilities?.supportsTemperatureControl ?? defaults?.supportsTemperatureControl ?? true,
    supportsHistory: backendCapabilities?.supportsHistory ?? defaults?.supportsHistory ?? true,
    supportsFileList: backendCapabilities?.supportsFileList ?? defaults?.supportsFileList ?? true,
    supportsFilamentControl: backendCapabilities?.supportsFilamentControl ?? defaults?.supportsFilamentControl ?? false,
    supportsObjectExclusion: backendCapabilities?.supportsObjectExclusion ?? defaults?.supportsObjectExclusion ?? false,
  };
}

export function canPauseOrResume(args: {
  isOnline: boolean;
  isEnabled?: boolean;
  isPrinting: boolean;
  isPaused: boolean;
  support: PrinterSupport;
}): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsControlOperations && (args.isPrinting || args.isPaused);
}

export function canCancel(args: {
  isOnline: boolean;
  isEnabled?: boolean;
  isPrinting: boolean;
  isPaused: boolean;
  support: PrinterSupport;
}): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsControlOperations && (args.isPrinting || args.isPaused);
}

export function canEmergencyStop(args: { isOnline: boolean; isEnabled?: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsControlOperations;
}

export function canDisableMotors(args: { isOnline: boolean; isEnabled?: boolean; isPrinting: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsControlOperations && !args.isPrinting;
}

export function canMove(args: { isOnline: boolean; isEnabled?: boolean; isPrinting: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsMovement && !args.isPrinting;
}

export function canSetStep(args: { isOnline: boolean; support: PrinterSupport }): boolean {
  return args.isOnline && args.support.supportsMovement;
}

export function canUseManualMove(args: { isOnline: boolean; isEnabled?: boolean; isPrinting: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsMovement && !args.isPrinting;
}

export function canSetTemperatures(args: { isOnline: boolean; isEnabled?: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsTemperatureControl;
}

export function canCooldown(args: { isOnline: boolean; isEnabled?: boolean; isPrinting: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsTemperatureControl && !args.isPrinting;
}

export function canOpenFiles(args: { isOnline: boolean; isEnabled?: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsFileList;
}

export function canOpenHistory(args: { isOnline: boolean; isEnabled?: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsHistory;
}

export function canFilamentControl(args: { isOnline: boolean; isEnabled?: boolean; isPrinting: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsFilamentControl && !args.isPrinting;
}

export function canFilamentChange(args: { isOnline: boolean; isEnabled?: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && args.support.supportsFilamentControl;
}

export function canExcludeObject(args: { isOnline: boolean; isEnabled?: boolean; isPrinting: boolean; isPaused?: boolean; support: PrinterSupport }): boolean {
  const isEnabled = args.isEnabled ?? true;
  return isEnabled && args.isOnline && (args.isPrinting || !!args.isPaused) && args.support.supportsObjectExclusion;
}
