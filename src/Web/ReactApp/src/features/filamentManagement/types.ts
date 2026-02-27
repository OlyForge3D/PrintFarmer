import type { ReactNode } from 'react';
import type { SpoolmanFilament, SpoolmanSpool } from '@/types/api';

/** Alias for SpoolmanSpool from the canonical type definitions. */
export type SpoolmanSpoolDto = SpoolmanSpool;

export interface SpoolTableColumn {
  id: string;
  label: string;
  visible: boolean;
  sortable?: boolean;
  render: (spool: SpoolmanSpoolDto) => ReactNode;
  sortValue?: (spool: SpoolmanSpoolDto) => string | number;
}

export interface FilamentTableColumn {
  id: string;
  label: string;
  visible: boolean;
  sortable?: boolean;
  render: (filament: SpoolmanFilament) => ReactNode;
  sortValue?: (filament: SpoolmanFilament) => string | number;
}
