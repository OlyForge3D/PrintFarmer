/**
 * Printer Bed Card Component
 * Displays printer bed visualization with status information
 * Designed for dashboard integration with responsive layout
 */

import React, { useState } from 'react';
import { PrinterBedVisualization, PrinterStatus } from './PrinterBedVisualization';
import { PrinterModelDto } from '@/types/api';

export interface PrinterBedCardProps {
  printerModel: PrinterModelDto;
  status: PrinterStatus;
  width?: 'full' | 'half' | 'third'; // Responsive width
  showControls?: boolean;
  onRefresh?: () => void;
}

/**
 * Temperature Gauge Component
 * Visual representation of temperature with target indicator
 */
const TemperatureGauge: React.FC<{
  label: string;
  current: number;
  target: number;
  max?: number;
}> = ({ label, current, target, max = 300 }) => {
  const percentage = Math.min((current / max) * 100, 100);

  return (
    <div className="space-y-1">
      <div className="flex justify-between text-xs text-pf-text-secondary">
        <span>{label}</span>
        <span className="text-pf-text-primary font-mono">
          {Math.round(current)}°C
          {target > 0 && <span className="text-pf-text-secondary">→{target}°C</span>}
        </span>
      </div>
      <div className="h-2 rounded-full overflow-hidden bg-pf-border-dark">
        <div
          className="h-full transition-all duration-300"
          style={{
            background: 'linear-gradient(to right, var(--pf-accent-2), var(--pf-error))',
            width: `${percentage}%`
          }}
        />
      </div>
    </div>
  );
};

/**
 * Status Badge Component
 * Shows printer state with visual indicator
 */
const StatusBadge: React.FC<{
  state: PrinterStatus['state'];
}> = ({ state }) => {
  const statusClasses: Record<PrinterStatus['state'], string> = {
    Idle: 'bg-pf-border-medium',
    Printing: 'bg-pf-success-bg',
    Paused: 'bg-pf-warning-bg',
    Error: 'bg-pf-error-bg',
    Offline: 'bg-pf-border-dark',
  };

  const statusTextClasses: Record<PrinterStatus['state'], string> = {
    Idle: 'text-pf-text-secondary',
    Printing: 'text-white',
    Paused: 'text-white',
    Error: 'text-white',
    Offline: 'text-pf-text-secondary',
  };

  const statusDotClasses: Record<PrinterStatus['state'], string> = {
    Idle: 'bg-pf-border opacity-60',
    Printing: 'bg-pf-success',
    Paused: 'bg-pf-warning',
    Error: 'bg-pf-error',
    Offline: 'bg-pf-border-medium opacity-50',
  };

  return (
    <div className={`inline-flex items-center gap-2 px-3 py-1 rounded-full ${statusClasses[state]}`}>
      <div className={`w-2 h-2 rounded-full animate-pulse ${statusDotClasses[state]}`} />
      <span className={`text-sm font-semibold ${statusTextClasses[state]}`}>{state}</span>
    </div>
  );
};

/**
 * Progress Bar Component
 * Shows print job progress
 */
const ProgressBar: React.FC<{
  progress?: number;
  jobName?: string;
}> = ({ progress, jobName }) => {
  if (!jobName || progress === undefined || progress === 0) {
    return null;
  }

  const percentage = Math.min(Math.max(progress, 0), 100);

  return (
    <div className="space-y-2">
      <div className="flex justify-between text-xs">
        <span className="text-gray-400">Job Progress</span>
        <span className="text-white font-mono">{Math.round(percentage)}%</span>
      </div>
      <div className="h-2 bg-gray-700 rounded-full overflow-hidden">
        <div
          className="h-full bg-gradient-to-r from-purple-500 to-pink-500 transition-all duration-300"
          style={{ width: `${percentage}%` }}
        />
      </div>
      {jobName && <p className="text-xs text-gray-400 truncate">{jobName}</p>}
    </div>
  );
};

/**
 * PrinterBedCard Component
 * Main dashboard card component combining visualization with status info
 */
