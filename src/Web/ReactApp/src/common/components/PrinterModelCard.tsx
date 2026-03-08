import React from 'react';
import { Card, Button, Badge } from '@/common/components/ui';
import type { PrinterModelDto, MotionTypeString, PrinterBackendString } from '@/types/api';

/**
 * Card data interface for PrinterModelCard
 * Extends PrinterModelDto with additional display fields
 */
export interface PrinterModelCardData extends PrinterModelDto {
  manufacturerName?: string;
}

/**
 * Props for the PrinterModelCard component
 */
export interface PrinterModelCardProps {
  /**
   * The printer model data to display
   */
  model: PrinterModelCardData;

  /**
   * Callback when the edit button is clicked
   */
  onEdit?: (model: PrinterModelCardData) => void;

  /**
   * Callback when the clone button is clicked
   */
  onClone?: (model: PrinterModelCardData) => void;

  /**
   * Callback when the delete button is clicked
   */
  onDelete?: (model: PrinterModelCardData) => void;

  /**
   * Whether the card is in a loading state (e.g., during delete)
   */
  isLoading?: boolean;
}

/**
 * Helper to format motion type for display
 */
function getMotionTypeDisplay(type?: MotionTypeString): string {
  switch (type) {
    case 'Cartesian':
      return 'Cartesian';
    case 'CoreXY':
      return 'CoreXY';
    case 'Delta':
      return 'Delta';
    default:
      return 'Unknown';
  }
}

/**
 * Helper to get motion type badge color
 */
function getMotionTypeBadgeVariant(type?: MotionTypeString): 'default' | 'success' | 'warning' | 'error' {
  switch (type) {
    case 'CoreXY':
      return 'success';
    case 'Delta':
      return 'warning';
    default:
      return 'default';
  }
}

/**
 * Helper to format backend type for display
 */
function getBackendDisplay(backend?: PrinterBackendString): string | null {
  switch (backend) {
    case 'Moonraker':
      return 'Moonraker';
    case 'PrusaLink':
      return 'PrusaLink';
    case 'OctoPrint':
      return 'OctoPrint';
    case 'SDCP':
      return 'SDCP';
    case 'FlashForge':
      return 'FlashForge';
    default:
      return null;
  }
}

/**
 * Format build volume dimensions
 */
function formatBuildVolume(x?: number, y?: number, z?: number): string | null {
  if (!x && !y && !z) return null;
  const xStr = x ?? '?';
  const yStr = y ?? '?';
  const zStr = z ?? '?';
  return `${xStr} × ${yStr} × ${zStr} mm`;
}

/**
 * PrinterModelCard - A card component for displaying printer model information
 * 
 * Shows rich printer model data including:
 * - Name and manufacturer
 * - Motion type (Cartesian, CoreXY, Delta)
 * - Build volume dimensions
 * - Capability badges (heated bed, enclosure, multi-material, auto-leveling)
 * - Number of extruders
 * - Max temperatures and print speed
 * - Default backend type
 * - Supported filament types
 * - Toolhead count
 * 
 * Edit, Clone, and Delete action buttons
 */
