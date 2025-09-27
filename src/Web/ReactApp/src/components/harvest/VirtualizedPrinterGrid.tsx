// Utility for virtualized grid/list of printer cards
// This will use react-window for performance with large fleets
// To be imported and used in HarvestPage

import React from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { PrinterCard, PrinterCardProps } from './PrinterCard';
import { HarvestOptions, GcodeHarvestOperation } from '@/types/api';
import styles from './VirtualizedPrinterGrid.module.css';
import { getRowTransformClass } from './rowTransformUtil';

interface VirtualizedPrinterGridProps {
  printers: PrinterCardProps['printer'][];
  operations: Record<string, PrinterCardProps['operation'] | undefined>;
  onStartHarvest: (printerId: string, options: HarvestOptions) => void;
  onCancelHarvest: (opId: string) => void;
  onSettings: (id: string) => void;
  onViewDetails: (op: GcodeHarvestOperation) => void;
  columnCount?: number;
  cardHeight?: number;
  cardWidth?: number;
  compact?: boolean;
}

export const VirtualizedPrinterGrid: React.FC<VirtualizedPrinterGridProps> = (props) => {
  const {
    printers,
    operations,
    onStartHarvest,
    onCancelHarvest,
    onSettings,
    onViewDetails,
    columnCount = 4,
    cardHeight = 240,
    cardWidth = 320,
    compact = false,
  } = props;

  const rowCount = Math.ceil(printers.length / columnCount);
  const parentRef = React.useRef<HTMLDivElement>(null);

  // Virtualizer for rows
  const rowVirtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement: () => parentRef.current,
    estimateSize: () => cardHeight,
    overscan: 2,
  });

  // Responsive: make grid width 100% on small screens, fixed on large
  const gridWidth = typeof window !== 'undefined' && window.innerWidth < 900
    ? window.innerWidth - 32
    : columnCount * cardWidth + 16;
  const gridHeight = Math.min(3, rowCount) * cardHeight + 16;

  return (
    <div
      ref={parentRef}
      className={styles.printerGridOuter}
      data-height={gridHeight}
      data-width={gridWidth}
    >
      <div
        className={styles.printerGridInner}
        data-height={rowVirtualizer.getTotalSize()}
        data-width="100%"
      >
        {rowVirtualizer.getVirtualItems().map(row => (
          <div
            key={row.key}
            className={styles.printerGridRow + ' ' + getRowTransformClass(row.start)}
          >
            {Array.from({ length: columnCount }).map((_, columnIndex) => {
              const idx = row.index * columnCount + columnIndex;
              if (idx >= printers.length) return null;
              const printer = printers[idx];
              const operation = operations[printer.id];
              return (
                <div
                  className={styles.printerGridCell + ' ' + styles.printerGridCellFixed}
                  key={printer.id}
                   data-width={cardWidth}
                   data-height={cardHeight}
                >
                  <PrinterCard
                    printer={printer}
                    operation={operation}
                    onStartHarvest={onStartHarvest}
                    onCancelHarvest={onCancelHarvest}
                    onSettings={onSettings}
                    onViewDetails={onViewDetails}
                    compact={compact}
                  />
                </div>
              );
            })}
          </div>
        ))}
      </div>
    </div>
  );
};
