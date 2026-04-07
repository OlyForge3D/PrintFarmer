/**
 * Slicer Workspace Component
 * Main container combining toolbar, 3D bed visualization, left tools, and status bar
 * Matches OrcaSlicer's interface layout
 */
import React, { useState, useCallback, useEffect } from 'react';
import { SlicerToolbar } from './SlicerToolbar';
import { SlicerLeftTools, type ToolType } from './SlicerLeftTools';
import { SlicerStatusBar } from './SlicerStatusBar';
import { SlicerBedVisualization, type LoadedModel, type BedConfig } from './SlicerBedVisualization';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Checkbox, Input, Select } from '@/common/components/ui';

export interface SlicerWorkspaceProps {
  /** Bed configuration including dimensions */
  bedConfig: BedConfig;
  /** List of loaded models */
  models?: LoadedModel[];
  /** Currently selected model ID */
  selectedModelId?: string;
  /** Callback when a model is selected */
  onModelSelect?: (modelId: string | null) => void;
  /** Callback when a model is moved/rotated/scaled */
  onModelTransform?: (modelId: string, position: [number, number, number], rotation: [number, number, number], scale: [number, number, number]) => void;
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
  onModelTransform,
  onAddModel,
  onSettingsProfiles,
  onSlice,
  slicing = false,
  canSlice = true,
  slicesRemaining,
  slicesTotal,
  className = '',
}) => {
  const [activeTool, setActiveTool] = useState<ToolType | null>(null);
  const [showLayers, setShowLayers] = useState(false);
  const [undoStack, setUndoStack] = useState<unknown[]>([]);
  const [redoStack, setRedoStack] = useState<unknown[]>([]);
  const [isScaleDialogOpen, setIsScaleDialogOpen] = useState(false);
  const [isMoveDialogOpen, setIsMoveDialogOpen] = useState(false);
  const [isRotateDialogOpen, setIsRotateDialogOpen] = useState(false);
  const [scaleMode, setScaleMode] = useState<'percent' | 'mm'>('percent');
  const [uniformScale, setUniformScale] = useState(true);
  const [scalePercentInput, setScalePercentInput] = useState<[number, number, number]>([100, 100, 100]);
  const [scaleMmInput, setScaleMmInput] = useState<[number, number, number]>([0, 0, 0]);
  const [movePositionInput, setMovePositionInput] = useState<[number, number, number]>([0, 0, 0]);
  const [rotateRelativeInput, setRotateRelativeInput] = useState<[number, number, number]>([0, 0, 0]);
  const [rotateAbsoluteInput, setRotateAbsoluteInput] = useState<[number, number, number]>([0, 0, 0]);
  const [selectedModelMetrics, setSelectedModelMetrics] = useState<{
    modelId: string;
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  } | null>(null);

  const hasModels = models.length > 0;
  const hasSelection = selectedModelId != null && models.some(m => m.id === selectedModelId);
  const radToDeg = (radians: number) => radians * (180 / Math.PI);
  const degToRad = (degrees: number) => degrees * (Math.PI / 180);

  useEffect(() => {
    // Keep tool state neutral when selection changes; user must explicitly choose a tool.
    queueMicrotask(() => setActiveTool(null));
  }, [selectedModelId]);

  const formatValue = (value: number) => Number.isFinite(value) ? value.toFixed(2) : '0.00';

  const applyUniformTriple = useCallback((triple: [number, number, number], value: number): [number, number, number] => {
    if (!uniformScale) return triple;
    return [value, value, value];
  }, [uniformScale]);

  // Toolbar action handlers (placeholders for now)
  const handleArrange = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Auto-arrange models'); }
  }, []);

  const handleOrient = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Orient model'); }
  }, []);

  const handleLayFlat = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Lay model flat'); }
  }, []);

  const handleSplit = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Split model'); }
  }, []);

  const handleCut = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Cut model'); }
  }, []);

  const handleMeasure = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Measure tool'); }
  }, []);

  const handleSupportPaint = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Support paint mode'); }
  }, []);

  const handleSeamPaint = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Seam paint mode'); }
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
    if (window.PrintFarmerDebug?.slicer) { console.log('Assembly view'); }
  }, []);

  const handleKeyboardShortcuts = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Show keyboard shortcuts'); }
  }, []);

  const handleToolChange = useCallback((tool: ToolType) => {
    if (!hasSelection && tool !== 'layers') return;

    if (tool === 'move') {
      const selectedModel = selectedModelId ? models.find((m) => m.id === selectedModelId) : undefined;
      if (selectedModel) {
        setMovePositionInput(selectedModel.position);
        setIsMoveDialogOpen(true);
      }
      setActiveTool(null);
      return;
    }

    if (tool === 'scale') {
      if (selectedModelMetrics) {
        setScalePercentInput([100, 100, 100]);
        setScaleMmInput(selectedModelMetrics.currentSize);
      }
      setScaleMode('percent');
      setUniformScale(true);
      setIsScaleDialogOpen(true);
      setActiveTool(null);
      return;
    }

    if (tool === 'rotate') {
      const selectedModel = selectedModelId ? models.find((m) => m.id === selectedModelId) : undefined;
      if (selectedModel) {
        setRotateRelativeInput([0, 0, 0]);
        setRotateAbsoluteInput([
          radToDeg(selectedModel.rotation[0]),
          radToDeg(selectedModel.rotation[1]),
          radToDeg(selectedModel.rotation[2]),
        ]);
        setIsRotateDialogOpen(true);
      }
      setActiveTool(null);
      return;
    }

    setActiveTool(tool);
  }, [hasSelection, selectedModelId, models, selectedModelMetrics]);

  const handleLayersToggle = useCallback(() => {
    setShowLayers(prev => !prev);
  }, []);

  const handleScaleApply = useCallback(() => {
    if (!selectedModelMetrics || !onModelTransform) {
      setIsScaleDialogOpen(false);
      return;
    }

    const model = models.find((m) => m.id === selectedModelMetrics.modelId);
    if (!model) {
      setIsScaleDialogOpen(false);
      return;
    }

    let newScale: [number, number, number];

    if (scaleMode === 'percent') {
      const source = uniformScale
        ? [scalePercentInput[0], scalePercentInput[0], scalePercentInput[0]] as [number, number, number]
        : scalePercentInput;

      newScale = [
        selectedModelMetrics.currentScale[0] * (source[0] / 100),
        selectedModelMetrics.currentScale[1] * (source[1] / 100),
        selectedModelMetrics.currentScale[2] * (source[2] / 100),
      ];
    } else {
      const source = uniformScale
        ? [scaleMmInput[0], scaleMmInput[0], scaleMmInput[0]] as [number, number, number]
        : scaleMmInput;

      if (uniformScale) {
        const currentX = selectedModelMetrics.currentSize[0] || 1;
        const ratio = source[0] / currentX;
        newScale = [
          selectedModelMetrics.currentScale[0] * ratio,
          selectedModelMetrics.currentScale[1] * ratio,
          selectedModelMetrics.currentScale[2] * ratio,
        ];
      } else {
        newScale = [
          source[0] / (selectedModelMetrics.baseSize[0] || 1),
          source[1] / (selectedModelMetrics.baseSize[1] || 1),
          source[2] / (selectedModelMetrics.baseSize[2] || 1),
        ];
      }
    }

    onModelTransform(model.id, model.position, model.rotation, newScale);
    setIsScaleDialogOpen(false);
  }, [models, onModelTransform, scaleMmInput, scaleMode, scalePercentInput, selectedModelMetrics, uniformScale]);

  const handleMoveApply = useCallback(() => {
    if (!selectedModelId || !onModelTransform) {
      setIsMoveDialogOpen(false);
      return;
    }

    const model = models.find((m) => m.id === selectedModelId);
    if (!model) {
      setIsMoveDialogOpen(false);
      return;
    }

    onModelTransform(model.id, movePositionInput, model.rotation, model.scale);
    setIsMoveDialogOpen(false);
  }, [selectedModelId, onModelTransform, models, movePositionInput]);

  const handleRotateApply = useCallback(() => {
    if (!selectedModelId || !onModelTransform) {
      setIsRotateDialogOpen(false);
      return;
    }

    const model = models.find((m) => m.id === selectedModelId);
    if (!model) {
      setIsRotateDialogOpen(false);
      return;
    }

    const nextRotation: [number, number, number] = [
      degToRad(rotateAbsoluteInput[0] + rotateRelativeInput[0]),
      degToRad(rotateAbsoluteInput[1] + rotateRelativeInput[1]),
      degToRad(rotateAbsoluteInput[2] + rotateRelativeInput[2]),
    ];

    onModelTransform(model.id, model.position, nextRotation, model.scale);
    setIsRotateDialogOpen(false);
  }, [selectedModelId, onModelTransform, models, rotateAbsoluteInput, rotateRelativeInput]);

  // Map left-tool type to Three.js TransformControls mode
  const transformMode = activeTool === 'move'
    ? 'translate'
    : activeTool === 'rotate'
      ? 'rotate'
      : activeTool === 'scale'
        ? 'scale'
        : null;

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
        hasSelection={hasSelection}
      />

      {/* Main content area with 3D bed and left tools */}
      <div className="flex-1 relative overflow-hidden">
        {/* 3D Bed Visualization */}
        <SlicerBedVisualization
          bedConfig={bedConfig}
          models={models}
          selectedModelId={selectedModelId}
          onModelSelect={onModelSelect}
          transformMode={transformMode}
          onModelTransform={onModelTransform}
          onSelectedModelMetricsChange={setSelectedModelMetrics}
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
          hasSelection={hasSelection}
        />
      </div>

      <Modal
        isOpen={isMoveDialogOpen}
        onClose={() => setIsMoveDialogOpen(false)}
        title="Move Model"
        size="md"
        footer={(
          <>
            <Button variant="secondary" onClick={() => setIsMoveDialogOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleMoveApply}>Apply</Button>
          </>
        )}
      >
        <div className="space-y-4">
          <Select
            label="Coordinates"
            value="world"
            onChange={() => {}}
            options={[{ label: 'World coordinates', value: 'world' }]}
          />
          <div className="grid grid-cols-3 gap-3">
            <Input
              label="X"
              type="number"
              step="0.01"
              value={String(movePositionInput[0])}
              onChange={(e) => {
                const value = Number(e.target.value || 0);
                setMovePositionInput((prev) => [value, prev[1], prev[2]]);
              }}
            />
            <Input
              label="Y"
              type="number"
              step="0.01"
              value={String(movePositionInput[1])}
              onChange={(e) => {
                const value = Number(e.target.value || 0);
                setMovePositionInput((prev) => [prev[0], value, prev[2]]);
              }}
            />
            <Input
              label="Z"
              type="number"
              step="0.01"
              value={String(movePositionInput[2])}
              onChange={(e) => {
                const value = Number(e.target.value || 0);
                setMovePositionInput((prev) => [prev[0], prev[1], value]);
              }}
            />
          </div>
          <div className="text-sm text-pf-text-muted border-t border-pf-border pt-3">Values are in millimeters (mm).</div>
        </div>
      </Modal>

      <Modal
        isOpen={isRotateDialogOpen}
        onClose={() => setIsRotateDialogOpen(false)}
        title="Rotate [R]"
        size="md"
        footer={(
          <>
            <Button variant="secondary" onClick={() => setIsRotateDialogOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleRotateApply}>Apply</Button>
          </>
        )}
      >
        <div className="space-y-4">
          <Select
            label="Coordinates"
            value="world"
            onChange={() => {}}
            options={[{ label: 'World coordinates', value: 'world' }]}
          />

          <div className="space-y-3">
            <div>
              <div className="text-sm text-pf-text-muted mb-2">Rotate (relative)</div>
              <div className="grid grid-cols-3 gap-3">
                <Input
                  label="X"
                  type="number"
                  step="0.01"
                  value={String(rotateRelativeInput[0])}
                  onChange={(e) => {
                    const value = Number(e.target.value || 0);
                    setRotateRelativeInput((prev) => [value, prev[1], prev[2]]);
                  }}
                />
                <Input
                  label="Y"
                  type="number"
                  step="0.01"
                  value={String(rotateRelativeInput[1])}
                  onChange={(e) => {
                    const value = Number(e.target.value || 0);
                    setRotateRelativeInput((prev) => [prev[0], value, prev[2]]);
                  }}
                />
                <Input
                  label="Z"
                  type="number"
                  step="0.01"
                  value={String(rotateRelativeInput[2])}
                  onChange={(e) => {
                    const value = Number(e.target.value || 0);
                    setRotateRelativeInput((prev) => [prev[0], prev[1], value]);
                  }}
                />
              </div>
            </div>

            <div>
              <div className="flex items-center justify-between mb-2">
                <div className="text-sm text-pf-text-muted">Rotate (absolute)</div>
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={() => {
                    setRotateRelativeInput([0, 0, 0]);
                    setRotateAbsoluteInput([0, 0, 0]);
                  }}
                >
                  Reset
                </Button>
              </div>
              <div className="grid grid-cols-3 gap-3">
                <Input
                  label="X"
                  type="number"
                  step="0.01"
                  value={String(rotateAbsoluteInput[0])}
                  onChange={(e) => {
                    const value = Number(e.target.value || 0);
                    setRotateAbsoluteInput((prev) => [value, prev[1], prev[2]]);
                  }}
                />
                <Input
                  label="Y"
                  type="number"
                  step="0.01"
                  value={String(rotateAbsoluteInput[1])}
                  onChange={(e) => {
                    const value = Number(e.target.value || 0);
                    setRotateAbsoluteInput((prev) => [prev[0], value, prev[2]]);
                  }}
                />
                <Input
                  label="Z"
                  type="number"
                  step="0.01"
                  value={String(rotateAbsoluteInput[2])}
                  onChange={(e) => {
                    const value = Number(e.target.value || 0);
                    setRotateAbsoluteInput((prev) => [prev[0], prev[1], value]);
                  }}
                />
              </div>
            </div>
          </div>

          <div className="text-sm text-pf-text-muted border-t border-pf-border pt-3">Angles are in degrees (°).</div>
        </div>
      </Modal>

      <Modal
        isOpen={isScaleDialogOpen}
        onClose={() => setIsScaleDialogOpen(false)}
        title="Scale Model"
        size="md"
        footer={(
          <>
            <Button variant="secondary" onClick={() => setIsScaleDialogOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleScaleApply}>Apply</Button>
          </>
        )}
      >
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <Select
              label="Scale mode"
              value={scaleMode}
              onChange={(value) => setScaleMode((value === 'mm' ? 'mm' : 'percent'))}
              options={[
                { label: 'Percent of current (%)', value: 'percent' },
                { label: 'Absolute size (mm)', value: 'mm' },
              ]}
            />
            <Checkbox
              label="Uniform scale"
              checked={uniformScale}
              onChange={(e) => setUniformScale(e.target.checked)}
            />
          </div>

          <div className="grid grid-cols-3 gap-3">
            <Input
              label="X"
              type="number"
              step="0.01"
              value={scaleMode === 'percent' ? String(scalePercentInput[0]) : String(scaleMmInput[0])}
              onChange={(e) => {
                const value = Number(e.target.value || 0);
                if (scaleMode === 'percent') {
                  setScalePercentInput((prev) => applyUniformTriple([
                    value,
                    prev[1],
                    prev[2],
                  ] as [number, number, number], value));
                } else {
                  setScaleMmInput((prev) => applyUniformTriple([
                    value,
                    prev[1],
                    prev[2],
                  ] as [number, number, number], value));
                }
              }}
            />
            <Input
              label="Y"
              type="number"
              step="0.01"
              disabled={uniformScale}
              value={scaleMode === 'percent' ? String(scalePercentInput[1]) : String(scaleMmInput[1])}
              onChange={(e) => {
                const value = Number(e.target.value || 0);
                if (scaleMode === 'percent') {
                  setScalePercentInput((prev) => [prev[0], value, prev[2]]);
                } else {
                  setScaleMmInput((prev) => [prev[0], value, prev[2]]);
                }
              }}
            />
            <Input
              label="Z"
              type="number"
              step="0.01"
              disabled={uniformScale}
              value={scaleMode === 'percent' ? String(scalePercentInput[2]) : String(scaleMmInput[2])}
              onChange={(e) => {
                const value = Number(e.target.value || 0);
                if (scaleMode === 'percent') {
                  setScalePercentInput((prev) => [prev[0], prev[1], value]);
                } else {
                  setScaleMmInput((prev) => [prev[0], prev[1], value]);
                }
              }}
            />
          </div>

          {selectedModelMetrics && (
            <div className="text-sm text-pf-text-muted border-t border-pf-border pt-3">
              Current size: X {formatValue(selectedModelMetrics.currentSize[0])} mm, Y {formatValue(selectedModelMetrics.currentSize[1])} mm, Z {formatValue(selectedModelMetrics.currentSize[2])} mm
            </div>
          )}
        </div>
      </Modal>

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
