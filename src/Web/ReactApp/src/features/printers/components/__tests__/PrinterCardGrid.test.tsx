import { act, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Printer } from '@/types/api';
import {
  PRINTER_GRID_VIRTUALIZATION_THRESHOLD,
  PrinterCardGrid,
} from '@/features/printers/components/PrinterCardGrid';

const scrollToIndex = vi.fn();
interface MockVirtualizerOptions {
  count: number;
  overscan: number;
  scrollMargin: number;
  rangeExtractor: (range: { startIndex: number; endIndex: number }) => number[];
}

const virtualizerOptions: MockVirtualizerOptions[] = [];

vi.mock('@tanstack/react-virtual', () => ({
  defaultRangeExtractor: ({ startIndex, endIndex }: { startIndex: number; endIndex: number }) =>
    Array.from({ length: endIndex - startIndex + 1 }, (_, index) => startIndex + index),
  useWindowVirtualizer: (options: MockVirtualizerOptions) => {
    virtualizerOptions.push(options);
    const mountedRowCount = Math.min(options.count, 4);
    return {
      getVirtualItems: () => Array.from({ length: mountedRowCount }, (_, index) => ({
        index,
        key: `row-${index}`,
        start: index * 360,
      })),
      getTotalSize: () => options.count * 360,
      measureElement: vi.fn(),
      scrollToIndex,
    };
  },
}));

let observedResize: (() => void) | undefined;
let containerWidth = 1000;

function createPrinters(count: number): Printer[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `printer-${index}`,
    name: `Printer ${index}`,
    backend: 'Moonraker',
    isOnline: true,
    isEnabled: true,
    state: 'Idle',
  })) as Printer[];
}

function renderCard(printer: Printer) {
  return <button type="button">Open {printer.name}</button>;
}

describe('PrinterCardGrid', () => {
  beforeEach(() => {
    scrollToIndex.mockClear();
    virtualizerOptions.length = 0;
    observedResize = undefined;
    containerWidth = 1000;

    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(() => containerWidth);
    vi.stubGlobal('ResizeObserver', class ResizeObserver {
      constructor(callback: ResizeObserverCallback) {
        observedResize = () => callback([], this);
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    });
  });

  it('retains the existing DOM and mounts every card below the threshold', () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD - 1);

    const { container } = render(
      <PrinterCardGrid printers={printers} mode="compact" renderPrinter={renderCard} />,
    );

    expect(screen.getAllByRole('button')).toHaveLength(printers.length);
    expect(screen.queryByTestId('virtualized-printer-grid')).not.toBeInTheDocument();
    expect(container.querySelectorAll('[data-tour="printers-card"]')).toHaveLength(1);
    expect(virtualizerOptions).toHaveLength(0);
  });

  it('mounts bounded rows with overscan for a 60-printer compact grid', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);

    const { container } = render(
      <PrinterCardGrid printers={printers} mode="compact" renderPrinter={renderCard} />,
    );

    await waitFor(() => expect(virtualizerOptions.at(-1)?.count).toBe(20));
    expect(screen.getAllByRole('button')).toHaveLength(12);
    expect(virtualizerOptions.at(-1)?.overscan).toBe(2);
    expect(screen.getByRole('list', { name: 'Printers' })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')[0]).toHaveAttribute('aria-setsize', '60');
    expect(screen.getAllByRole('listitem')[0]).toHaveAttribute('aria-posinset', '1');
    expect(container.querySelectorAll('[data-tour="printers-card"]')).toHaveLength(1);
  });

  it('recalculates responsive rows when the grid width changes', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);
    render(<PrinterCardGrid printers={printers} mode="detailed" renderPrinter={renderCard} />);

    await waitFor(() => expect(virtualizerOptions.at(-1)?.count).toBe(30));
    expect(screen.getAllByRole('button')).toHaveLength(8);

    containerWidth = 600;
    act(() => observedResize?.());

    await waitFor(() => expect(virtualizerOptions.at(-1)?.count).toBe(60));
    expect(screen.getAllByRole('button')).toHaveLength(4);
  });

  it('preserves focus when realtime data updates a mounted printer', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);
    const { rerender } = render(
      <PrinterCardGrid printers={printers} mode="compact" renderPrinter={renderCard} />,
    );

    await waitFor(() => expect(screen.getAllByRole('button')).toHaveLength(12));
    const focusedCard = screen.getByRole('button', { name: 'Open Printer 4' });
    act(() => focusedCard.focus());

    await waitFor(() => {
      expect(virtualizerOptions.at(-1)?.rangeExtractor({ startIndex: 10, endIndex: 12 }))
        .toEqual([1, 10, 11, 12]);
    });

    const updatedPrinters = printers.map((printer) => printer.id === 'printer-4'
      ? { ...printer, state: 'Printing' }
      : printer);
    rerender(
      <PrinterCardGrid printers={updatedPrinters} mode="compact" renderPrinter={renderCard} />,
    );

    expect(focusedCard).toHaveFocus();
  });

  it('scrolls the active deep-linked printer row into view', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);
    render(
      <PrinterCardGrid
        printers={printers}
        mode="compact"
        activePrinterId="printer-55"
        renderPrinter={renderCard}
      />,
    );

    await waitFor(() => {
      expect(scrollToIndex).toHaveBeenCalledWith(18, { align: 'center' });
    });
  });
});
