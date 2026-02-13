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
      <div className={`flex items-center gap-3 rounded-sm border border-pf-border bg-pf-bg-0/30 p-3 ${className ?? ''}`}>
        <span className="flex-1 text-xs text-pf-text-tertiary">No spool loaded</span>
        <SpoolIcon size={56} className="shrink-0 opacity-50" />
      </div>
    );
  }

  const topMeta: string[] = [];
  if (spoolInfo.activeSpoolId !== undefined && spoolInfo.activeSpoolId !== null) {
    topMeta.push(`#${spoolInfo.activeSpoolId}`);
  }
  if (spoolInfo.vendor) {
    topMeta.push(spoolInfo.vendor.toUpperCase());
  }

  const title = spoolInfo.spoolName || spoolInfo.filamentName || 'Loaded Filament';
  const weight = formatWeight(spoolInfo.remainingWeightG);

  const footerParts: string[] = [];
  if (spoolInfo.material) {
    footerParts.push(spoolInfo.material.toUpperCase());
  }
  if (weight) {
    footerParts.push(weight);
  }

  return (
    <div className={`flex items-center gap-3 rounded-sm border border-pf-border bg-pf-bg-0/30 p-3 ${className ?? ''}`}>
      <div className="flex-1 min-w-0">
        {topMeta.length > 0 && (
          <div className="text-[10px] uppercase tracking-[0.18em] text-pf-text-secondary truncate">
            {topMeta.join(' | ')}
          </div>
        )}

        <div className="text-pf-text-primary text-base font-medium leading-tight truncate mt-1">
          {title}
        </div>

        {footerParts.length > 0 && (
          <div className="text-pf-text-secondary text-xs truncate mt-1">
            {footerParts.join(' | ')}
          </div>
        )}
      </div>

      <SpoolIcon
        fillColor={spoolInfo.colorHex}
        size={64}
        className="shrink-0"
      />
    </div>
  );
}
