/**
 * PaintToolPanel — floating panel for paint tool settings.
 * Shows when any paint mode is active (color, support, seam, fuzzy skin).
 * Follows the TextTool / SequentialPrintPanel floating panel pattern.
 * Mirrors OrcaSlicer's paint tool options for full parity.
 */
import { useState, useCallback } from 'react';
import clsx from 'clsx';
import { Button, Slider, Input, Checkbox } from '@/common/components/ui';
import { FormField } from '@/common/components/ui';

export type PaintToolType = 'color' | 'support' | 'seam' | 'fuzzySkin';
export type PaintMode = 'paint' | 'erase';
export type SupportPaintVariant = 'enforce' | 'block';
export type SeamPaintVariant = 'preferred' | 'blocked';
export type BrushShape = 'circle' | 'square';
export type BrushToolType = 'circle' | 'sphere' | 'fill' | 'triangle';

export interface PaintToolPanelProps {
  activeTool: PaintToolType;
  onClose: () => void;
  /** Brush size (0.1–10.0) */
  brushSize: number;
  onBrushSizeChange: (size: number) => void;
  /** Paint or erase mode */
  paintMode: PaintMode;
  onPaintModeChange: (mode: PaintMode) => void;
  /** Brush shape (legacy, mapped from brushToolType) */
  brushShape: BrushShape;
  onBrushShapeChange: (shape: BrushShape) => void;
  /** Brush tool type — circle/sphere/fill/triangle */
  brushToolType: BrushToolType;
  onBrushToolTypeChange: (type: BrushToolType) => void;
  /** Clear all paint for active tool + model */
  onClearAll: () => void;

  // --- Section view (clip plane) ---
  sectionViewEnabled: boolean;
  onSectionViewEnabledChange: (enabled: boolean) => void;
  sectionViewDepth: number;
  onSectionViewDepthChange: (depth: number) => void;

  // --- Color paint ---
  activeExtruder?: number;
  onExtruderChange?: (index: number) => void;
  extruderCount?: number;

  // --- Support paint ---
  supportVariant?: SupportPaintVariant;
  onSupportVariantChange?: (variant: SupportPaintVariant) => void;
  overhangOnly?: boolean;
  onOverhangOnlyChange?: (enabled: boolean) => void;
  highlightOverhangAngle?: number;
  onHighlightOverhangAngleChange?: (angle: number) => void;

  // --- Seam paint ---
  seamVariant?: SeamPaintVariant;
  onSeamVariantChange?: (variant: SeamPaintVariant) => void;
}

const TOOL_LABELS: Record<PaintToolType, string> = {
  color: 'Color Painting',
  support: 'Support Painting',
  seam: 'Seam Painting',
  fuzzySkin: 'Fuzzy Skin Painting',
};

const TOOL_DESCRIPTIONS: Record<PaintToolType, string> = {
  color: 'Paint multi-color regions for MMU/AMS printing',
  support: 'Paint faces that need or block support material',
  seam: 'Paint preferred or blocked seam positions',
  fuzzySkin: 'Paint faces that get fuzzy skin texture',
};

const EXTRUDER_COLORS = [
  '#ef4444', '#3b82f6', '#22c55e', '#eab308',
  '#a855f7', '#f97316', '#06b6d4', '#ec4899',
];

