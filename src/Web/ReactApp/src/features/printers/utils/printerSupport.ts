import type { PrinterBackendCapabilitiesDto } from '@/types/api';

export interface PrinterSupport {
  supportsControlOperations: boolean;
  supportsMovement: boolean;
  supportsTemperatureControl: boolean;
  supportsHistory: boolean;
  supportsFileList: boolean;
  supportsFilamentControl: boolean;
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
