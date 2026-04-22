/**
 * Slicer Workspace Component
 * Main container combining toolbar, 3D bed visualization, left tools, and status bar
 * Matches OrcaSlicer's interface layout
 */
import React, { useState, useCallback, useEffect, useRef, useMemo } from 'react';
import * as THREE from 'three';
import { toast } from 'sonner';
import { SlicerToolbar } from './SlicerToolbar';
import { SlicerLeftTools, type ToolType } from './SlicerLeftTools';
import { SlicerStatusBar } from './SlicerStatusBar';
import { SlicerBedVisualization, type LoadedModel, type BedConfig } from './SlicerBedVisualization';
import { Button, Checkbox, Input, Select } from '@/common/components/ui';
import { RotateCcw, AlertTriangle } from 'lucide-react';

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
  /** Callback to toggle sidebar visibility */
  onToggleSidebar?: () => void;
  /** Whether sidebar is currently open */
  sidebarOpen?: boolean;
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
  onToggleSidebar,
  sidebarOpen = true,
  className = '',
}) => {
  const [activeTool, setActiveTool] = useState<ToolType | null>(null);
  const [showLayers, setShowLayers] = useState(false);
  const [showGridLines, setShowGridLines] = useState(true);
  const [layFlatMode, setLayFlatMode] = useState(false);
  const [autoOrientTrigger, setAutoOrientTrigger] = useState(0);
  const [measureMode, setMeasureMode] = useState(false);
  const [assemblyViewActive, setAssemblyViewActive] = useState(false);
  const [splitTrigger, setSplitTrigger] = useState(0);
  const [undoStack, setUndoStack] = useState<TransformHistoryEntry[]>([]);
  const [redoStack, setRedoStack] = useState<TransformHistoryEntry[]>([]);
  const isApplyingHistoryRef = useRef(false);
  const [uniformScale, setUniformScale] = useState(true);
  const [scalePercentInput, setScalePercentInput] = useState<[number, number, number]>([100, 100, 100]);
  const [scaleMmInput, setScaleMmInput] = useState<[number, number, number]>([0, 0, 0]);
  const [moveCoordinateMode, setMoveCoordinateMode] = useState<'world' | 'object'>('world');
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

  // Compute which models exceed the build volume.
  // For each model we approximate the axis-aligned bounding box from
  // baseSize * scale + position.  Rotation makes the true AABB larger;
  // we use the bounding-sphere radius of the scaled size as a
  // conservative estimate when any rotation is non-zero.
  const outOfBoundsModelIds = useMemo(() => {
    const halfW = bedConfig.width / 2;
    const halfD = bedConfig.depth / 2;
    const maxZ = bedConfig.height;
    const ids = new Set<string>();

    for (const model of models) {
      // We only know baseSize for the selected model via metrics.
      // For others, approximate using scale (baseSize unknown → skip).
      let bx: number, by: number, bz: number;
      if (selectedModelMetrics && selectedModelMetrics.modelId === model.id) {
        bx = selectedModelMetrics.baseSize[0] * model.scale[0];
        by = selectedModelMetrics.baseSize[1] * model.scale[1];
        bz = selectedModelMetrics.baseSize[2] * model.scale[2];
      } else {
        // Without baseSize we can't check — skip non-selected models for now.
        continue;
      }

      let hx: number, hy: number, hz: number;
      const hasRotation = model.rotation[0] !== 0 || model.rotation[1] !== 0 || model.rotation[2] !== 0;
      if (hasRotation) {
        // Compute exact rotated AABB from rotation matrix absolute values
        const m = new THREE.Matrix4().makeRotationFromEuler(
          new THREE.Euler(model.rotation[0], model.rotation[1], model.rotation[2]),
        );
        const e = m.elements;
        const ox = bx / 2, oy = by / 2, oz = bz / 2;
        hx = Math.abs(e[0]) * ox + Math.abs(e[4]) * oy + Math.abs(e[8]) * oz;
        hy = Math.abs(e[1]) * ox + Math.abs(e[5]) * oy + Math.abs(e[9]) * oz;
        hz = Math.abs(e[2]) * ox + Math.abs(e[6]) * oy + Math.abs(e[10]) * oz;
      } else {
        hx = bx / 2; hy = by / 2; hz = bz / 2;
      }

      const [px, py, pz] = model.position;
      // STLModel renders at world Z = pz + halfZ_raw (unscaled geometry half-height).
      // The rotated AABB center is at (px, py, pz + halfZ_raw) with half-extents (hx, hy, hz).
      const halfZRaw = selectedModelMetrics.baseSize[2] / 2;
      const epsilon = 0.75; // Avoid warning flicker from tiny floating-point boundary jitter.
      if (
        px - hx < -halfW - epsilon || px + hx > halfW + epsilon ||
        py - hy < -halfD - epsilon || py + hy > halfD + epsilon ||
        pz + halfZRaw - hz < -epsilon || pz + halfZRaw + hz > maxZ + epsilon
      ) {
        ids.add(model.id);
      }
    }
    return ids;
  }, [bedConfig.depth, bedConfig.height, bedConfig.width, models, selectedModelMetrics]);
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
    queueMicrotask(() => {
      setActiveTool(null);
      setLayFlatMode(false);
    });
  }, [selectedModelId]);


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
        setMovePositionInput(moveCoordinateMode === 'world' ? position : [0, 0, 0]);
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
    [activeTool, models, moveCoordinateMode, onModelTransform, pushHistoryEntry, selectedModelId, selectedModelMetrics, triplesEqual, uniformScale],
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

    // Trigger auto-orient inside the 3D scene (needs geometry access)
    setAutoOrientTrigger((prev) => prev + 1);
  }, [getSelectedModel, onModelTransform]);

  const handleLayFlat = useCallback(() => {
    if (!onModelTransform) return;
    const selected = getSelectedModel();
    if (!selected) return;
    // Toggle lay-flat face-picking mode
    setLayFlatMode((prev) => !prev);
  }, [getSelectedModel, onModelTransform]);

  const handleSplit = useCallback(() => {
    if (!hasSelection) {
      toast.info('Select a model first to split it');
      return;
    }
    setSplitTrigger((prev) => prev + 1);
  }, [hasSelection]);

  const handleCut = useCallback(() => {
    toast.info('Cut tool coming soon — plane-based model cutting will be available in a future update');
  }, []);

  const handleMeasure = useCallback(() => {
    setMeasureMode((prev) => {
      const next = !prev;
      if (next) {
        toast.info('Measure mode: click two points on a model surface to measure distance');
      } else {
        toast.info('Measure mode off');
      }
      return next;
    });
  }, []);

  const handleSupportPaint = useCallback(() => {
    toast.info('Support painting coming soon — brush-based support placement will be available in a future update');
  }, []);

  const handleSeamPaint = useCallback(() => {
    toast.info('Seam painting coming soon — custom seam line control will be available in a future update');
  }, []);

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
    setAssemblyViewActive((prev) => {
      const next = !prev;
      if (next) {
        toast.info('Assembly view: models offset for inspection');
      } else {
        toast.info('Assembly view off — positions restored');
      }
      return next;
    });
  }, []);

  const handleKeyboardShortcuts = useCallback(() => {
    if (window.PrintFarmerDebug?.slicer) { console.log('Show keyboard shortcuts'); }
    // Placeholder history marker until shortcut dialog state is implemented.
    pushHistoryEntry({ action: 'Keyboard Shortcuts', deltas: [] });
  }, [pushHistoryEntry]);

  const handleToolChange = useCallback((tool: ToolType) => {
    if (!hasSelection && tool !== 'layers') return;
    setLayFlatMode(false);

    if (tool === 'move') {
      const selectedModel = selectedModelId ? models.find((m) => m.id === selectedModelId) : undefined;
      if (selectedModel) {
        setMovePositionInput(moveCoordinateMode === 'world' ? selectedModel.position : [0, 0, 0]);
      }
      setActiveTool('move');
      return;
    }

    if (tool === 'scale') {
      if (selectedModelMetrics) {
        setScalePercentInput([100, 100, 100]);
        setScaleMmInput(selectedModelMetrics.currentSize);
      }
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
  }, [hasSelection, moveCoordinateMode, selectedModelId, models, selectedModelMetrics]);

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

  const applyScaleFromPercent = useCallback((percent: [number, number, number]) => {
    if (!selectedModelId || !selectedModelMetrics) return;
    const model = models.find((m) => m.id === selectedModelId);
    if (!model) return;
    const nextScale: [number, number, number] = [percent[0] / 100, percent[1] / 100, percent[2] / 100];
    handleModelTransform(model.id, model.position, model.rotation, nextScale, { actionLabel: 'Scale Model' });
  }, [selectedModelId, selectedModelMetrics, models, handleModelTransform]);

  const applyScaleFromMm = useCallback((mm: [number, number, number]) => {
    if (!selectedModelId || !selectedModelMetrics) return;
    const model = models.find((m) => m.id === selectedModelId);
    if (!model) return;
    const nextScale: [number, number, number] = [
      mm[0] / selectedModelMetrics.baseSize[0],
      mm[1] / selectedModelMetrics.baseSize[1],
      mm[2] / selectedModelMetrics.baseSize[2],
    ];
    handleModelTransform(model.id, model.position, model.rotation, nextScale, { actionLabel: 'Scale Model' });
  }, [selectedModelId, selectedModelMetrics, models, handleModelTransform]);

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
        onToggleSidebar={onToggleSidebar}
        sidebarOpen={sidebarOpen}
        canUndo={undoStack.length > 0}
        canRedo={redoStack.length > 0}
        hasModels={hasModels}
        hasSelection={hasSelection}
        measureActive={measureMode}
        assemblyActive={assemblyViewActive}
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
          outOfBoundsModelIds={outOfBoundsModelIds}
          layFlatMode={layFlatMode}
          onLayFlatComplete={() => setLayFlatMode(false)}
          autoOrientTrigger={autoOrientTrigger}
          measureMode={measureMode}
          assemblyViewActive={assemblyViewActive}
          splitTrigger={splitTrigger}
          showGrid={true}
          showAxes={true}
          showGridLines={showGridLines}
          className="w-full h-full"
        />

        {/* Out-of-bounds warning banner */}
        {outOfBoundsModelIds.size > 0 && (
          <div className="absolute top-2 left-1/2 -translate-x-1/2 z-30 flex items-center gap-2 rounded-md bg-amber-900/90 border border-amber-600 px-3 py-1.5 text-amber-200 text-xs shadow-lg backdrop-blur-xs">
            <AlertTriangle size={14} className="shrink-0" />
            <span>Object outside build volume</span>
          </div>
        )}

        {/* Left manipulation tools */}
        <SlicerLeftTools
          activeTool={activeTool}
          onToolChange={handleToolChange}
          onLayersToggle={handleLayersToggle}
          showLayers={showLayers}
          hasSelection={hasSelection}
          showGridLines={showGridLines}
          onGridToggle={() => setShowGridLines(prev => !prev)}
        />

        {/* Non-modal transform panel: can be used alongside gizmo controls */}
        {hasSelection && activeTool && activeTool !== 'layers' && (
          <div className="absolute right-4 bottom-8 z-20 w-80 rounded-md border border-pf-border bg-pf-bg-1/95 backdrop-blur-xs shadow-lg p-3">
            <div className="text-sm font-semibold text-pf-text-primary mb-2">
              {activeTool === 'move' ? 'Move' : activeTool === 'rotate' ? 'Rotate' : 'Scale'}
            </div>

            {activeTool === 'move' && (
              <div className="space-y-2">
                <Select value={moveCoordinateMode} onChange={(e) => { const mode = e.target.value === 'object' ? 'object' as const : 'world' as const; setMoveCoordinateMode(mode); if (selectedModelId) { const m = models.find((x) => x.id === selectedModelId); if (m) { setMovePositionInput(mode === 'world' ? m.position : [0, 0, 0]); } } }}>
                  <option value="world">World (absolute)</option>
                  <option value="object">Object (relative)</option>
                </Select>
                <div className="grid grid-cols-[auto_1fr_1fr_1fr_auto] items-center gap-x-2 gap-y-1.5">
                  <div />
                  <div className="text-xs text-red-500 font-medium text-center">X</div>
                  <div className="text-xs text-green-500 font-medium text-center">Y</div>
                  <div className="text-xs text-sky-500 font-medium text-center">Z</div>
                  <div />
                  <div className="text-xs text-pf-text-primary whitespace-nowrap">{moveCoordinateMode === 'world' ? 'Position' : 'Offset'}</div>
                  <Input type="number" step="0.01" value={String(movePositionInput[0])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [v, movePositionInput[1], movePositionInput[2]]; setMovePositionInput(next); if (selectedModelId) { const m = models.find((x) => x.id === selectedModelId); if (m) { const pos: [number, number, number] = moveCoordinateMode === 'world' ? next : [m.position[0] + next[0], m.position[1] + next[1], m.position[2] + next[2]]; handleModelTransform(m.id, pos, m.rotation, m.scale, { actionLabel: 'Move Model' }); } } }} />
                  <Input type="number" step="0.01" value={String(movePositionInput[1])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [movePositionInput[0], v, movePositionInput[2]]; setMovePositionInput(next); if (selectedModelId) { const m = models.find((x) => x.id === selectedModelId); if (m) { const pos: [number, number, number] = moveCoordinateMode === 'world' ? next : [m.position[0] + next[0], m.position[1] + next[1], m.position[2] + next[2]]; handleModelTransform(m.id, pos, m.rotation, m.scale, { actionLabel: 'Move Model' }); } } }} />
                  <Input type="number" step="0.01" value={String(movePositionInput[2])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [movePositionInput[0], movePositionInput[1], v]; setMovePositionInput(next); if (selectedModelId) { const m = models.find((x) => x.id === selectedModelId); if (m) { const pos: [number, number, number] = moveCoordinateMode === 'world' ? next : [m.position[0] + next[0], m.position[1] + next[1], m.position[2] + next[2]]; handleModelTransform(m.id, pos, m.rotation, m.scale, { actionLabel: 'Move Model' }); } } }} />
                  <Button variant="ghost" size="sm" className="p-1!" title="Reset position" onClick={() => { const zero: [number, number, number] = [0, 0, 0]; setMovePositionInput(zero); if (selectedModelId) { const m = models.find((x) => x.id === selectedModelId); if (m) handleModelTransform(m.id, zero, m.rotation, m.scale, { actionLabel: 'Reset Position' }); } }}>
                    <RotateCcw size={14} />
                  </Button>
                </div>
              </div>
            )}

            {activeTool === 'rotate' && (
              <div className="grid grid-cols-[auto_1fr_1fr_1fr_auto] items-center gap-x-2 gap-y-1.5">
                <div />
                <div className="text-xs text-red-500 font-medium text-center">X</div>
                <div className="text-xs text-green-500 font-medium text-center">Y</div>
                <div className="text-xs text-sky-500 font-medium text-center">Z</div>
                <div />
                <div className="text-xs text-pf-text-primary whitespace-nowrap">Relative</div>
                <Input type="number" step="0.01" value={String(rotateRelativeInput[0])} onChange={(e) => setRotateRelativeAxis(0, Number(e.target.value || 0))} />
                <Input type="number" step="0.01" value={String(rotateRelativeInput[1])} onChange={(e) => setRotateRelativeAxis(1, Number(e.target.value || 0))} />
                <Input type="number" step="0.01" value={String(rotateRelativeInput[2])} onChange={(e) => setRotateRelativeAxis(2, Number(e.target.value || 0))} />
                <div />
                <div className="text-xs text-pf-text-primary whitespace-nowrap">Absolute</div>
                <Input type="number" step="0.01" value={String(rotateAbsoluteInput[0])} onChange={(e) => setRotateAbsoluteAxis(0, Number(e.target.value || 0))} />
                <Input type="number" step="0.01" value={String(rotateAbsoluteInput[1])} onChange={(e) => setRotateAbsoluteAxis(1, Number(e.target.value || 0))} />
                <Input type="number" step="0.01" value={String(rotateAbsoluteInput[2])} onChange={(e) => setRotateAbsoluteAxis(2, Number(e.target.value || 0))} />
                <Button variant="ghost" size="sm" className="p-1!" title="Reset rotation" onClick={() => { setRotateBaseAbsoluteInput([0, 0, 0]); setRotateRelativeInput([0, 0, 0]); setRotateAbsoluteInput([0, 0, 0]); handleRotateApply(); }}>
                  <RotateCcw size={14} />
                </Button>
              </div>
            )}

            {activeTool === 'scale' && (
              <div className="grid grid-cols-[auto_1fr_1fr_1fr_auto] items-center gap-x-2 gap-y-1.5">
                <div />
                <div className="text-xs text-red-500 font-medium text-center">X</div>
                <div className="text-xs text-green-500 font-medium text-center">Y</div>
                <div className="text-xs text-sky-500 font-medium text-center">Z</div>
                <div />
                <div className="text-xs text-pf-text-primary whitespace-nowrap">Scale %</div>
                <Input type="number" step="0.01" value={String(scalePercentInput[0])} onChange={(e) => { const v = Number(e.target.value || 0); const next = applyUniformTriple([v, scalePercentInput[1], scalePercentInput[2]], v); setScalePercentInput(next); if (selectedModelMetrics) { setScaleMmInput([(selectedModelMetrics.baseSize[0] * next[0]) / 100, (selectedModelMetrics.baseSize[1] * next[1]) / 100, (selectedModelMetrics.baseSize[2] * next[2]) / 100]); } applyScaleFromPercent(next); }} />
                <Input type="number" step="0.01" disabled={uniformScale} value={String(scalePercentInput[1])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [scalePercentInput[0], v, scalePercentInput[2]]; setScalePercentInput(next); if (selectedModelMetrics) { setScaleMmInput([(selectedModelMetrics.baseSize[0] * next[0]) / 100, (selectedModelMetrics.baseSize[1] * next[1]) / 100, (selectedModelMetrics.baseSize[2] * next[2]) / 100]); } applyScaleFromPercent(next); }} />
                <Input type="number" step="0.01" disabled={uniformScale} value={String(scalePercentInput[2])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [scalePercentInput[0], scalePercentInput[1], v]; setScalePercentInput(next); if (selectedModelMetrics) { setScaleMmInput([(selectedModelMetrics.baseSize[0] * next[0]) / 100, (selectedModelMetrics.baseSize[1] * next[1]) / 100, (selectedModelMetrics.baseSize[2] * next[2]) / 100]); } applyScaleFromPercent(next); }} />
                <div />
                <div className="text-xs text-pf-text-primary whitespace-nowrap">Size mm</div>
                <Input type="number" step="0.01" value={String(scaleMmInput[0])} onChange={(e) => { const v = Number(e.target.value || 0); let next: [number, number, number]; if (uniformScale && selectedModelMetrics) { const ratio = v / (selectedModelMetrics.currentSize[0] || 1); next = [v, selectedModelMetrics.currentSize[1] * ratio, selectedModelMetrics.currentSize[2] * ratio]; } else { next = [v, scaleMmInput[1], scaleMmInput[2]]; } setScaleMmInput(next); if (selectedModelMetrics) { setScalePercentInput([(next[0] / selectedModelMetrics.baseSize[0]) * 100, (next[1] / selectedModelMetrics.baseSize[1]) * 100, (next[2] / selectedModelMetrics.baseSize[2]) * 100]); } applyScaleFromMm(next); }} />
                <Input type="number" step="0.01" disabled={uniformScale} value={String(scaleMmInput[1])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [scaleMmInput[0], v, scaleMmInput[2]]; setScaleMmInput(next); if (selectedModelMetrics) { setScalePercentInput([(next[0] / selectedModelMetrics.baseSize[0]) * 100, (next[1] / selectedModelMetrics.baseSize[1]) * 100, (next[2] / selectedModelMetrics.baseSize[2]) * 100]); } applyScaleFromMm(next); }} />
                <Input type="number" step="0.01" disabled={uniformScale} value={String(scaleMmInput[2])} onChange={(e) => { const v = Number(e.target.value || 0); const next: [number, number, number] = [scaleMmInput[0], scaleMmInput[1], v]; setScaleMmInput(next); if (selectedModelMetrics) { setScalePercentInput([(next[0] / selectedModelMetrics.baseSize[0]) * 100, (next[1] / selectedModelMetrics.baseSize[1]) * 100, (next[2] / selectedModelMetrics.baseSize[2]) * 100]); } applyScaleFromMm(next); }} />
                <Button variant="ghost" size="sm" className="p-1!" title="Reset scale" onClick={() => { setScalePercentInput([100, 100, 100]); if (selectedModelMetrics) { setScaleMmInput([...selectedModelMetrics.baseSize]); } applyScaleFromPercent([100, 100, 100]); }}>
                  <RotateCcw size={14} />
                </Button>
                <div className="col-span-5 pt-1">
                  <Checkbox label="Uniform" checked={uniformScale} onChange={(e) => setUniformScale(e.target.checked)} />
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
