import React from 'react';
import type { PrinterSpoolInfo } from '@/types/api';
import { SpoolIcon } from '@/common/components/icons/SpoolIcon';

export interface LoadedFilamentCardProps {
  spoolInfo?: PrinterSpoolInfo;
  className?: string;
}

function formatWeight(weight?: number): string | null {
  if (weight === undefined || weight === null || Number.isNaN(weight)) {
    return null;
  }

  if (weight >= 1000) {
    return `${(weight / 1000).toFixed(2)}kg`;
  }

  return `${Math.round(weight)}g`;
}

export function LoadedFilamentCard({ spoolInfo, className }: LoadedFilamentCardProps) {
  if (!spoolInfo?.hasActiveSpool) {
    return (
      <div className={`flex items-center gap-3 rounded-sm border border-pf-border bg-pf-bg-0/30 px-3 py-2 ${className ?? ''}`}>
        <span className="flex-1 text-xs text-pf-text-tertiary">No spool loaded</span>
        <SpoolIcon size={44} className="shrink-0 opacity-50" />
      </div>
    );
  }

  const title = spoolInfo.spoolName || spoolInfo.filamentName || 'Loaded Filament';
  const spoolNumber = spoolInfo.activeSpoolId != null ? `#${spoolInfo.activeSpoolId}` : null;
  const weight = formatWeight(spoolInfo.remainingWeightG);

  const footerParts: string[] = [];
  if (spoolInfo.material) {
    footerParts.push(spoolInfo.material.toUpperCase());
  }
  if (weight) {
    footerParts.push(weight);
  }

  return (
    <div className={`flex items-center gap-3 rounded-sm border border-pf-border bg-pf-bg-0/30 px-3 py-2 ${className ?? ''}`}>
      <div className="flex-1 min-w-0 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 items-baseline">
        {/* Row 1: Name (id) */}
        <span className="text-pf-text-primary text-base font-medium leading-tight truncate col-span-2">
          {title}{spoolNumber && <span className="text-[10px] text-pf-text-tertiary font-normal ml-1.5">({spoolNumber})</span>}
        </span>

        {/* Row 2: Vendor | Material + Weight */}
        <span className="text-[10px] text-pf-text-secondary truncate">
          {spoolInfo.vendor ?? '—'}
        </span>
        <span className="text-xs text-pf-text-secondary truncate">
          {footerParts.join(' | ') || '—'}
        </span>
      </div>

      <SpoolIcon
        fillColor={spoolInfo.colorHex}
        size={44}
        className="shrink-0"
      />
    </div>
  );
}
