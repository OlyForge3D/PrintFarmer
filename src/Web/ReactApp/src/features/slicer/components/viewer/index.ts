/**
 * Slicer Viewer Components
 * Barrel export for slicer 3D visualization components
 */

export { SlicerToolbar, type SlicerToolbarProps } from './SlicerToolbar';
export { SlicerLeftTools, type SlicerLeftToolsProps, type ToolType } from './SlicerLeftTools';
export { SlicerStatusBar, type SlicerStatusBarProps } from './SlicerStatusBar';
export { SlicerBedVisualization, type SlicerBedVisualizationProps, type LoadedModel, type BedConfig } from './SlicerBedVisualization';
export { SlicerWorkspace, type SlicerWorkspaceProps } from './SlicerWorkspace';

// Re-export icons for external use
export * from './SlicerToolbarIcons';
