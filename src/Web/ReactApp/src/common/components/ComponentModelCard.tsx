import React from 'react';
import { Card, Button, Badge } from '@/common/components/ui';

/**
 * Common interface for component models to enable polymorphic rendering
 */
export interface ComponentModelBase {
  id: string;
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  description?: string;
  url?: string;
}

/**
 * Extended interface for hotend-specific properties
 */
export interface HotendModelCardData extends ComponentModelBase {
  type: 'hotend';
  maxTemp?: number;
  isHighFlow: boolean;
}

/**
 * Extended interface for extruder-specific properties
 */
export interface ExtruderModelCardData extends ComponentModelBase {
  type: 'extruder';
  gearRatio?: string;
  isDirectDrive: boolean;
}

/**
 * Extended interface for toolhead-specific properties
 */
export interface ToolheadModelCardData extends ComponentModelBase {
  type: 'toolhead';
  // Toolheads have no additional properties beyond the base
}

/**
 * Extended interface for nozzle-specific properties
 */
export interface NozzleModelCardData extends ComponentModelBase {
  type: 'nozzle';
  maxTemp?: number;
  isHardened: boolean;
}

/**
 * Union type of all component model card data types
 */
export type ComponentModelCardData =
  | HotendModelCardData
  | ExtruderModelCardData
  | ToolheadModelCardData
  | NozzleModelCardData;

/**
 * Props for the ComponentModelCard component
 * Generic T allows type-safe callbacks for specific model types
 */
export interface ComponentModelCardProps<T extends ComponentModelCardData = ComponentModelCardData> {
  /**
   * The component model data to display
   */
  model: T;

  /**
   * Callback when the edit button is clicked
   */
  onEdit?: (model: T) => void;

  /**
   * Callback when the clone button is clicked
   */
  onClone?: (model: T) => void;

  /**
   * Callback when the delete button is clicked
   */
  onDelete?: (model: T) => void;

  /**
   * Whether the card is in a loading state (e.g., during delete)
   */
  isLoading?: boolean;
}

/**
 * Renders component-type specific properties as badges/chips
 */
function TypeSpecificProperties({ model }: { model: ComponentModelCardData }) {
  switch (model.type) {
    case 'hotend':
      return (
        <div className="flex flex-wrap gap-2 mt-2">
          {model.maxTemp && (
            <Badge variant="default" size="sm">
              Max {model.maxTemp}°C
            </Badge>
          )}
          {model.isHighFlow && (
            <Badge variant="success" size="sm">
              High Flow
            </Badge>
          )}
        </div>
      );

    case 'extruder':
      return (
        <div className="flex flex-wrap gap-2 mt-2">
          {model.gearRatio && (
            <Badge variant="default" size="sm">
              Ratio: {model.gearRatio}
            </Badge>
          )}
          <Badge 
            variant={model.isDirectDrive ? 'success' : 'default'} 
            size="sm"
          >
            {model.isDirectDrive ? 'Direct Drive' : 'Bowden'}
          </Badge>
        </div>
      );

    case 'nozzle':
      return (
        <div className="flex flex-wrap gap-2 mt-2">
          {model.maxTemp && (
            <Badge variant="default" size="sm">
              Max {model.maxTemp}°C
            </Badge>
          )}
          {model.isHardened && (
            <Badge variant="warning" size="sm">
              Hardened
            </Badge>
          )}
        </div>
      );

    case 'toolhead':
      // Toolheads don't have additional properties to display
      return null;

    default:
      return null;
  }
}

/**
 * ComponentModelCard - A reusable card component for displaying hardware component models
 * 
 * Displays component model information in a consistent card format with:
 * - Model name and manufacturer
 * - Type-specific properties as badges (max temp, high flow, gear ratio, etc.)
 * - Optional description
 * - Optional URL link
 * - Edit and Delete action buttons
 * 
 * Used across Hotends, Extruders, Toolheads, and Nozzles catalog tabs.
 */
export function ComponentModelCard<T extends ComponentModelCardData>({
  model,
  onEdit,
  onClone,
  onDelete,
  isLoading = false,
}: ComponentModelCardProps<T>) {
  return (
    <Card className="h-full flex flex-col">
      <div className="p-4 flex-1">
        {/* Header: Name and Actions */}
        <div className="flex justify-between items-start">
          <div className="flex-1 min-w-0">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white truncate">
              {model.name}
            </h3>
            <p className="text-sm text-gray-500 dark:text-gray-400">
              {model.manufacturerName || 'Unknown Manufacturer'}
            </p>
          </div>

          {/* Action Buttons */}
          <div className="flex gap-1 ml-2 flex-shrink-0">
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
                title={`Clone ${model.name}`}
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
                className="text-red-600 hover:text-red-700 hover:bg-red-50 dark:text-red-400 dark:hover:text-red-300 dark:hover:bg-red-900/20"
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

        {/* Type-specific Properties */}
        <TypeSpecificProperties model={model} />

        {/* Description */}
        {model.description && (
          <p className="mt-3 text-sm text-gray-600 dark:text-gray-300 line-clamp-2">
            {model.description}
          </p>
        )}

        {/* URL Link */}
        {model.url && (
          <a
            href={model.url}
            target="_blank"
            rel="noopener noreferrer"
            className="mt-2 inline-flex items-center gap-1 text-sm text-blue-600 dark:text-blue-400 hover:underline"
          >
            <svg
              className="w-3 h-3"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
              />
            </svg>
            Product Page
          </a>
        )}
      </div>
    </Card>
  );
}

export default ComponentModelCard;
