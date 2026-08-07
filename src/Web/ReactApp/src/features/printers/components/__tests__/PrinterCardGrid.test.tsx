import { useState } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
    return {
      getVirtualItems: () => options
        .rangeExtractor({ startIndex: 0, endIndex: Math.min(options.count - 1, 3) })
        .map((index) => ({
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

const observedResizes: Array<() => void> = [];
let containerWidth = 1000;
let gridOffset = 0;

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

function StatefulCard({ printer }: { printer: Printer }) {
  const [note, setNote] = useState('');
  return (
    <input
      aria-label={`Note for ${printer.name}`}
      value={note}
      onChange={(event) => setNote(event.target.value)}
    />
  );
}

describe('PrinterCardGrid', () => {
  beforeEach(() => {
    scrollToIndex.mockClear();
    virtualizerOptions.length = 0;
    observedResizes.length = 0;
    containerWidth = 1000;
    gridOffset = 0;

    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(() => containerWidth);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({
      x: 0,
      y: gridOffset,
      top: gridOffset,
      right: containerWidth,
      bottom: gridOffset,
      left: 0,
      width: containerWidth,
      height: 0,
      toJSON: () => ({}),
    }));
    vi.stubGlobal('ResizeObserver', class ResizeObserver {
      constructor(callback: ResizeObserverCallback) {
        observedResizes.push(() => callback([], this));
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
    act(() => observedResizes.forEach((notify) => notify()));

    await waitFor(() => expect(virtualizerOptions.at(-1)?.count).toBe(60));
    expect(screen.getAllByRole('button')).toHaveLength(4);
  });

  it('preserves a focused stateful card when sorting and responsive columns move it between rows', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);
    const renderStatefulCard = (printer: Printer) => <StatefulCard printer={printer} />;
    const { rerender } = render(
      <PrinterCardGrid printers={printers} mode="compact" renderPrinter={renderStatefulCard} />,
    );

    const focusedCard = await screen.findByRole('textbox', { name: 'Note for Printer 4' });
    fireEvent.change(focusedCard, { target: { value: 'kept state' } });
    act(() => focusedCard.focus());

    await waitFor(() => {
      expect(virtualizerOptions.at(-1)?.rangeExtractor({ startIndex: 10, endIndex: 12 }))
        .toEqual([1, 10, 11, 12]);
    });

    containerWidth = 600;
    act(() => observedResizes.forEach((notify) => notify()));
    await waitFor(() => expect(virtualizerOptions.at(-1)?.count).toBe(60));

    const reorderedPrinters = [printers[4], ...printers.filter((printer) => printer.id !== 'printer-4')];
    rerender(
      <PrinterCardGrid
        printers={reorderedPrinters}
        mode="compact"
        renderPrinter={renderStatefulCard}
      />,
    );

    const movedCard = screen.getByRole('textbox', { name: 'Note for Printer 4' });
    expect(movedCard).toBe(focusedCard);
    expect(movedCard).toHaveValue('kept state');
    expect(movedCard).toHaveFocus();
  });

  it('refreshes scroll margin when surrounding layout content is inserted', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);
    render(<PrinterCardGrid printers={printers} mode="compact" renderPrinter={renderCard} />);

    await waitFor(() => expect(virtualizerOptions.at(-1)?.scrollMargin).toBe(0));
    gridOffset = 240;
    const grid = screen.getByTestId('virtualized-printer-grid');
    grid.parentElement?.prepend(document.createElement('aside'));

    await waitFor(() => expect(virtualizerOptions.at(-1)?.scrollMargin).toBe(240));
  });

  it('rescrolls the active printer when sorting moves it to another row', async () => {
    const printers = createPrinters(PRINTER_GRID_VIRTUALIZATION_THRESHOLD);
    const { rerender } = render(
      <PrinterCardGrid
        printers={printers}
        mode="compact"
        activePrinterId="printer-55"
        renderPrinter={renderCard}
      />,
    );

    await waitFor(() => expect(scrollToIndex).toHaveBeenCalledWith(18, { align: 'center' }));
    scrollToIndex.mockClear();
    const reorderedPrinters = [printers[55], ...printers.filter((printer) => printer.id !== 'printer-55')];
    rerender(
      <PrinterCardGrid
        printers={reorderedPrinters}
        mode="compact"
        activePrinterId="printer-55"
        renderPrinter={renderCard}
      />,
    );

    await waitFor(() => expect(scrollToIndex).toHaveBeenCalledWith(0, { align: 'center' }));
  });
});
