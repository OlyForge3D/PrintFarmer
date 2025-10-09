// Utility for virtualized grid/list of printer cards
// This will use react-window for performance with large fleets
// To be imported and used in HarvestPage

import React from 'react';
import { PrinterCard, PrinterCardProps } from './PrinterCard';
import { HarvestOptions, GcodeHarvestOperation } from '@/types/api';
import styles from './VirtualizedPrinterGrid.module.css';

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
    compact = false,
  } = props;

  const parentRef = React.useRef<HTMLDivElement>(null);

  return (
    <div
      ref={parentRef}
      className={styles.printerGridOuter}
    >
      <div
        className={`${styles.printerGridRow} ${compact ? styles.printerGridRowCompact : ''}`}
      >
        {printers.map((printer) => {
          const operation = operations[printer.id];
          return (
            <div
              className={`${styles.printerGridCell} ${compact ? styles.printerGridCellCompact : ''}`}
              key={printer.id}
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
    </div>
  );
};
