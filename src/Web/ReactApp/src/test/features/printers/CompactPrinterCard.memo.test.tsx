import { describe, expect, it, vi } from 'vitest';
import {
  areCompactPrinterCardPropsEqual,
  type CompactPrinterCardMemoProps,
} from '@/features/printers/utils/compactPrinterCardMemo';
import { PrinterBackend, type Printer } from '@/types/api';

function createPrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Printer 1',
    backend: PrinterBackend.Moonraker,
    backendUrl: 'http://printer-1.local',
    frontendUrl: 'http://printer-1.local',
    isOnline: true,
    isReachable: true,
    state: 'Idle',
    progress: 0,
    ...overrides,
  } as Printer;
}

function createProps(printer: Printer): CompactPrinterCardMemoProps {
  return {
    printer,
    onExpand: vi.fn(),
    onEdit: vi.fn(),
  };
}

describe('CompactPrinterCard memoization', () => {
  it('skips rendering when parent recreates unchanged printer props', () => {
    const previous = createProps(createPrinter());
    const next = createProps(createPrinter());

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(true);
  });

  it('renders when live printer status changes', () => {
    const previous = createProps(createPrinter({ progress: 10, state: 'Printing' }));
    const next = createProps(createPrinter({ progress: 11, state: 'Printing' }));

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(false);
  });
});