export const PrinterBedCard: React.FC<PrinterBedCardProps> = ({
  printerModel,
  status,
  width = 'full',
  showControls = true,
  onRefresh,
}) => {
  const [autoRotate, setAutoRotate] = useState(false);

  // Determine responsive width
  const widthClasses = {
    full: 'w-full',
    half: 'w-1/2',
    third: 'w-1/3',
  };

  // Responsive height
  const isMobile = typeof window !== 'undefined' && window.innerWidth < 768;
  const visualizationHeight = isMobile ? 300 : 400;

  return (
    <div className={`${widthClasses[width]} bg-gray-800/50 rounded-lg border border-gray-700 overflow-hidden`}>
      {/* Header */}
      <div className="bg-gray-900/80 border-b border-gray-700 px-4 py-3 flex justify-between items-start">
        <div className="flex-1">
          <h3 className="text-lg font-semibold text-white">{status.name}</h3>
          <p className="text-xs text-gray-400">{printerModel.name}</p>
        </div>
        <StatusBadge state={status.state} />
      </div>

      {/* Main Content - Visualization + Status Panel */}
      <div className="flex flex-col lg:flex-row">
        {/* 3D Visualization */}
        <div className="flex-1 bg-gray-950">
          <PrinterBedVisualization
            printerModel={printerModel}
            status={status}
            height={visualizationHeight}
            autoRotate={autoRotate}
            showAxes={false}
            showGrid={true}
          />
        </div>

        {/* Status Information Panel */}
        <div className="w-full lg:w-64 bg-gray-900/50 border-t lg:border-t-0 lg:border-l border-gray-700 p-4 space-y-4">
          {/* Controls */}
          {showControls && (
            <div className="space-y-2">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={autoRotate}
                  onChange={(e) => setAutoRotate(e.target.checked)}
                  className="w-4 h-4 rounded bg-gray-700 border-gray-600 cursor-pointer"
                />
                <span className="text-sm text-gray-300">Auto-rotate</span>
              </label>

              {onRefresh && (
                <button
                  onClick={onRefresh}
                  className="w-full px-3 py-2 text-sm bg-gray-700 hover:bg-gray-600 text-gray-300 rounded-lg transition-colors"
                >
                  Refresh Status
                </button>
              )}
            </div>
          )}

          {/* Temperature Data */}
          {status.temperatures && (
            <div className="space-y-3 pt-2 border-t border-gray-700">
              <p className="text-xs font-semibold text-gray-400 uppercase">Temperatures</p>
              <TemperatureGauge
                label="Hotend"
                current={status.temperatures.hotend}
                target={status.temperatures.hotendTarget}
                max={300}
              />
              <TemperatureGauge
                label="Bed"
                current={status.temperatures.bed}
                target={status.temperatures.bedTarget}
                max={150}
              />
            </div>
          )}

          {/* Position Data */}
          {status.nozzlePosition && (
            <div className="space-y-2 pt-2 border-t border-gray-700">
              <p className="text-xs font-semibold text-gray-400 uppercase">Position</p>
              <div className="grid grid-cols-3 gap-2 text-center">
                <div>
                  <p className="text-xs text-gray-500">X</p>
                  <p className="text-sm font-mono text-white">{status.nozzlePosition.x.toFixed(1)}mm</p>
                </div>
                <div>
                  <p className="text-xs text-gray-500">Y</p>
                  <p className="text-sm font-mono text-white">{status.nozzlePosition.y.toFixed(1)}mm</p>
                </div>
                <div>
                  <p className="text-xs text-gray-500">Z</p>
                  <p className="text-sm font-mono text-white">{status.nozzlePosition.z.toFixed(1)}mm</p>
                </div>
              </div>
            </div>
          )}

          {/* Progress */}
          {status.progress !== undefined && (
            <div className="pt-2 border-t border-gray-700">
              <ProgressBar progress={status.progress} jobName={status.jobName} />
            </div>
          )}

          {/* Error Message */}
          {status.state === 'Error' && status.state === 'Error' && (
            <div className="bg-red-900/30 border border-red-700 rounded p-2">
              <p className="text-xs text-red-200">Error: Check printer status</p>
            </div>
          )}
        </div>
      </div>

      {/* Footer - Controls Info */}
      {showControls && (
        <div className="bg-gray-900/50 border-t border-gray-700 px-4 py-2 text-xs text-gray-500">
          <p>Use mouse: Left-drag to rotate • Right-drag to pan • Scroll to zoom</p>
        </div>
      )}
    </div>
  );
};

export default PrinterBedCard;
