/**
 * Slicer Viewer Components
 * Barrel export for slicer 3D visualization components
 */

export { SlicerToolbar, type SlicerToolbarProps } from './SlicerToolbar';
export { SlicerLeftTools, type SlicerLeftToolsProps, type ToolType } from './SlicerLeftTools';
export { SlicerStatusBar, type SlicerStatusBarProps } from './SlicerStatusBar';
export { SlicerBedVisualization, type SlicerBedVisualizationProps, type LoadedModel, type BedConfig } from './SlicerBedVisualization';
export { SlicerWorkspace, type SlicerWorkspaceProps } from './SlicerWorkspace';
export { TextTool, type TextToolConfig, type TextToolProps } from './TextTool';
export { PlateTabBar, type PlateTabBarProps } from './PlateTabBar';
export { ClearanceZoneOverlay, type ClearanceZoneOverlayProps } from './ClearanceZoneOverlay';
export { SequentialPrintPanel, type SequentialPrintPanelProps } from './SequentialPrintPanel';

// Re-export plate manager types and utilities
export type { BuildPlate, PlateManagerState } from '@/features/slicer/utils/plateManager';

// Re-export icons for external use
export * from './SlicerToolbarIcons';
