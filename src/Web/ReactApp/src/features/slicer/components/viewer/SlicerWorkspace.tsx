/**
 * Slicer Workspace Component
 * Main container combining toolbar, 3D bed visualization, left tools, and status bar
 * Matches OrcaSlicer's interface layout
 */
import React, { useState, useCallback } from 'react';
import { SlicerToolbar } from './SlicerToolbar';
import { SlicerLeftTools, type ToolType } from './SlicerLeftTools';
import { SlicerStatusBar } from './SlicerStatusBar';
import { SlicerBedVisualization, type LoadedModel, type BedConfig } from './SlicerBedVisualization';

export interface SlicerWorkspaceProps {
  /** Bed configuration including dimensions */
  bedConfig: BedConfig;
  /** List of loaded models */
  models?: LoadedModel[];
  /** Currently selected model ID */
  selectedModelId?: string;
  /** Callback when a model is selected */
  onModelSelect?: (modelId: string | null) => void;
  /** Callback when Add Model is clicked */
  onAddModel?: () => void;
  /** Callback when Settings & Profiles is clicked */
  onSettingsProfiles?: () => void;
  /** Callback when Slice is clicked */
  onSlice?: () => void;
  /** Whether slicing is in progress */
  slicing?: boolean;
  /** Whether the user can slice (has models, valid settings) */
  canSlice?: boolean;
  /** Current number of remaining slices (optional) */
  slicesRemaining?: number;
  /** Total number of slices allowed (optional) */
  slicesTotal?: number;
  /** Additional CSS class */
  className?: string;
}

export const SlicerWorkspace: React.FC<SlicerWorkspaceProps> = ({
  bedConfig,
  models = [],
  selectedModelId,
  onModelSelect,
  onAddModel,
  onSettingsProfiles,
  onSlice,
  slicing = false,
  canSlice = true,
  slicesRemaining,
  slicesTotal,
  className = '',
}) => {
  const [activeTool, setActiveTool] = useState<ToolType>('move');
  const [showLayers, setShowLayers] = useState(false);
  const [undoStack, setUndoStack] = useState<unknown[]>([]);
  const [redoStack, setRedoStack] = useState<unknown[]>([]);

  const hasModels = models.length > 0;

  // Toolbar action handlers (placeholders for now)
  const handleArrange = useCallback(() => {
    console.log('Auto-arrange models');
  }, []);

  const handleOrient = useCallback(() => {
    console.log('Orient model');
  }, []);

  const handleLayFlat = useCallback(() => {
    console.log('Lay model flat');
  }, []);

  const handleSplit = useCallback(() => {
    console.log('Split model');
  }, []);

  const handleCut = useCallback(() => {
    console.log('Cut model');
  }, []);

  const handleMeasure = useCallback(() => {
    console.log('Measure tool');
  }, []);

  const handleSupportPaint = useCallback(() => {
    console.log('Support paint mode');
  }, []);

  const handleSeamPaint = useCallback(() => {
    console.log('Seam paint mode');
  }, []);

  const handleUndo = useCallback(() => {
    if (undoStack.length > 0) {
      const lastAction = undoStack[undoStack.length - 1];
      setUndoStack(prev => prev.slice(0, -1));
      setRedoStack(prev => [...prev, lastAction]);
    }
  }, [undoStack]);

  const handleRedo = useCallback(() => {
    if (redoStack.length > 0) {
      const lastRedo = redoStack[redoStack.length - 1];
      setRedoStack(prev => prev.slice(0, -1));
      setUndoStack(prev => [...prev, lastRedo]);
    }
  }, [redoStack]);

  const handleAssemblyView = useCallback(() => {
    console.log('Assembly view');
  }, []);

  const handleKeyboardShortcuts = useCallback(() => {
    console.log('Show keyboard shortcuts');
  }, []);

  const handleToolChange = useCallback((tool: ToolType) => {
    setActiveTool(tool);
  }, []);

  const handleLayersToggle = useCallback(() => {
    setShowLayers(prev => !prev);
  }, []);

  return (
    <div className={`flex flex-col h-full bg-pf-bg-0 ${className}`}>
      {/* Top Toolbar */}
      <SlicerToolbar
        onAddModel={onAddModel}
        onArrange={handleArrange}
        onOrient={handleOrient}
        onLayFlat={handleLayFlat}
        onSplit={handleSplit}
        onCut={handleCut}
        onMeasure={handleMeasure}
        onSupportPaint={handleSupportPaint}
        onSeamPaint={handleSeamPaint}
        onUndo={handleUndo}
        onRedo={handleRedo}
        onAssemblyView={handleAssemblyView}
        onSettingsProfiles={onSettingsProfiles}
        onKeyboardShortcuts={handleKeyboardShortcuts}
        canUndo={undoStack.length > 0}
        canRedo={redoStack.length > 0}
        hasModels={hasModels}
      />

      {/* Main content area with 3D bed and left tools */}
      <div className="flex-1 relative overflow-hidden">
        {/* 3D Bed Visualization */}
        <SlicerBedVisualization
          bedConfig={bedConfig}
          models={models}
          selectedModelId={selectedModelId}
          onModelSelect={onModelSelect}
          showGrid={true}
          showAxes={true}
          className="w-full h-full"
        />

        {/* Left manipulation tools */}
        <SlicerLeftTools
          activeTool={activeTool}
          onToolChange={handleToolChange}
          onLayersToggle={handleLayersToggle}
          showLayers={showLayers}
        />
      </div>

      {/* Bottom Status Bar */}
      <SlicerStatusBar
        objectCount={models.length}
        bedWidth={bedConfig.width}
        bedDepth={bedConfig.depth}
        bedHeight={bedConfig.height}
        slicesRemaining={slicesRemaining}
        slicesTotal={slicesTotal}
        onSlice={onSlice}
        slicing={slicing}
        canSlice={canSlice && hasModels}
      />
    </div>
  );
};

export default SlicerWorkspace;
