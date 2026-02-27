import type { ReactNode } from 'react';
import type { SpoolmanFilament } from '@/types/api';

/** Matches backend SpoolmanController (SpoolmanSpoolDto) serialized with camelCase.
 * TODO: Consolidate with SpoolmanSpool in @/types/api to avoid type drift. */
export interface SpoolmanSpoolDto {
  id: number;
  name: string;
  material: string;
  remainingWeightG?: number | null;
  colorHex?: string | null;
  inUse: boolean;
  filamentName?: string | null;
  vendor?: string | null;
  registeredAt?: string | null;
  firstUsedAt?: string | null;
  lastUsedAt?: string | null;
  initialWeightG?: number | null;
  usedWeightG?: number | null;
  spoolWeightG?: number | null;
  remainingLengthMm?: number | null;
  usedLengthMm?: number | null;
  location?: string | null;
  lotNumber?: string | null;
  archived?: boolean | null;
  usedPercent?: number | null;
  remainingPercent?: number | null;
  price?: number | null;
  comment?: string | null;
}

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
