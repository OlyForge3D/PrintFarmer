/* eslint-disable local/pf-no-raw-html-controls */
import React, { useState } from 'react';
import { Printer, GcodeHarvestOperation, HarvestOptions, GcodeHarvestStatus } from '@/types/api';
import { getHarvestErrorInfo } from '@/common/utils/harvestErrorHelper';
import { ErrorIcon } from './ErrorIcon';

export interface PrinterCardProps {
  printer: Printer;
  operation?: GcodeHarvestOperation; // Current/active harvest operation for this printer, if any
  onStartHarvest?: (printerId: string, options: HarvestOptions) => void;
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
  const isRunning = !!operation && operation.status === GcodeHarvestStatus.Running;
  const isFailed = !!operation && operation.status === GcodeHarvestStatus.Failed;
  const isCompleted = !!operation && operation.status === GcodeHarvestStatus.Completed;
  const isCancelled = !!operation && operation.status === GcodeHarvestStatus.Cancelled;
  const progress = isRunning && operation.filesFound > 0
    ? Math.round((operation.filesProcessed / operation.filesFound) * 100)
    : 0;

  // Per-card harvest options state
  const [options, setOptions] = useState<HarvestOptions>({
    includeSubfolders: true,
    fileTypes: ['gcode', 'gco', 'g'],
    minFileSize: 1024,
    duplicateHandling: 'skip',
  });

