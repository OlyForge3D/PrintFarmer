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

  const previousEntries = Object.entries(previous);
  const nextEntries = Object.entries(next);
  const previousKeys = previousEntries.map(([key]) => key);
  const nextKeys = nextEntries.map(([key]) => key);
  if (previousKeys.length !== nextKeys.length) {
    return false;
  }

  return previousEntries.every(([key, value]) => (
    nextEntries.some(([nextKey, nextValue]) => nextKey === key && Object.is(value, nextValue))
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