export function PrinterModelCard({
  model,
  onEdit,
  onClone,
  onDelete,
  isLoading = false,
}: PrinterModelCardProps) {
  const buildVolume = formatBuildVolume(model.maxX, model.maxY, model.maxZ);
  const backendDisplay = getBackendDisplay(model.defaultBackend);
  const toolheadCount = model.toolheads?.length ?? 0;

  return (
    <Card className="h-full flex flex-col">
      <div className="p-4 flex-1">
        {/* Header: Name and Actions */}
        <div className="flex justify-between items-start">
          <div className="flex-1 min-w-0">
            <h3 className="text-lg font-semibold text-pf-text-primary dark:text-white truncate">
              {model.name}
            </h3>
            <p className="text-sm text-pf-text-secondary">
              {model.manufacturerName || 'Unknown Manufacturer'}
            </p>
          </div>

          {/* Action Buttons */}
          <div className="flex gap-1 ml-2 shrink-0">
            {onEdit && (
              <Button
                variant="subtle"
                size="sm"
                onClick={() => onEdit(model)}
                disabled={isLoading}
                aria-label={`Edit ${model.name}`}
              >
                <svg
                  className="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
                  />
                </svg>
              </Button>
            )}
            {onClone && (
              <Button
                variant="subtle"
                size="sm"
                onClick={() => onClone(model)}
                disabled={isLoading}
                aria-label={`Clone ${model.name}`}
                className="text-pf-success hover:text-pf-success hover:bg-pf-success/10"
              >
                <svg
                  className="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
                  />
                </svg>
              </Button>
            )}
            {onDelete && (
              <Button
                variant="subtle"
                size="sm"
                onClick={() => onDelete(model)}
                disabled={isLoading}
                aria-label={`Delete ${model.name}`}
                className="text-pf-error hover:text-pf-error hover:bg-pf-error/10 dark:hover:text-pf-error"
              >
                <svg
                  className="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                  />
                </svg>
              </Button>
            )}
          </div>
        </div>

        {/* Primary Info Row: Motion Type & Build Volume */}
        <div className="flex flex-wrap gap-2 mt-3">
          {model.motionType && (
            <Badge variant={getMotionTypeBadgeVariant(model.motionType)} size="sm">
              {getMotionTypeDisplay(model.motionType)}
            </Badge>
          )}
          {backendDisplay && (
            <Badge variant="default" size="sm">
              {backendDisplay}
            </Badge>
          )}
        </div>

        {/* Build Volume */}
        {buildVolume && (
          <div className="mt-2 text-sm text-pf-text-secondary">
            <span className="font-medium">Build:</span> {buildVolume}
          </div>
        )}

        {/* Capability Badges */}
        <div className="flex flex-wrap gap-1.5 mt-3">
          {model.hasHeatedBed && (
            <Badge variant="success" size="sm">
              🔥 Heated Bed
            </Badge>
          )}
          {model.hasEnclosure && (
            <Badge variant="success" size="sm">
              📦 Enclosure
            </Badge>
          )}
          {model.multiMaterial && (
            <Badge variant="warning" size="sm">
              🎨 Multi-Material
            </Badge>
          )}
          {model.supportsAutoLeveling && (
            <Badge variant="success" size="sm">
              📐 Auto-Level
            </Badge>
          )}
          {toolheadCount > 1 && (
            <Badge variant="warning" size="sm">
              {toolheadCount} Toolheads
            </Badge>
          )}
        </div>

        {/* Secondary Info: Temperatures & Speed */}
        <div className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-pf-text-secondary">
          {model.maxBedTemp && (
            <div>
              <span className="font-medium">Max Bed:</span> {model.maxBedTemp}°C
            </div>
          )}
          {model.maxPrintSpeed && (
            <div>
              <span className="font-medium">Max Speed:</span> {model.maxPrintSpeed} mm/s
            </div>
          )}
          {toolheadCount > 0 && (
            <div>
              <span className="font-medium">Toolheads:</span> {toolheadCount}
            </div>
          )}
        </div>

        {/* Supported Filament Types */}
        {model.supportedFilamentTypes && model.supportedFilamentTypes.length > 0 && (
          <div className="mt-3">
            <p className="text-xs font-medium text-pf-text-secondary mb-1">
              Supported Materials:
            </p>
            <div className="flex flex-wrap gap-1">
              {model.supportedFilamentTypes.slice(0, 5).map((type) => (
                <Badge key={type} variant="default" size="sm" className="text-xs">
                  {type}
                </Badge>
              ))}
              {model.supportedFilamentTypes.length > 5 && (
                <Badge variant="default" size="sm" className="text-xs">
                  +{model.supportedFilamentTypes.length - 5} more
                </Badge>
              )}
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

export default PrinterModelCard;