/** SVG icons for the 4 brush tool types (match OrcaSlicer icons) */
function CircleBrushIcon({ active }: { active: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true">
      <circle cx="9" cy="9" r="7" fill="none" stroke={active ? 'currentColor' : '#888'} strokeWidth="1.5" />
      <circle cx="9" cy="9" r="3" fill={active ? 'currentColor' : '#888'} />
    </svg>
  );
}
function SphereBrushIcon({ active }: { active: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true">
      <circle cx="9" cy="9" r="7" fill="none" stroke={active ? 'currentColor' : '#888'} strokeWidth="1.5" />
      <ellipse cx="9" cy="9" rx="5" ry="3" fill="none" stroke={active ? 'currentColor' : '#888'} strokeWidth="1" />
      <circle cx="9" cy="9" r="2" fill={active ? 'currentColor' : '#888'} />
    </svg>
  );
}
function FillBrushIcon({ active }: { active: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true">
      <path d="M4 14L9 3L14 14Z" fill={active ? 'currentColor' : '#888'} opacity="0.3" />
      <path d="M4 14L9 3L14 14Z" fill="none" stroke={active ? 'currentColor' : '#888'} strokeWidth="1.5" strokeLinejoin="round" />
      <path d="M3 13h12" stroke={active ? 'currentColor' : '#888'} strokeWidth="1.5" />
    </svg>
  );
}
function TriangleBrushIcon({ active }: { active: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true">
      <path d="M9 3L15 15H3Z" fill={active ? 'currentColor' : '#888'} opacity="0.5" />
      <path d="M9 3L15 15H3Z" fill="none" stroke={active ? 'currentColor' : '#888'} strokeWidth="1.5" strokeLinejoin="round" />
    </svg>
  );
}

const BRUSH_TOOL_ICONS: Record<BrushToolType, React.FC<{ active: boolean }>> = {
  circle: CircleBrushIcon,
  sphere: SphereBrushIcon,
  fill: FillBrushIcon,
  triangle: TriangleBrushIcon,
};

const BRUSH_TOOL_LABELS: Record<BrushToolType, string> = {
  circle: 'Circle brush',
  sphere: 'Sphere brush',
  fill: 'Fill (connected region)',
  triangle: 'Fill single triangle',
};

