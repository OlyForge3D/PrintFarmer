import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PrinterBackend, type Printer } from '@/types/api';
import { PrinterDetailsSidebar } from '../PrinterDetailsSidebar';

const mockInvalidateQueries = vi.fn();
const mockRefetch = vi.fn();

vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({
    data: undefined,
    isLoading: false,
    refetch: mockRefetch,
  }),
  useQueryClient: () => ({
    invalidateQueries: mockInvalidateQueries,
  }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinter: () => ({
    data: undefined,
    isLoading: false,
    refetch: mockRefetch,
  }),
  usePrinterDetails: () => ({ data: undefined }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: () => ({ ready: false }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: undefined }),
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock('@/features/printers/components/PrinterFilesModal', () => ({
  PrinterFilesModal: () => null,
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: () => null,
}));

const printer: Printer = {
  id: 'printer-1',
  name: 'Printer Alpha',
  manufacturerName: 'Prusa',
  modelName: 'MK4',
  backend: PrinterBackend.PrusaLink,
  isOnline: true,
  isEnabled: true,
  state: 'Idle',
  hotendTemp: 25,
  hotendTarget: 0,
  bedTemp: 23,
  bedTarget: 0,
  x: 0,
  y: 0,
  z: 0,
};

describe('PrinterDetailsSidebar', () => {
  it('bounds content layout on desktop and lets the inner region scroll', () => {
    const { container } = render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="content"
      />
    );

    const shell = container.firstElementChild;
    expect(shell).toHaveClass('w-full', 'max-w-sm', 'overflow-hidden', 'flex', 'flex-col');
    expect(shell).toHaveClass('lg:max-h-[calc(100dvh-5rem)]', 'lg:min-h-0');

    const scrollRegion = shell?.querySelector('.overflow-y-auto');
    expect(scrollRegion).toHaveClass('flex-1', 'min-h-0', 'overflow-y-auto');
  });

  it('keeps the drawer layout height contract unchanged', () => {
    const { container } = render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />
    );

    const shell = container.firstElementChild;
    expect(shell).toHaveClass('w-[calc(100%-1.5rem)]', 'h-[calc(100%-1.5rem)]', 'shrink-0');
    expect(shell).not.toHaveClass('lg:max-h-[calc(100dvh-5rem)]');
  });
});
