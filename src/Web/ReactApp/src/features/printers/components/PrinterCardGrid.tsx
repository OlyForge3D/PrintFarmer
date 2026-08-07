import {
  Fragment,
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { defaultRangeExtractor, useVirtualizer, type Range } from '@tanstack/react-virtual';
import type { Printer } from '@/types/api';

export const PRINTER_GRID_VIRTUALIZATION_THRESHOLD = 60;

const GRID_GAP_PX = 16;
const COMPACT_CARD_WIDTH_PX = 288;
const DETAILED_CARD_WIDTH_PX = 416;
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

function cardWidth(mode: PrinterGridMode): number {
  return mode === 'compact' ? COMPACT_CARD_WIDTH_PX : DETAILED_CARD_WIDTH_PX;
}

function estimatedRowHeight(mode: PrinterGridMode): number {
  return mode === 'compact'
    ? COMPACT_ESTIMATED_ROW_HEIGHT_PX
    : DETAILED_ESTIMATED_ROW_HEIGHT_PX;
}

function getColumnCount(width: number, mode: PrinterGridMode): number {
  return Math.max(1, Math.floor((width + GRID_GAP_PX) / (cardWidth(mode) + GRID_GAP_PX)));
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
  const lastScrollTargetRef = useRef<string | undefined>(undefined);
  const [scrollElement, setScrollElement] = useState<HTMLElement | null>(null);
  const [columnCount, setColumnCount] = useState(1);
  const [scrollMargin, setScrollMargin] = useState(0);
  const [focusedPrinterId, setFocusedPrinterId] = useState<string>();
  const [measuredCardHeights, setMeasuredCardHeights] = useState<ReadonlyMap<string, number>>(
    () => new Map(),
  );

  useLayoutEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let animationFrame: number | undefined;
    const nextScrollElement = container.closest<HTMLElement>('[data-main-content]');
    setScrollElement(nextScrollElement);
    const measure = () => {
      setColumnCount(getColumnCount(container.clientWidth, mode));
      if (!nextScrollElement) return;
      const containerRect = container.getBoundingClientRect();
      const scrollRect = nextScrollElement.getBoundingClientRect();
      setScrollMargin(containerRect.top - scrollRect.top + nextScrollElement.scrollTop);
    };
    const scheduleMeasure = () => {
      if (animationFrame !== undefined) cancelAnimationFrame(animationFrame);
      animationFrame = requestAnimationFrame(() => {
        animationFrame = undefined;
        measure();
      });
    };

    measure();
    const resizeObserver = new ResizeObserver(scheduleMeasure);
    let ancestor: HTMLElement | null = container;
    while (ancestor) {
      resizeObserver.observe(ancestor);
      ancestor = ancestor.parentElement;
    }

    const layoutRoot = nextScrollElement ?? container.parentElement;
    const mutationObserver = new MutationObserver(scheduleMeasure);
    if (layoutRoot) {
      mutationObserver.observe(layoutRoot, {
        attributes: true,
        attributeFilter: ['class', 'hidden', 'style'],
        childList: true,
        subtree: true,
      });
      layoutRoot.addEventListener('transitionrun', scheduleMeasure, true);
      layoutRoot.addEventListener('transitionend', scheduleMeasure, true);
    }
    window.addEventListener('resize', scheduleMeasure);

    return () => {
      if (animationFrame !== undefined) cancelAnimationFrame(animationFrame);
      resizeObserver.disconnect();
      mutationObserver.disconnect();
      layoutRoot?.removeEventListener('transitionrun', scheduleMeasure, true);
      layoutRoot?.removeEventListener('transitionend', scheduleMeasure, true);
      window.removeEventListener('resize', scheduleMeasure);
    };
  }, [mode]);

  const measureCard = useCallback((element: HTMLDivElement | null) => {
    if (!element) return;

    const printerId = element.dataset.printerId;
    if (!printerId) return;

    const publishHeight = () => {
      const height = element.getBoundingClientRect().height;
      if (height <= 0) return;
      setMeasuredCardHeights((current) => {
        if (current.get(printerId) === height) return current;
        const next = new Map(current);
        next.set(printerId, height);
        return next;
      });
    };

    publishHeight();
    const observer = new ResizeObserver(publishHeight);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const rowCount = Math.ceil(printers.length / columnCount);
  // TanStack Virtual intentionally exposes mutable measurement methods.
  // eslint-disable-next-line react-hooks/incompatible-library
  const rowVirtualizer = useVirtualizer({
    useFlushSync: false,
    getScrollElement: () => scrollElement,
    count: rowCount,
    estimateSize: () => estimatedRowHeight(mode),
    overscan: ROW_OVERSCAN,
    scrollMargin,
    rangeExtractor: (range: Range) => {
      const indexes = new Set(defaultRangeExtractor(range));
      // Keep the canonical tour target stable instead of moving it to the
      // first overscan row as the operator scrolls.
      indexes.add(0);

      const focusedPrinterIndex = focusedPrinterId
        ? printers.findIndex((printer) => printer.id === focusedPrinterId)
        : -1;
      if (focusedPrinterIndex >= 0) {
        indexes.add(Math.floor(focusedPrinterIndex / columnCount));
      }
      return [...indexes].sort((left, right) => left - right);
    },
  });

  useEffect(() => {
    if (!activePrinterId || !scrollElement) return;

    const printerIndex = printers.findIndex((printer) => printer.id === activePrinterId);
    if (printerIndex < 0) return;

    const rowIndex = Math.floor(printerIndex / columnCount);
    const targetKey = `${activePrinterId}:${printerIndex}:${columnCount}:${rowIndex}`;
    if (lastScrollTargetRef.current === targetKey) return;
    lastScrollTargetRef.current = targetKey;
    rowVirtualizer.scrollToIndex(rowIndex, { align: 'center' });
  }, [activePrinterId, columnCount, printers, rowVirtualizer, scrollElement]);

  const virtualRows = rowVirtualizer.getVirtualItems();
  const renderedItems: ReactNode[] = [];
  const fixedCardWidth = cardWidth(mode);

  virtualRows.forEach((virtualRow) => {
    const startIndex = virtualRow.index * columnCount;
    const rowPrinters = printers.slice(startIndex, startIndex + columnCount);
    const rowHeights = rowPrinters.map((printer) => measuredCardHeights.get(printer.id));
    const allCardsMeasured = rowHeights.every((height) => height !== undefined);
    const measuredRowHeight = allCardsMeasured
      ? Math.max(...rowHeights as number[]) + GRID_GAP_PX
      : estimatedRowHeight(mode);
    const rowOffset = virtualRow.start - scrollMargin;

    renderedItems.push(
      <div
        key={`measure-row-${virtualRow.index}`}
        ref={rowVirtualizer.measureElement}
        data-index={virtualRow.index}
        aria-hidden="true"
        className="pointer-events-none absolute left-0 top-0 w-px invisible"
        style={{ height: measuredRowHeight, transform: `translateY(${rowOffset}px)` }}
      />,
    );

    rowPrinters.forEach((printer, columnIndex) => {
      renderedItems.push(
        <div
          key={`printer-${printer.id}`}
          ref={measureCard}
          role="listitem"
          aria-setsize={printers.length}
          aria-posinset={startIndex + columnIndex + 1}
          data-printer-id={printer.id}
          className="absolute left-0 top-0 min-w-0"
          style={{
            width: columnCount === 1 ? '100%' : fixedCardWidth,
            transform: `translate(${columnIndex * (fixedCardWidth + GRID_GAP_PX)}px, ${rowOffset}px)`,
          }}
          {...(virtualRow.index === 0 && columnIndex === 0
            ? { 'data-tour': 'printers-card' }
            : {})}
        >
          {renderPrinter(printer)}
        </div>,
      );
    });
  });

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
        if (card?.dataset.printerId) setFocusedPrinterId(card.dataset.printerId);
      }}
      onBlurCapture={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setFocusedPrinterId(undefined);
        }
      }}
    >
      {renderedItems}
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