export function PaintToolPanel({
  activeTool,
  onClose,
  brushSize,
  onBrushSizeChange,
  paintMode,
  onPaintModeChange,
  brushToolType,
  onBrushToolTypeChange,
  onClearAll,
  sectionViewEnabled,
  onSectionViewEnabledChange,
  sectionViewDepth,
  onSectionViewDepthChange,
  activeExtruder = 0,
  onExtruderChange,
  extruderCount = 4,
  supportVariant = 'enforce',
  onSupportVariantChange,
  overhangOnly = false,
  onOverhangOnlyChange,
  highlightOverhangAngle = 37,
  onHighlightOverhangAngleChange,
  seamVariant = 'preferred',
  onSeamVariantChange,
}: PaintToolPanelProps) {
  const [showClearConfirm, setShowClearConfirm] = useState(false);

  const handleClearAll = useCallback(() => {
    if (!showClearConfirm) {
      setShowClearConfirm(true);
      return;
    }
    onClearAll();
    setShowClearConfirm(false);
  }, [showClearConfirm, onClearAll]);

  const handleClearCancel = useCallback(() => {
    setShowClearConfirm(false);
  }, []);

  const handleBrushSizeInput = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = parseFloat(e.target.value);
    if (!Number.isNaN(v)) {
      onBrushSizeChange(Math.max(0.1, Math.min(10, v)));
    }
  }, [onBrushSizeChange]);

  const handleSectionDepthInput = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = parseFloat(e.target.value);
    if (!Number.isNaN(v)) {
      onSectionViewDepthChange(Math.max(0, Math.min(100, v)));
    }
  }, [onSectionViewDepthChange]);

  return (
    <div
      className="absolute bottom-4 left-4 bg-pf-bg-2/95 backdrop-blur-sm rounded-lg border border-pf-border shadow-xl p-4 w-80 z-20 max-h-[calc(100vh-8rem)] overflow-y-auto"
      role="region"
      aria-label={TOOL_LABELS[activeTool]}
    >
      {/* Header */}
      <div className="flex items-center justify-between mb-2">
        <h3 className="text-sm font-semibold text-pf-text-primary">
          {TOOL_LABELS[activeTool]}
        </h3>
        <Button variant="ghost" size="sm" onClick={onClose} title="Close paint panel">
          ✕
        </Button>
      </div>
      <p className="text-xs text-pf-text-muted mb-3">{TOOL_DESCRIPTIONS[activeTool]}</p>

      <div className="space-y-3">
        {/* Brush tool type selector — 4-icon row */}
        <div>
          <span className="text-xs font-medium text-pf-text-secondary block mb-1">Tool</span>
          <div className="flex gap-1">
            {(Object.keys(BRUSH_TOOL_ICONS) as BrushToolType[]).map((type) => {
              const Icon = BRUSH_TOOL_ICONS[type];
              const isActive = brushToolType === type;
              return (
                <Button
                  key={type}
                  variant={isActive ? 'primary' : 'secondary'}
                  size="sm"
                  onClick={() => onBrushToolTypeChange(type)}
                  title={BRUSH_TOOL_LABELS[type]}
                  aria-pressed={isActive}
                  className="flex-1 flex items-center justify-center px-1"
                >
                  <Icon active={isActive} />
                </Button>
              );
            })}
          </div>
        </div>

        {/* Paint / Erase mode toggle */}
        <div>
          <span className="text-xs font-medium text-pf-text-secondary block mb-1">Mode</span>
          <div className="flex gap-1">
            <Button
              variant={paintMode === 'paint' ? 'primary' : 'secondary'}
              size="sm"
              onClick={() => onPaintModeChange('paint')}
              className="flex-1"
            >
              Paint
            </Button>
            <Button
              variant={paintMode === 'erase' ? 'primary' : 'secondary'}
              size="sm"
              onClick={() => onPaintModeChange('erase')}
              className="flex-1"
            >
              Erase
            </Button>
          </div>
          <p className="text-[10px] text-pf-text-muted mt-1">
            Right-click always erases · X toggles mode
          </p>
        </div>

        {/* Pen size — slider + numeric input */}
        <div>
          <span className="text-xs font-medium text-pf-text-secondary block mb-1">Pen size</span>
          <div className="flex items-center gap-2">
            <Slider
              value={brushSize}
              onChange={onBrushSizeChange}
              min={0.1}
              max={10}
              step={0.1}
              aria-label="Pen size"
              className="flex-1"
            />
            <Input
              type="number"
              value={brushSize.toFixed(2)}
              onChange={handleBrushSizeInput}
              step={0.1}
              min={0.1}
              max={10}
              className="w-16 text-xs text-center"
              aria-label="Pen size value"
            />
          </div>
        </div>

        {/* Color paint — extruder palette */}
        {activeTool === 'color' && (
          <div>
            <span className="text-xs font-medium text-pf-text-secondary block mb-1.5">
              Active Extruder
            </span>
            <div className="flex gap-1 flex-wrap">
              {Array.from({ length: extruderCount }, (_, i) => (
                <Button
                  key={i}
                  variant="unstyled"
                  onClick={() => onExtruderChange?.(i)}
                  title={`Extruder ${i + 1}`}
                  className={clsx(
                    'w-7 h-7 rounded-md border-2 transition-all shrink-0 p-0',
                    activeExtruder === i
                      ? 'border-white shadow-lg scale-110'
                      : 'border-transparent hover:border-pf-border/60',
                  )}
                  style={{ backgroundColor: EXTRUDER_COLORS[i % EXTRUDER_COLORS.length] }}
                  aria-label={`Extruder ${i + 1}`}
                  aria-pressed={activeExtruder === i}
                />
              ))}
            </div>
          </div>
        )}

        {/* Support paint — variant + overhang options */}
        {activeTool === 'support' && (
          <>
            <div>
              <span className="text-xs font-medium text-pf-text-secondary block mb-1">
                Support Type
              </span>
              <div className="flex gap-1">
                <Button
                  variant={supportVariant === 'enforce' ? 'success' : 'secondary'}
                  size="sm"
                  onClick={() => onSupportVariantChange?.('enforce')}
                  className="flex-1"
                >
                  Enforce
                </Button>
                <Button
                  variant={supportVariant === 'block' ? 'danger' : 'secondary'}
                  size="sm"
                  onClick={() => onSupportVariantChange?.('block')}
                  className="flex-1"
                >
                  Block
                </Button>
              </div>
            </div>
            <div className="space-y-2">
              <Checkbox
                checked={overhangOnly}
                onChange={(e) => onOverhangOnlyChange?.(e.target.checked)}
                label="On overhangs only"
              />
              <FormField label={`Highlight overhangs: ${highlightOverhangAngle}°`} htmlFor="paint-overhang-angle">
                <div className="flex items-center gap-2">
                  <Slider
                    value={highlightOverhangAngle}
                    onChange={(v) => onHighlightOverhangAngleChange?.(v)}
                    min={0}
                    max={90}
                    step={1}
                    aria-label="Highlight overhang angle"
                    className="flex-1"
                  />
                  <Input
                    type="number"
                    value={String(highlightOverhangAngle)}
                    onChange={(e) => {
                      const v = parseInt(e.target.value, 10);
                      if (!Number.isNaN(v)) onHighlightOverhangAngleChange?.(Math.max(0, Math.min(90, v)));
                    }}
                    min={0}
                    max={90}
                    className="w-14 text-xs text-center"
                    aria-label="Overhang angle value"
                  />
                </div>
              </FormField>
            </div>
          </>
        )}

        {/* Seam paint — variant selector */}
        {activeTool === 'seam' && (
          <div>
            <span className="text-xs font-medium text-pf-text-secondary block mb-1">
              Seam Preference
            </span>
            <div className="flex gap-1">
              <Button
                variant={seamVariant === 'preferred' ? 'success' : 'secondary'}
                size="sm"
                onClick={() => onSeamVariantChange?.('preferred')}
                className="flex-1"
              >
                Preferred
              </Button>
              <Button
                variant={seamVariant === 'blocked' ? 'danger' : 'secondary'}
                size="sm"
                onClick={() => onSeamVariantChange?.('blocked')}
                className="flex-1"
              >
                Blocked
              </Button>
            </div>
          </div>
        )}

        {activeTool === 'fuzzySkin' && (
          <p className="text-xs text-pf-text-muted">
            Paint faces to apply fuzzy skin texture. Left-click to paint, right-click to erase.
          </p>
        )}

        {/* Section view (clip plane depth) */}
        <div className="space-y-1">
          <Checkbox
            checked={sectionViewEnabled}
            onChange={(e) => onSectionViewEnabledChange(e.target.checked)}
            label="Section view"
          />
          {sectionViewEnabled && (
            <div className="flex items-center gap-2 pl-5">
              <Slider
                value={sectionViewDepth}
                onChange={onSectionViewDepthChange}
                min={0}
                max={100}
                step={0.5}
                aria-label="Section view depth"
                className="flex-1"
              />
              <Input
                type="number"
                value={sectionViewDepth.toFixed(1)}
                onChange={handleSectionDepthInput}
                step={0.5}
                min={0}
                max={100}
                className="w-16 text-xs text-center"
                aria-label="Section depth value"
              />
            </div>
          )}
        </div>

        {/* Erase all painting */}
        <div className="pt-2 border-t border-pf-border/40">
          {!showClearConfirm ? (
            <Button variant="danger" size="sm" onClick={handleClearAll} className="w-full">
              Erase All Painting
            </Button>
          ) : (
            <div className="flex gap-1">
              <Button variant="danger" size="sm" onClick={handleClearAll} className="flex-1">
                Confirm Erase
              </Button>
              <Button variant="secondary" size="sm" onClick={handleClearCancel} className="flex-1">
                Cancel
              </Button>
            </div>
          )}
        </div>

        {/* Keyboard hints */}
        <div className="text-[10px] text-pf-text-muted space-y-0.5">
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">[</kbd> / <kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">]</kbd> Pen size</p>
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">X</kbd> Toggle paint/erase</p>
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">P</kbd> Cycle paint tools</p>
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">Esc</kbd> Exit paint mode</p>
        </div>
      </div>
    </div>
  );
}

export default PaintToolPanel;
