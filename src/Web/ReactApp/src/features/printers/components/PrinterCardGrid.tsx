import { Fragment, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { defaultRangeExtractor, useWindowVirtualizer, type Range } from '@tanstack/react-virtual';
import type { Printer } from '@/types/api';

export const PRINTER_GRID_VIRTUALIZATION_THRESHOLD = 60;

const GRID_GAP_PX = 16;
const COMPACT_CARD_WIDTH_PX = 288;
const DETAILED_CARD_WIDTH_PX = 416;
const SINGLE_COLUMN_BREAKPOINT_PX = 640;
const ROW_OVERSCAN = 2;
const COMPACT_ESTIMATED_ROW_HEIGHT_PX = 360;
const DETAILED_ESTIMATED_ROW_HEIGHT_PX = 720;

type PrinterGridMode = 'compact' | 'detailed';

interface PrinterCardGridProps {
  printers: Printer[];
  mode: PrinterGridMode;
  activePrinterId?: string | null;
  renderPrinter: (printer: Printer) => ReactNode;
}

function getColumnCount(width: number, mode: PrinterGridMode): number {
  if (width < SINGLE_COLUMN_BREAKPOINT_PX) return 1;

  const cardWidth = mode === 'compact' ? COMPACT_CARD_WIDTH_PX : DETAILED_CARD_WIDTH_PX;
  return Math.max(1, Math.floor((width + GRID_GAP_PX) / (cardWidth + GRID_GAP_PX)));
}

function gridClassName(mode: PrinterGridMode): string {
  return mode === 'compact'
    ? 'grid grid-cols-1 sm:grid-cols-[repeat(auto-fill,18rem)] gap-4 transition-opacity duration-200 min-w-0'
    : 'grid grid-cols-1 sm:grid-cols-[repeat(auto-fill,26rem)] gap-4';
}

function SmallPrinterCardGrid({ printers, mode, renderPrinter }: PrinterCardGridProps) {
  return (
    <div className={gridClassName(mode)}>
      {printers.map((printer, index) => mode === 'compact' ? (
        <div key={printer.id} {...(index === 0 ? { 'data-tour': 'printers-card' } : {})}>
          {renderPrinter(printer)}
        </div>
      ) : (
        <Fragment key={printer.id}>{renderPrinter(printer)}</Fragment>
      ))}
    </div>
  );
}

function VirtualizedPrinterCardGrid({
  printers,
  mode,
  activePrinterId,
  renderPrinter,
}: PrinterCardGridProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const lastScrollTargetRef = useRef<string>();
  const [columnCount, setColumnCount] = useState(1);
  const [scrollMargin, setScrollMargin] = useState(0);
  const [focusedPrinterId, setFocusedPrinterId] = useState<string>();

  useLayoutEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const measure = () => {
      setColumnCount(getColumnCount(container.clientWidth, mode));
      setScrollMargin(container.getBoundingClientRect().top + window.scrollY);
    };

    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(container);
    return () => observer.disconnect();
  }, [mode]);

  const rowCount = Math.ceil(printers.length / columnCount);
  const rowVirtualizer = useWindowVirtualizer({
    count: rowCount,
    estimateSize: () => mode === 'compact'
      ? COMPACT_ESTIMATED_ROW_HEIGHT_PX
      : DETAILED_ESTIMATED_ROW_HEIGHT_PX,
    overscan: ROW_OVERSCAN,
    scrollMargin,
    rangeExtractor: (range: Range) => {
      const indexes = defaultRangeExtractor(range);
      const focusedPrinterIndex = focusedPrinterId
        ? printers.findIndex((printer) => printer.id === focusedPrinterId)
        : -1;
      if (focusedPrinterIndex < 0) return indexes;

      const focusedRowIndex = Math.floor(focusedPrinterIndex / columnCount);
      if (indexes.includes(focusedRowIndex)) return indexes;
      return [...indexes, focusedRowIndex].sort((left, right) => left - right);
    },
  });

  useEffect(() => {
    if (!activePrinterId) return;

    const printerIndex = printers.findIndex((printer) => printer.id === activePrinterId);
    if (printerIndex < 0) return;

    const targetKey = `${activePrinterId}:${columnCount}`;
    if (lastScrollTargetRef.current === targetKey) return;
    lastScrollTargetRef.current = targetKey;
    rowVirtualizer.scrollToIndex(Math.floor(printerIndex / columnCount), { align: 'center' });
  }, [activePrinterId, columnCount, printers, rowVirtualizer]);

  const virtualRows = rowVirtualizer.getVirtualItems();

  return (
    <div
      ref={containerRef}
      className="relative min-w-0 w-full"
      style={{ height: rowVirtualizer.getTotalSize() }}
      role="list"
      aria-label="Printers"
      data-testid="virtualized-printer-grid"
      onFocusCapture={(event) => {
        const card = (event.target as HTMLElement).closest<HTMLElement>('[data-printer-id]');
        if (!card?.dataset.printerId) return;
        setFocusedPrinterId(card.dataset.printerId);
      }}
      onBlurCapture={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setFocusedPrinterId(undefined);
        }
      }}
    >
      {virtualRows.map((virtualRow, virtualRowIndex) => {
        const startIndex = virtualRow.index * columnCount;
        const rowPrinters = printers.slice(startIndex, startIndex + columnCount);

        return (
          <div
            key={virtualRow.key}
            ref={rowVirtualizer.measureElement}
            data-index={virtualRow.index}
            role="presentation"
            className={`${gridClassName(mode)} absolute left-0 top-0 w-full pb-4`}
            style={{ transform: `translateY(${virtualRow.start - scrollMargin}px)` }}
          >
            {rowPrinters.map((printer, columnIndex) => (
              <div
                key={printer.id}
                role="listitem"
                aria-setsize={printers.length}
                aria-posinset={startIndex + columnIndex + 1}
                data-printer-id={printer.id}
                {...(virtualRowIndex === 0 && columnIndex === 0
                  ? { 'data-tour': 'printers-card' }
                  : {})}
              >
                {renderPrinter(printer)}
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}

/**
 * Keeps the exact small-fleet grid while window-virtualizing responsive rows
 * once rendering every card would become expensive.
 */
export function PrinterCardGrid(props: PrinterCardGridProps) {
  if (props.printers.length < PRINTER_GRID_VIRTUALIZATION_THRESHOLD) {
    return <SmallPrinterCardGrid {...props} />;
  }

  return <VirtualizedPrinterCardGrid {...props} />;
}
