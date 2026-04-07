/**
 * Slicer Workspace Component
 * Main container combining toolbar, 3D bed visualization, left tools, and status bar
 * Matches OrcaSlicer's interface layout
 */
import React, { useState, useCallback, useEffect, useRef } from 'react';
import { SlicerToolbar } from './SlicerToolbar';
import { SlicerLeftTools, type ToolType } from './SlicerLeftTools';
import { SlicerStatusBar } from './SlicerStatusBar';
import { SlicerBedVisualization, type LoadedModel, type BedConfig } from './SlicerBedVisualization';
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
  onModelTransform?: (
    modelId: string,
    position: [number, number, number],
    rotation: [number, number, number],
    scale: [number, number, number],
    options?: {
      recordHistory?: boolean;
      actionLabel?: string;
      historyBefore?: TransformSnapshot;
    },
  ) => void;
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

type TransformSnapshot = {
  position: [number, number, number];
  rotation: [number, number, number];
  scale: [number, number, number];
};

type TransformDelta = {
  modelId: string;
  before: TransformSnapshot;
  after: TransformSnapshot;
};

type TransformHistoryEntry = {
  action: string;
  deltas: TransformDelta[];
};

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
  const [undoStack, setUndoStack] = useState<TransformHistoryEntry[]>([]);
  const [redoStack, setRedoStack] = useState<TransformHistoryEntry[]>([]);
  const isApplyingHistoryRef = useRef(false);
  const [scaleMode, setScaleMode] = useState<'percent' | 'mm'>('percent');
  const [uniformScale, setUniformScale] = useState(true);
  const [scalePercentInput, setScalePercentInput] = useState<[number, number, number]>([100, 100, 100]);
  const [scaleMmInput, setScaleMmInput] = useState<[number, number, number]>([0, 0, 0]);
  const [movePositionInput, setMovePositionInput] = useState<[number, number, number]>([0, 0, 0]);
  const [rotateBaseAbsoluteInput, setRotateBaseAbsoluteInput] = useState<[number, number, number]>([0, 0, 0]);
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

  const setRotateAbsoluteAxis = useCallback((axis: 0 | 1 | 2, value: number) => {
    setRotateAbsoluteInput((prevAbsolute) => {
      const nextAbsolute: [number, number, number] = [...prevAbsolute] as [number, number, number];
      nextAbsolute[axis] = value;

      setRotateRelativeInput((prevRelative) => {
        const nextRelative: [number, number, number] = [...prevRelative] as [number, number, number];
        nextRelative[axis] = nextAbsolute[axis] - rotateBaseAbsoluteInput[axis];
        return nextRelative;
      });

      return nextAbsolute;
    });
  }, [rotateBaseAbsoluteInput]);

  const setRotateRelativeAxis = useCallback((axis: 0 | 1 | 2, value: number) => {
    setRotateRelativeInput((prevRelative) => {
      const nextRelative: [number, number, number] = [...prevRelative] as [number, number, number];
      nextRelative[axis] = value;

      setRotateAbsoluteInput((prevAbsolute) => {
        const nextAbsolute: [number, number, number] = [...prevAbsolute] as [number, number, number];
        nextAbsolute[axis] = rotateBaseAbsoluteInput[axis] + value;
        return nextAbsolute;
      });

      return nextRelative;
    });
  }, [rotateBaseAbsoluteInput]);

  useEffect(() => {
    // Keep tool state neutral when selection changes; user must explicitly choose a tool.
    queueMicrotask(() => setActiveTool(null));
  }, [selectedModelId]);

  const formatValue = (value: number) => Number.isFinite(value) ? value.toFixed(2) : '0.00';

  const applyUniformTriple = useCallback((triple: [number, number, number], value: number): [number, number, number] => {
    if (!uniformScale) return triple;
    return [value, value, value];
  }, [uniformScale]);

  const triplesEqual = useCallback((a: [number, number, number], b: [number, number, number]) => (
    a[0] === b[0] && a[1] === b[1] && a[2] === b[2]
  ), []);

  const pushHistoryEntry = useCallback((entry: TransformHistoryEntry) => {
    setUndoStack((prev) => [...prev, entry]);
    setRedoStack([]);
  }, []);

  const getSelectedModel = useCallback(() => {
    if (!selectedModelId) return undefined;
    return models.find((m) => m.id === selectedModelId);
  }, [models, selectedModelId]);

  const handleModelTransform = useCallback(
    (
      modelId: string,
      position: [number, number, number],
      rotation: [number, number, number],
      scale: [number, number, number],
      options?: { recordHistory?: boolean; actionLabel?: string; historyBefore?: TransformSnapshot },
    ) => {
      if (!onModelTransform) {
        return;
      }

      const recordHistory = options?.recordHistory ?? true;
      const actionLabel = options?.actionLabel ?? 'Transform';
      const historyBefore = options?.historyBefore;
      const currentModel = models.find((model) => model.id === modelId);

      let nextScale: [number, number, number] = scale;
      if (currentModel && activeTool === 'scale' && uniformScale && selectedModelId === modelId) {
        const deltas: [number, number, number] = [
          Math.abs(scale[0] - currentModel.scale[0]),
          Math.abs(scale[1] - currentModel.scale[1]),
          Math.abs(scale[2] - currentModel.scale[2]),
        ];
        const dominantAxis = deltas[1] > deltas[0] ? (deltas[2] > deltas[1] ? 2 : 1) : (deltas[2] > deltas[0] ? 2 : 0);
        const master = scale[dominantAxis];
        nextScale = [master, master, master];
      }

      if (currentModel) {
        const noChange =
          triplesEqual(currentModel.position, position) &&
          triplesEqual(currentModel.rotation, rotation) &&
          triplesEqual(currentModel.scale, nextScale);

        const shouldCreateHistoryFromSnapshot =
          recordHistory &&
          !isApplyingHistoryRef.current &&
          historyBefore != null &&
          (!triplesEqual(historyBefore.position, position) ||
            !triplesEqual(historyBefore.rotation, rotation) ||
            !triplesEqual(historyBefore.scale, nextScale));

        if (noChange && !shouldCreateHistoryFromSnapshot) {
          return;
        }

        if (recordHistory && !isApplyingHistoryRef.current) {
          const before = historyBefore ?? {
            position: currentModel.position,
            rotation: currentModel.rotation,
            scale: currentModel.scale,
          };

          pushHistoryEntry({
            action: actionLabel,
            deltas: [{
              modelId,
              before,
              after: {
                position,
                rotation,
                scale: nextScale,
              },
            }],
          });
        }
      }

      onModelTransform(modelId, position, rotation, nextScale, options);

      if (selectedModelId !== modelId || !activeTool) {
        return;
      }

      if (activeTool === 'move') {
        setMovePositionInput(position);
        return;
      }

      if (activeTool === 'rotate') {
        const absolute: [number, number, number] = [
          radToDeg(rotation[0]),
          radToDeg(rotation[1]),
          radToDeg(rotation[2]),
        ];
        setRotateBaseAbsoluteInput(absolute);
        setRotateRelativeInput([0, 0, 0]);
        setRotateAbsoluteInput(absolute);
        return;
      }

      if (activeTool === 'scale') {
        setScalePercentInput([100, 100, 100]);

        if (selectedModelMetrics && selectedModelMetrics.modelId === modelId) {
          setScaleMmInput([
            selectedModelMetrics.baseSize[0] * nextScale[0],
            selectedModelMetrics.baseSize[1] * nextScale[1],
            selectedModelMetrics.baseSize[2] * nextScale[2],
          ]);
        }
      }
    },
    [activeTool, models, onModelTransform, pushHistoryEntry, selectedModelId, selectedModelMetrics, triplesEqual, uniformScale],
  );

  const applyHistoryEntry = useCallback((entry: TransformHistoryEntry, direction: 'before' | 'after') => {
    entry.deltas.forEach((delta) => {
      const target = delta[direction];
      handleModelTransform(
        delta.modelId,
        target.position,
        target.rotation,
        target.scale,
        { recordHistory: false },
      );
    });
  }, [handleModelTransform]);

  // Toolbar action handlers
  const handleArrange = useCallback(() => {
    if (!onModelTransform || models.length === 0) return;

    const cols = Math.max(1, Math.ceil(Math.sqrt(models.length)));
    const rows = Math.max(1, Math.ceil(models.length / cols));
    const stepX = bedConfig.width / (cols + 1);
    const stepY = bedConfig.depth / (rows + 1);

    const deltas: TransformDelta[] = [];

    models.forEach((model, index) => {
      const col = index % cols;
      const row = Math.floor(index / cols);

      const x = -bedConfig.width / 2 + stepX * (col + 1);
      const y = -bedConfig.depth / 2 + stepY * (row + 1);

      const nextPosition: [number, number, number] = [x, y, model.position[2]];

      if (!triplesEqual(model.position, nextPosition)) {
        deltas.push({
          modelId: model.id,
          before: {
            position: model.position,
            rotation: model.rotation,
            scale: model.scale,
          },
          after: {
            position: nextPosition,
            rotation: model.rotation,
            scale: model.scale,
          },
        });
      }

      handleModelTransform(model.id, nextPosition, model.rotation, model.scale, { recordHistory: false });
    });

    if (deltas.length > 0) {
      pushHistoryEntry({ action: 'Auto Arrange', deltas });
    }
  }, [bedConfig.depth, bedConfig.width, handleModelTransform, models, onModelTransform, pushHistoryEntry, triplesEqual]);

  const handleOrient = useCallback(() => {
    if (!onModelTransform) return;
    const selected = getSelectedModel();
    if (!selected) return;

    // Reset orientation to canonical axes.
    handleModelTransform(selected.id, selected.position, [0, 0, 0], selected.scale, { actionLabel: 'Orient Model' });
  }, [getSelectedModel, handleModelTransform, onModelTransform]);

  const handleLayFlat = useCallback(() => {
    if (!onModelTransform) return;
    const selected = getSelectedModel();
    if (!selected) return;

    // Keep current Z yaw while removing tilt on X/Y.
    handleModelTransform(
      selected.id,
      selected.position,
      [0, 0, selected.rotation[2]],
      selected.scale,
      { actionLabel: 'Lay Flat' },
    );
  }, [getSelectedModel, handleModelTransform, onModelTransform]);

  const handleSplit = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Split model'); }
    // Placeholder history marker until split operation state is implemented.
    pushHistoryEntry({ action: 'Split Model', deltas: [] });
  }, [pushHistoryEntry]);

  const handleCut = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Cut model'); }
    // Placeholder history marker until cut operation state is implemented.
    pushHistoryEntry({ action: 'Cut Model', deltas: [] });
  }, [pushHistoryEntry]);

  const handleMeasure = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Measure tool'); }
    // Placeholder history marker until measure state is implemented.
    pushHistoryEntry({ action: 'Measure Tool', deltas: [] });
  }, [pushHistoryEntry]);

  const handleSupportPaint = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Support paint mode'); }
    // Placeholder history marker until support painting state is implemented.
    pushHistoryEntry({ action: 'Support Painting', deltas: [] });
  }, [pushHistoryEntry]);

  const handleSeamPaint = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Seam paint mode'); }
    // Placeholder history marker until seam painting state is implemented.
    pushHistoryEntry({ action: 'Seam Painting', deltas: [] });
  }, [pushHistoryEntry]);

  const handleUndo = useCallback(() => {
    if (undoStack.length > 0) {
      const lastAction = undoStack[undoStack.length - 1];
      isApplyingHistoryRef.current = true;
      applyHistoryEntry(lastAction, 'before');
      isApplyingHistoryRef.current = false;
      setUndoStack(prev => prev.slice(0, -1));
      setRedoStack(prev => [...prev, lastAction]);
    }
  }, [applyHistoryEntry, undoStack]);

  const handleRedo = useCallback(() => {
    if (redoStack.length > 0) {
      const lastRedo = redoStack[redoStack.length - 1];
      isApplyingHistoryRef.current = true;
      applyHistoryEntry(lastRedo, 'after');
      isApplyingHistoryRef.current = false;
      setRedoStack(prev => prev.slice(0, -1));
      setUndoStack(prev => [...prev, lastRedo]);
    }
  }, [applyHistoryEntry, redoStack]);

  const handleAssemblyView = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Assembly view'); }
    // Placeholder history marker until assembly view state is implemented.
    pushHistoryEntry({ action: 'Assembly View', deltas: [] });
  }, [pushHistoryEntry]);

  const handleKeyboardShortcuts = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Show keyboard shortcuts'); }
    // Placeholder history marker until shortcut dialog state is implemented.
    pushHistoryEntry({ action: 'Keyboard Shortcuts', deltas: [] });
  }, [pushHistoryEntry]);

  const handleToolChange = useCallback((tool: ToolType) => {
    if (!hasSelection && tool !== 'layers') return;

    if (tool === 'move') {
      const selectedModel = selectedModelId ? models.find((m) => m.id === selectedModelId) : undefined;
      if (selectedModel) {
        setMovePositionInput(selectedModel.position);
      }
      setActiveTool('move');
      return;
    }

    if (tool === 'scale') {
      if (selectedModelMetrics) {
        setScalePercentInput([100, 100, 100]);
        setScaleMmInput(selectedModelMetrics.currentSize);
      }
      setScaleMode('percent');
      setUniformScale(true);
      setActiveTool('scale');
      return;
    }

    if (tool === 'rotate') {
      const selectedModel = selectedModelId ? models.find((m) => m.id === selectedModelId) : undefined;
      if (selectedModel) {
        const baseAbsolute: [number, number, number] = [
          radToDeg(selectedModel.rotation[0]),
          radToDeg(selectedModel.rotation[1]),
          radToDeg(selectedModel.rotation[2]),
        ];

        setRotateBaseAbsoluteInput(baseAbsolute);
        setRotateRelativeInput([0, 0, 0]);
        setRotateAbsoluteInput(baseAbsolute);
      }
      setActiveTool('rotate');
      return;
    }

    setActiveTool(tool);
  }, [hasSelection, selectedModelId, models, selectedModelMetrics]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!hasSelection) return;

      const target = event.target as HTMLElement | null;
      const tagName = target?.tagName?.toLowerCase();
      if (tagName === 'input' || tagName === 'textarea' || tagName === 'select' || target?.isContentEditable) {
        return;
      }

      if (event.metaKey || event.ctrlKey || event.altKey) return;

      const key = event.key.toLowerCase();
      if (key === 't') {
        event.preventDefault();
        handleToolChange('move');
      } else if (key === 'r') {
        event.preventDefault();
        handleToolChange('rotate');
      } else if (key === 's') {
        event.preventDefault();
        handleToolChange('scale');
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [handleToolChange, hasSelection]);

  const handleLayersToggle = useCallback(() => {
    setShowLayers(prev => !prev);
  }, []);

  const handleScaleApply = useCallback(() => {
    if (!selectedModelMetrics || !onModelTransform) {
      return;
    }

    const model = models.find((m) => m.id === selectedModelMetrics.modelId);
    if (!model) {
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

    handleModelTransform(model.id, model.position, model.rotation, newScale, { actionLabel: 'Scale Model' });
  }, [handleModelTransform, models, onModelTransform, scaleMmInput, scaleMode, scalePercentInput, selectedModelMetrics, uniformScale]);

  const handleMoveApply = useCallback(() => {
    if (!selectedModelId || !onModelTransform) {
      return;
    }

    const model = models.find((m) => m.id === selectedModelId);
    if (!model) {
      return;
    }

    handleModelTransform(model.id, movePositionInput, model.rotation, model.scale, { actionLabel: 'Move Model' });
  }, [selectedModelId, handleModelTransform, onModelTransform, models, movePositionInput]);

  const handleRotateApply = useCallback(() => {
    if (!selectedModelId || !onModelTransform) {
      return;
    }

    const model = models.find((m) => m.id === selectedModelId);
    if (!model) {
      return;
    }

    const nextRotation: [number, number, number] = [
      degToRad(rotateAbsoluteInput[0]),
      degToRad(rotateAbsoluteInput[1]),
      degToRad(rotateAbsoluteInput[2]),
    ];

    handleModelTransform(model.id, model.position, nextRotation, model.scale, { actionLabel: 'Rotate Model' });

    // After applying absolute rotation, treat the new absolute as baseline.
    setRotateBaseAbsoluteInput(rotateAbsoluteInput);
    setRotateRelativeInput([0, 0, 0]);
  }, [selectedModelId, handleModelTransform, onModelTransform, models, rotateAbsoluteInput]);

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
          onModelTransform={handleModelTransform}
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

        {/* Non-modal transform panel: can be used alongside gizmo controls */}
        {hasSelection && activeTool && activeTool !== 'layers' && (
          <div className="absolute right-4 bottom-24 z-20 w-[min(560px,calc(100%-2rem))] rounded-md border border-pf-border bg-pf-bg-1/95 backdrop-blur-xs shadow-lg p-3">
            <div className="flex items-center justify-between mb-2">
              <div className="text-sm font-semibold text-pf-text-primary">
                {activeTool === 'move' ? 'Move' : activeTool === 'rotate' ? 'Rotate' : 'Scale'}
              </div>
              <Button variant="subtle" size="sm" onClick={() => setActiveTool(null)}>Close</Button>
            </div>

            {activeTool === 'move' && (
              <div className="space-y-2">
                <div className="grid grid-cols-[180px_1fr] items-center gap-2">
                  <Select value="world" onChange={() => {}}>
                    <option value="world">World coordinates</option>
                  </Select>
                  <div className="grid grid-cols-3 gap-2 text-center">
                    <span className="text-sm text-red-500 font-medium">X</span>
                    <span className="text-sm text-green-500 font-medium">Y</span>
                    <span className="text-sm text-sky-500 font-medium">Z</span>
                  </div>
                </div>
                <div className="grid grid-cols-[110px_1fr_34px] items-center gap-2">
                  <div className="text-sm text-pf-text-primary">Position</div>
                  <div className="grid grid-cols-3 gap-2">
                    <Input
                      type="number"
                      step="0.01"
                      value={String(movePositionInput[0])}
                      onChange={(e) => {
                        const value = Number(e.target.value || 0);
                        setMovePositionInput((prev) => [value, prev[1], prev[2]]);
                      }}
                    />
                    <Input
                      type="number"
                      step="0.01"
                      value={String(movePositionInput[1])}
                      onChange={(e) => {
                        const value = Number(e.target.value || 0);
                        setMovePositionInput((prev) => [prev[0], value, prev[2]]);
                      }}
                    />
                    <Input
                      type="number"
                      step="0.01"
                      value={String(movePositionInput[2])}
                      onChange={(e) => {
                        const value = Number(e.target.value || 0);
                        setMovePositionInput((prev) => [prev[0], prev[1], value]);
                      }}
                    />
                  </div>
                  <div className="text-xs text-pf-text-primary">mm</div>
                </div>
                <div className="flex justify-end">
                  <Button variant="primary" onClick={handleMoveApply}>Apply</Button>
                </div>
              </div>
            )}

            {activeTool === 'rotate' && (
              <div className="space-y-2">
                <div className="grid grid-cols-[180px_1fr] items-center gap-2">
                  <Select value="world" onChange={() => {}}>
                    <option value="world">World coordinates</option>
                  </Select>
                  <div className="grid grid-cols-3 gap-2 text-center">
                    <span className="text-sm text-red-500 font-medium">X</span>
                    <span className="text-sm text-green-500 font-medium">Y</span>
                    <span className="text-sm text-sky-500 font-medium">Z</span>
                  </div>
                </div>

                <div className="grid grid-cols-[110px_1fr_20px] items-center gap-2">
                  <div className="text-sm text-pf-text-primary">Relative</div>
                  <div className="grid grid-cols-3 gap-2">
                    <Input type="number" step="0.01" value={String(rotateRelativeInput[0])} onChange={(e) => setRotateRelativeAxis(0, Number(e.target.value || 0))} />
                    <Input type="number" step="0.01" value={String(rotateRelativeInput[1])} onChange={(e) => setRotateRelativeAxis(1, Number(e.target.value || 0))} />
                    <Input type="number" step="0.01" value={String(rotateRelativeInput[2])} onChange={(e) => setRotateRelativeAxis(2, Number(e.target.value || 0))} />
                  </div>
                  <div className="text-xs text-pf-text-primary">°</div>
                </div>

                <div className="grid grid-cols-[110px_1fr_20px_auto] items-center gap-2">
                  <div className="text-sm text-pf-text-primary">Absolute</div>
                  <div className="grid grid-cols-3 gap-2">
                    <Input type="number" step="0.01" value={String(rotateAbsoluteInput[0])} onChange={(e) => setRotateAbsoluteAxis(0, Number(e.target.value || 0))} />
                    <Input type="number" step="0.01" value={String(rotateAbsoluteInput[1])} onChange={(e) => setRotateAbsoluteAxis(1, Number(e.target.value || 0))} />
                    <Input type="number" step="0.01" value={String(rotateAbsoluteInput[2])} onChange={(e) => setRotateAbsoluteAxis(2, Number(e.target.value || 0))} />
                  </div>
                  <div className="text-xs text-pf-text-primary">°</div>
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={() => {
                      setRotateBaseAbsoluteInput([0, 0, 0]);
                      setRotateRelativeInput([0, 0, 0]);
                      setRotateAbsoluteInput([0, 0, 0]);
                    }}
                  >
                    Reset
                  </Button>
                </div>

                <div className="flex justify-end">
                  <Button variant="primary" onClick={handleRotateApply}>Apply</Button>
                </div>
              </div>
            )}

            {activeTool === 'scale' && (
              <div className="space-y-2">
                <div className="grid grid-cols-[180px_160px_1fr] items-center gap-2">
                  <div>
                    <div className="text-xs text-pf-text-muted mb-1">Scale mode</div>
                    <Select
                      value={scaleMode}
                      onChange={(e) => setScaleMode(e.target.value === 'mm' ? 'mm' : 'percent')}
                    >
                      <option value="percent">Percent of current (%)</option>
                      <option value="mm">Absolute size (mm)</option>
                    </Select>
                  </div>
                  <div className="pt-4">
                    <Checkbox
                      label="Uniform scale"
                      checked={uniformScale}
                      onChange={(e) => setUniformScale(e.target.checked)}
                    />
                  </div>
                  <div className="grid grid-cols-3 gap-2 text-center">
                    <span className="text-sm text-red-500 font-medium">X</span>
                    <span className="text-sm text-green-500 font-medium">Y</span>
                    <span className="text-sm text-sky-500 font-medium">Z</span>
                  </div>
                </div>

                {/* Primary input row with current mode */}
                <div className="grid grid-cols-[110px_1fr_50px] items-center gap-2">
                  <div className="text-sm text-pf-text-primary">{scaleMode === 'percent' ? 'Scale' : 'Size'}</div>
                  <div className="grid grid-cols-3 gap-2">
                    <Input
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
                  <div className="text-xs text-pf-text-primary">{scaleMode === 'percent' ? '%' : 'mm'}</div>
                </div>

                {/* Reference row showing the alternative format */}
                {selectedModelMetrics && (
                  <div className="grid grid-cols-[110px_1fr_50px] items-center gap-2">
                    <div className="text-xs text-pf-text-muted">{scaleMode === 'percent' ? 'Size' : 'Scale'}</div>
                    <div className="grid grid-cols-3 gap-2">
                      {scaleMode === 'percent' ? (
                        <>
                          <div className="text-xs text-pf-text-muted bg-pf-bg-secondary rounded px-2 py-1 text-center">
                            {formatValue((selectedModelMetrics.baseSize[0] * scalePercentInput[0]) / 100)}
                          </div>
                          <div className="text-xs text-pf-text-muted bg-pf-bg-secondary rounded px-2 py-1 text-center">
                            {formatValue((selectedModelMetrics.baseSize[1] * scalePercentInput[1]) / 100)}
                          </div>
                          <div className="text-xs text-pf-text-muted bg-pf-bg-secondary rounded px-2 py-1 text-center">
                            {formatValue((selectedModelMetrics.baseSize[2] * scalePercentInput[2]) / 100)}
                          </div>
                        </>
                      ) : (
                        <>
                          <div className="text-xs text-pf-text-muted bg-pf-bg-secondary rounded px-2 py-1 text-center">
                            {formatValue((scaleMmInput[0] / selectedModelMetrics.baseSize[0]) * 100)}
                          </div>
                          <div className="text-xs text-pf-text-muted bg-pf-bg-secondary rounded px-2 py-1 text-center">
                            {formatValue((scaleMmInput[1] / selectedModelMetrics.baseSize[1]) * 100)}
                          </div>
                          <div className="text-xs text-pf-text-muted bg-pf-bg-secondary rounded px-2 py-1 text-center">
                            {formatValue((scaleMmInput[2] / selectedModelMetrics.baseSize[2]) * 100)}
                          </div>
                        </>
                      )}
                    </div>
                    <div className="text-xs text-pf-text-muted">{scaleMode === 'percent' ? 'mm' : '%'}</div>
                  </div>
                )}

                {selectedModelMetrics && (
                  <div className="text-xs text-pf-text-muted border-t border-pf-border pt-2">
                    Base size: X {formatValue(selectedModelMetrics.baseSize[0])} mm, Y {formatValue(selectedModelMetrics.baseSize[1])} mm, Z {formatValue(selectedModelMetrics.baseSize[2])} mm
                  </div>
                )}

                <div className="flex justify-end">
                  <Button variant="primary" onClick={handleScaleApply}>Apply</Button>
                </div>
              </div>
            )}
          </div>
        )}
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