  return (
    <div
      className={`bg-pf-bg-1 border border-pf-border rounded-xl hover:shadow-lg transition-all duration-200 flex flex-col h-full w-full focus:outline-hidden focus:ring-2 focus:ring-pf-accent ${compact ? 'gap-1 p-3 text-xs' : 'gap-3 p-5 text-base'}`}
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
          <span className={compact ? 'font-semibold' : 'font-bold text-lg text-pf-text-primary'}>{printer.name}</span>
        </div>
        <button
          className="text-pf-text-secondary hover:text-pf-accent focus:outline-hidden"
          title="Printer settings"
          aria-label={`Open settings for ${printer.name}`}
          tabIndex={0}
          onClick={() => onSettings?.(printer.id)}
        >
          <svg width={compact ? 14 : 18} height={compact ? 14 : 18} fill="none" viewBox="0 0 24 24"><circle cx="12" cy="12" r="2" fill="currentColor"/><circle cx="19" cy="12" r="2" fill="currentColor"/><circle cx="5" cy="12" r="2" fill="currentColor"/></svg>
        </button>
      </div>
      <div className={`text-pf-text-secondary ${compact ? 'text-[10px] mb-0' : 'text-xs mb-1'}`}>{modelLoc}</div>
      <div className={`flex items-center ${compact ? 'gap-1 mb-0' : 'gap-2 mb-1'}`}>
        <span className={`font-medium ${statusColor} ${compact ? 'text-[10px]' : 'text-xs'}`}>{statusLabel}</span>
        {printer.state && <span className={compact ? 'text-[10px] text-pf-text-secondary' : 'text-xs text-pf-text-secondary'}>• {printer.state}</span>}
      </div>
      {/* Harvest options UI (only when not running) */}
      {!isRunning && (
        <form className={compact ? 'mb-1' : 'mb-2 space-y-2'} onSubmit={e => { e.preventDefault(); onStartHarvest?.(printer.id, options); }}>
          <div className="flex flex-wrap gap-2 items-center">
            <label className="flex items-center gap-1.5 text-xs text-pf-text-primary cursor-pointer">
              <input
                type="checkbox"
                checked={options.includeSubfolders}
                onChange={e => setOptions(o => ({ ...o, includeSubfolders: e.target.checked }))}
                className="w-4 h-4 rounded-sm border-pf-border bg-pf-panel text-pf-accent focus:ring-2 focus:ring-pf-accent cursor-pointer"
              />
              Subfolders
            </label>
          </div>
          <div className="flex flex-wrap gap-2 items-center">
            <span className="text-xs font-medium text-pf-text-secondary">File Types:</span>
            {['gcode', 'gco', 'g'].map(ext => (
              <label key={ext} className="flex items-center gap-1.5 text-xs text-pf-text-primary cursor-pointer">
                <input
                  type="checkbox"
                  checked={Array.isArray(options.fileTypes) && options.fileTypes.includes(ext)}
                  onChange={e => {
                    setOptions(o => e.target.checked
                      ? { ...o, fileTypes: [...(Array.isArray(o.fileTypes) ? o.fileTypes : []), ext] }
                      : { ...o, fileTypes: (Array.isArray(o.fileTypes) ? o.fileTypes.filter(t => t !== ext) : []) }
                    );
                  }}
                  className="w-4 h-4 rounded-sm border-pf-border bg-pf-panel text-pf-accent focus:ring-2 focus:ring-pf-accent cursor-pointer"
                />
                .{ext}
              </label>
            ))}
          </div>
          <div className="flex flex-wrap gap-2 items-center text-xs">
            <label className="flex items-center gap-1">
              <span className="text-pf-text-secondary font-medium">Min Size:</span>
              <select
                value={options.minFileSize}
                onChange={e => setOptions(o => ({ ...o, minFileSize: parseInt(e.target.value) }))}
                className="px-2 py-1 rounded-lg bg-pf-panel border border-pf-border focus:outline-hidden focus:ring-2 focus:ring-pf-accent text-pf-text-primary text-xs"
              >
                <option value={0}>No min</option>
                <option value={1024}>1 KB</option>
                <option value={10240}>10 KB</option>
                <option value={102400}>100 KB</option>
                <option value={1048576}>1 MB</option>
              </select>
            </label>
            <label className="flex items-center gap-1">
              <span className="text-pf-text-secondary font-medium">Duplicates:</span>
              <select
                value={options.duplicateHandling}
                onChange={e => setOptions(o => ({ ...o, duplicateHandling: e.target.value as 'skip' | 'overwrite' | 'rename' }))}
                className="px-2 py-1 rounded-lg bg-pf-panel border border-pf-border focus:outline-hidden focus:ring-2 focus:ring-pf-accent text-pf-text-primary text-xs"
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

      {/* Enhanced Error Display */}
      {operation && (isFailed || operation.error) && (() => {
        const errorInfo = getHarvestErrorInfo(operation);
        if (!errorInfo) return null;

        return (
          <div className={`bg-red-50 border border-red-300 rounded-lg p-2 ${compact ? 'mb-1' : 'mb-2'}`}>
            <div className="flex items-start gap-2">
              <ErrorIcon type={errorInfo.iconType} className={compact ? 'w-3 h-3 text-red-600 shrink-0' : 'w-4 h-4 text-red-600 shrink-0'} />
              <div className="flex-1 min-w-0">
                <p className={`font-semibold text-red-800 ${compact ? 'text-[10px]' : 'text-xs'}`}>
                  {errorInfo.title}
                  {errorInfo.canRetry && <span className="ml-1 text-green-700">🔄</span>}
                </p>
                <p className={`text-red-700 wrap-break-word ${compact ? 'text-[9px] mt-0.5' : 'text-xs mt-1'}`}>
                  {errorInfo.message}
                </p>
                {!compact && errorInfo.suggestion && (
                  <p className={`text-red-600 mt-1 text-[10px] italic`}>
                    💡 {errorInfo.suggestion}
                  </p>
                )}
              </div>
            </div>
          </div>
        );
      })()}

      {/* Display cancellation message */}
      {operation && isCancelled && (
        <div className={`bg-yellow-50 border border-yellow-300 rounded-lg p-2 ${compact ? 'mb-1' : 'mb-2'}`}>
          <div className="flex items-start gap-2">
            <svg className="w-4 h-4 text-yellow-600 shrink-0 mt-0.5" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
            </svg>
            <div className="flex-1 min-w-0">
              <p className={`font-semibold text-yellow-800 ${compact ? 'text-[10px]' : 'text-xs'}`}>Harvest Cancelled</p>
              <p className={`text-yellow-700 ${compact ? 'text-[9px] mt-0.5' : 'text-xs mt-1'}`}>
                The harvest operation was cancelled.
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Display completion message if harvest completed successfully */}
      {operation && isCompleted && !operation.error && (
        <div className={`bg-green-50 border border-green-300 rounded-lg p-2 ${compact ? 'mb-1' : 'mb-2'}`}>
          <div className="flex items-start gap-2">
            <svg className="w-4 h-4 text-green-600 shrink-0 mt-0.5" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
            </svg>
            <div className="flex-1 min-w-0">
              <p className={`font-semibold text-green-800 ${compact ? 'text-[10px]' : 'text-xs'}`}>Harvest Complete</p>
              <p className={`text-green-700 ${compact ? 'text-[9px] mt-0.5' : 'text-xs mt-1'}`}>
                Added: {operation.filesAdded} • Skipped: {operation.filesSkipped} • Errors: {operation.filesErrored}
              </p>
            </div>
          </div>
        </div>
      )}

      <div className={`flex gap-2 ${compact ? 'mt-1' : 'mt-auto'}`}>
        {!isRunning && (
          <button
            className={`flex-1 rounded-lg bg-pf-accent hover:bg-pf-accent-hover text-white transition-all duration-200 cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed hover:scale-105 hover:shadow-md active:scale-95 ${compact ? 'text-xs py-1.5 px-3' : 'text-sm py-2 px-4 font-medium'}`}
            onClick={() => onStartHarvest?.(printer.id, options)}
            disabled={!printer.isOnline}
            aria-label={`Start harvest on ${printer.name}`}
            tabIndex={0}
          >
            Start Harvest
          </button>
        )}
        {isRunning && operation && (
          <>
            <button
              className={`flex-1 rounded-lg bg-red-600 hover:bg-red-700 text-white transition-all duration-200 cursor-pointer hover:scale-105 hover:shadow-md active:scale-95 ${compact ? 'text-xs py-1.5 px-3' : 'text-sm py-2 px-4 font-medium'}`}
              onClick={() => onCancelHarvest?.(operation.id)}
              aria-label={`Cancel harvest on ${printer.name}`}
              tabIndex={0}
            >
              Cancel
            </button>
            <button
              className={`flex-1 rounded-lg bg-pf-text-tertiary hover:bg-pf-text-secondary text-white transition-all duration-200 cursor-pointer hover:scale-105 hover:shadow-md active:scale-95 ${compact ? 'text-xs py-1.5 px-3' : 'text-sm py-2 px-4 font-medium'}`}
              onClick={() => onViewDetails?.(operation)}
              aria-label={`View details for ${printer.name}`}
              tabIndex={0}
            >
              Details
            </button>
          </>
        )}
        {!isRunning && operation && (
          <button
            className={`flex-1 rounded-lg bg-pf-text-tertiary hover:bg-pf-text-secondary text-white transition-all duration-200 cursor-pointer hover:scale-105 hover:shadow-md active:scale-95 ${compact ? 'text-xs py-1.5 px-3' : 'text-sm py-2 px-4 font-medium'}`}
            onClick={() => onViewDetails?.(operation)}
            aria-label={`View details for ${printer.name}`}
            tabIndex={0}
          >
            View Details
          </button>
        )}
      </div>
    </div>
  );
};
