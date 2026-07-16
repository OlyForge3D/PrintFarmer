import type { PrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import type { Printer, PrinterBackendCapabilitiesDto } from '@/types/api';

export interface CompactPrinterCardMemoProps {
  printer: Printer | PrinterDisplay;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onExpand?: (printerId: string) => void;
  onEdit?: (printer: Printer) => void;
}

function shallowEqualPrinter(previous: Printer | PrinterDisplay, next: Printer | PrinterDisplay): boolean {
  if (previous === next) {
    return true;
  }

  const previousRecord = previous as Record<string, unknown>;
  const nextRecord = next as Record<string, unknown>;
  const previousKeys = Object.keys(previousRecord);
  const nextKeys = Object.keys(nextRecord);
  if (previousKeys.length !== nextKeys.length) {
    return false;
  }

  return previousKeys.every((key) => (
    Object.prototype.hasOwnProperty.call(nextRecord, key) &&
    Object.is(previousRecord[key], nextRecord[key])
  ));
}

export function areCompactPrinterCardPropsEqual(
  previous: CompactPrinterCardMemoProps,
  next: CompactPrinterCardMemoProps,
): boolean {
  return (
    shallowEqualPrinter(previous.printer, next.printer) &&
    previous.backendCapabilities === next.backendCapabilities &&
    previous.onExpand === next.onExpand &&
    previous.onEdit === next.onEdit
  );
}
