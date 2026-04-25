/**
 * PaintToolPanel — floating panel for paint tool settings.
 * Shows when any paint mode is active (color, support, seam, fuzzy skin).
 * Follows the TextTool / SequentialPrintPanel floating panel pattern.
 */
import { useState, useCallback } from 'react';
import clsx from 'clsx';
import { Button, Slider, Select } from '@/common/components/ui';
import { FormField } from '@/common/components/ui';

export type PaintToolType = 'color' | 'support' | 'seam' | 'fuzzySkin';
export type PaintMode = 'paint' | 'erase';
export type SupportPaintVariant = 'enforce' | 'block';
export type SeamPaintVariant = 'preferred' | 'blocked';
export type BrushShape = 'circle' | 'square';

export interface PaintToolPanelProps {
  activeTool: PaintToolType;
  onClose: () => void;
  /** Brush size (1–20) */
  brushSize: number;
  onBrushSizeChange: (size: number) => void;
  /** Paint or erase mode */
  paintMode: PaintMode;
  onPaintModeChange: (mode: PaintMode) => void;
  /** Brush shape */
  brushShape: BrushShape;
  onBrushShapeChange: (shape: BrushShape) => void;
  /** Clear all paint for active tool + model */
  onClearAll: () => void;

  // --- Color paint ---
  /** Active extruder index for color painting */
  activeExtruder?: number;
  onExtruderChange?: (index: number) => void;
  /** Total extruder count */
  extruderCount?: number;

  // --- Support paint ---
  supportVariant?: SupportPaintVariant;
  onSupportVariantChange?: (variant: SupportPaintVariant) => void;

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

export function PaintToolPanel({
  activeTool,
  onClose,
  brushSize,
  onBrushSizeChange,
  paintMode,
  onPaintModeChange,
  brushShape,
  onBrushShapeChange,
  onClearAll,
  activeExtruder = 0,
  onExtruderChange,
  extruderCount = 4,
  supportVariant = 'enforce',
  onSupportVariantChange,
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

  return (
    <div
      className="absolute bottom-4 left-4 bg-pf-bg-2/95 backdrop-blur-sm rounded-lg border border-pf-border shadow-xl p-4 w-72 z-20"
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
            Tip: Right-click always erases, X toggles mode
          </p>
        </div>

        {/* Brush size */}
        <FormField label={`Brush size: ${brushSize}`} htmlFor="paint-brush-size">
          <Slider
            id="paint-brush-size"
            value={brushSize}
            onChange={onBrushSizeChange}
            min={1}
            max={20}
            step={1}
            aria-label="Brush size"
          />
        </FormField>

        {/* Brush shape */}
        <FormField label="Brush shape" htmlFor="paint-brush-shape">
          <Select
            id="paint-brush-shape"
            value={brushShape}
            onChange={(e) => onBrushShapeChange(e.target.value as BrushShape)}
          >
            <option value="circle">Circle</option>
            <option value="square">Square</option>
          </Select>
        </FormField>

        {/* Tool-specific options */}
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

        {activeTool === 'support' && (
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
        )}

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

        {/* Actions */}
        <div className="pt-2 border-t border-pf-border/40">
          {!showClearConfirm ? (
            <Button variant="danger" size="sm" onClick={handleClearAll} className="w-full">
              Clear All Paint
            </Button>
          ) : (
            <div className="flex gap-1">
              <Button variant="danger" size="sm" onClick={handleClearAll} className="flex-1">
                Confirm Clear
              </Button>
              <Button variant="secondary" size="sm" onClick={handleClearCancel} className="flex-1">
                Cancel
              </Button>
            </div>
          )}
        </div>

        {/* Keyboard hints */}
        <div className="text-[10px] text-pf-text-muted space-y-0.5">
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">[</kbd> / <kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">]</kbd> Brush size</p>
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">X</kbd> Toggle paint/erase</p>
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">P</kbd> Cycle paint tools</p>
          <p><kbd className="px-1 py-0.5 bg-pf-bg-0 rounded text-[9px]">Esc</kbd> Exit paint mode</p>
        </div>
      </div>
    </div>
  );
}

export default PaintToolPanel;
