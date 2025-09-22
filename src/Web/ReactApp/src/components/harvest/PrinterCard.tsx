import React, { useState } from 'react';
import { Printer, GcodeHarvestOperation } from '@/types/api';

export interface PrinterCardProps {
  printer: Printer;
  operation?: GcodeHarvestOperation; // Current/active harvest operation for this printer, if any
  onStartHarvest?: (printerId: string, options: any) => void;
  onCancelHarvest?: (operationId: string) => void;
  onSettings?: (printerId: string) => void;
  onViewDetails?: (operation: GcodeHarvestOperation) => void;
  compact?: boolean;
  // Add more props as needed for real-time status, progress, etc.
}

export const PrinterCard: React.FC<PrinterCardProps> = ({
  printer,
  operation,
  onStartHarvest,
  onCancelHarvest,
  onSettings,
  onViewDetails,
  compact = false
}) => {

  // Status color and label
  const statusColor = printer.isOnline ? 'text-green-600' : 'text-gray-400';
  const statusLabel = printer.isOnline ? 'Online' : 'Offline';
  // Model/location string
  const modelLoc = [printer.manufacturerName, printer.modelName].filter(Boolean).join(' • ');

  // Progress (if operation is running)
  const isRunning = !!operation && operation.status === 'Running';
  const progress = isRunning && operation.filesFound > 0
    ? Math.round((operation.filesProcessed / operation.filesFound) * 100)
    : 0;

  // Per-card harvest options state
  const [options, setOptions] = useState({
    includeSubfolders: true,
    fileTypes: ['gcode', 'gco', 'g'],
    minFileSize: 1024,
    duplicateHandling: 'skip',
  });

  return (
    <div
      className={`pf-card pf-card-hover flex flex-col focus:outline-none focus:ring-2 focus:ring-pf-accent ${compact ? 'gap-1 p-2 min-w-[120px] max-w-[180px] text-xs' : 'gap-2 p-4 min-w-[260px] max-w-xs text-base'} shadow-md border border-pf-border rounded-lg bg-pf-bg-0`}
      tabIndex={0}
      role="group"
      aria-label={`Printer card: ${printer.name}`}
      onKeyDown={e => {
        if (e.key === 'Enter' || e.key === ' ') {
          if (operation && onViewDetails) onViewDetails(operation);
        }
      }}
    >
      <div className="flex items-center justify-between">
        <div className={`flex items-center ${compact ? 'gap-1' : 'gap-2'}`}> 
          <span className={`w-2 h-2 rounded-full ${printer.isOnline ? 'bg-green-500' : 'bg-gray-400'}`} aria-label={statusLabel} />
          <span className={compact ? 'font-semibold' : 'font-bold text-lg text-pf-text-0'}>{printer.name}</span>
        </div>
        <button
          className="text-pf-muted hover:text-pf-accent focus:outline-none"
          title="Printer settings"
          aria-label={`Open settings for ${printer.name}`}
          tabIndex={0}
          onClick={() => onSettings?.(printer.id)}
        >
          <svg width={compact ? 14 : 18} height={compact ? 14 : 18} fill="none" viewBox="0 0 24 24"><circle cx="12" cy="12" r="2" fill="currentColor"/><circle cx="19" cy="12" r="2" fill="currentColor"/><circle cx="5" cy="12" r="2" fill="currentColor"/></svg>
        </button>
      </div>
      <div className={`text-pf-muted ${compact ? 'text-[10px] mb-0' : 'text-xs mb-1'}`}>{modelLoc}</div>
      <div className={`flex items-center ${compact ? 'gap-1 mb-0' : 'gap-2 mb-1'}`}>
        <span className={`font-medium ${statusColor} ${compact ? 'text-[10px]' : 'text-xs'}`}>{statusLabel}</span>
        {printer.state && <span className={compact ? 'text-[10px] text-pf-muted' : 'text-xs text-pf-muted'}>• {printer.state}</span>}
      </div>
      {/* Harvest options UI (only when not running) */}
      {!isRunning && (
        <form className={compact ? 'mb-1' : 'mb-2 space-y-1'} onSubmit={e => { e.preventDefault(); onStartHarvest?.(printer.id, options); }}>
          <div className="flex flex-wrap gap-1 items-center">
            <label className="flex items-center gap-1 text-xs">
              <input
                type="checkbox"
                checked={options.includeSubfolders}
                onChange={e => setOptions(o => ({ ...o, includeSubfolders: e.target.checked }))}
                className="mr-1"
              />
              Subfolders
            </label>
            <span className="text-xs text-pf-muted">Types:</span>
            {['gcode', 'gco', 'g'].map(ext => (
              <label key={ext} className="flex items-center gap-0.5 text-xs">
                <input
                  type="checkbox"
                  checked={options.fileTypes.includes(ext)}
                  onChange={e => {
                    setOptions(o => e.target.checked
                      ? { ...o, fileTypes: [...o.fileTypes, ext] }
                      : { ...o, fileTypes: o.fileTypes.filter(t => t !== ext) }
                    );
                  }}
                  className="mr-0.5"
                />
                .{ext}
              </label>
            ))}
          </div>
          <div className="flex flex-wrap gap-1 items-center text-xs">
            <label>
              Min Size:
              <select
                value={options.minFileSize}
                onChange={e => setOptions(o => ({ ...o, minFileSize: parseInt(e.target.value) }))}
                className="ml-1 px-1 py-0.5 border rounded"
              >
                <option value={0}>No min</option>
                <option value={1024}>1 KB</option>
                <option value={10240}>10 KB</option>
                <option value={102400}>100 KB</option>
                <option value={1048576}>1 MB</option>
              </select>
            </label>
            <label>
              Duplicates:
              <select
                value={options.duplicateHandling}
                onChange={e => setOptions(o => ({ ...o, duplicateHandling: e.target.value }))}
                className="ml-1 px-1 py-0.5 border rounded"
              >
                <option value="skip">Skip</option>
                <option value="overwrite">Overwrite</option>
                <option value="rename">Rename</option>
              </select>
            </label>
          </div>
        </form>
      )}

      {isRunning && operation && (
        <div className={compact ? 'mb-1' : 'mb-2'}>
          <div className={`flex items-center justify-between ${compact ? 'text-[10px] mb-0' : 'text-xs mb-1'}`}>
            <span>Harvest: {operation.filesProcessed}/{operation.filesFound}</span>
            <span>{progress}%</span>
          </div>
          {(() => {
            // Snap progress to nearest 10% for className
            const progressValue = Math.min(Math.round(progress / 10) * 10, 100);
            const progressClass = `pf-progress-bar-${progressValue}`;
            return (
              <div
                className={`w-full bg-gray-200 rounded-full ${compact ? 'pf-progress-bar-compact' : 'pf-progress-bar'} ${progressClass}`}
              >
                <div
                  className={`bg-pf-accent rounded-full transition-all duration-300 ${compact ? 'pf-progress-inner-compact' : 'pf-progress-inner'}`}
                />
              </div>
            );
          })()}
        </div>
      )}
      <div className={`flex gap-1 ${compact ? 'mt-1' : 'gap-2 mt-auto'}`}>
        {!isRunning && (
          <button
            className={`pf-btn pf-btn-primary flex-1 ${compact ? 'text-xs py-1 px-2' : ''}`}
            onClick={() => onStartHarvest?.(printer.id, options)}
            disabled={!printer.isOnline}
            aria-label={`Start harvest on ${printer.name}`}
            tabIndex={0}
          >
            Start
          </button>
        )}
        {isRunning && operation && (
          <button
            className={`pf-btn pf-btn-danger flex-1 ${compact ? 'text-xs py-1 px-2' : ''}`}
            onClick={() => onCancelHarvest?.(operation.id)}
            aria-label={`Cancel harvest on ${printer.name}`}
            tabIndex={0}
          >
            Cancel
          </button>
        )}
        {operation && (
          <button
            className={`pf-btn pf-btn-secondary flex-1 ${compact ? 'text-xs py-1 px-2' : ''}`}
            onClick={() => onViewDetails?.(operation)}
            aria-label={`View details for ${printer.name}`}
            tabIndex={0}
          >
            Details
          </button>
        )}
      </div>
    </div>
  );
};
