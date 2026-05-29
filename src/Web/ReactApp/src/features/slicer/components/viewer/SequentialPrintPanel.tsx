/**
 * Sequential Print Panel — floating panel that shows clearance settings,
 * print order, and collision warnings when sequential mode is active.
 * Follows the DecimationPanel floating-panel pattern.
 */
import { useCallback } from 'react';
import clsx from 'clsx';
import { Input, Toggle } from '@/common/components/ui';
import type {
  PrintheadClearance,
  CollisionResult,
  SequentialPrintOrder,
} from '../../utils/sequentialPrinting';

export interface SequentialPrintPanelProps {
  enabled: boolean;
  onToggle: (enabled: boolean) => void;
  clearance: PrintheadClearance;
  onClearanceChange: (clearance: PrintheadClearance) => void;
  printOrder: SequentialPrintOrder;
  modelNames: Map<string, string>;
  modelPositions: Map<string, number>;
}

export function SequentialPrintPanel({
  enabled,
  onToggle,
  clearance,
  onClearanceChange,
  printOrder,
  modelNames,
  modelPositions,
}: SequentialPrintPanelProps) {
  const updateField = useCallback(
    (field: keyof PrintheadClearance, value: number) => {
      onClearanceChange({ ...clearance, [field]: value });
    },
    [clearance, onClearanceChange],
  );

  const collisionPairs = printOrder.collisions.map((c: CollisionResult) => ({
    a: modelNames.get(c.modelA) ?? c.modelA,
    b: modelNames.get(c.modelB) ?? c.modelB,
  }));

  return (
    <div
      className="absolute bottom-4 right-4 bg-pf-bg-2/95 backdrop-blur-sm rounded-lg border border-pf-border shadow-xl p-4 w-80 z-20"
      role="region"
      aria-label="Sequential Printing"
    >
      {/* Header with toggle */}
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-semibold text-pf-text-primary">
          Sequential Printing
        </h3>
        <label className="flex items-center gap-2 cursor-pointer">
          <span className="text-xs text-pf-text-muted">
            {enabled ? 'On' : 'Off'}
          </span>
          <Toggle
            size="sm"
            checked={enabled}
            onChange={() => onToggle(!enabled)}
          />
        </label>
      </div>

      {enabled && (
        <>
          {/* Clearance inputs */}
          <div className="mb-3">
            <div className="text-xs text-pf-text-muted mb-1.5 font-medium">
              Printhead Clearance (mm)
            </div>
            <div className="grid grid-cols-2 gap-x-3 gap-y-1.5">
              <label className="flex items-center gap-1.5 text-xs text-pf-text-secondary">
                <span className="w-10">Left</span>
                <Input
                  type="number"
                  min={0}
                  step={1}
                  value={String(clearance.offsetLeft)}
                  onChange={(e) =>
                    updateField('offsetLeft', Math.max(0, Number(e.target.value || 0)))
                  }
                  className="text-xs"
                />
              </label>
              <label className="flex items-center gap-1.5 text-xs text-pf-text-secondary">
                <span className="w-10">Right</span>
                <Input
                  type="number"
                  min={0}
                  step={1}
                  value={String(clearance.offsetRight)}
                  onChange={(e) =>
                    updateField('offsetRight', Math.max(0, Number(e.target.value || 0)))
                  }
                  className="text-xs"
                />
              </label>
              <label className="flex items-center gap-1.5 text-xs text-pf-text-secondary">
                <span className="w-10">Front</span>
                <Input
                  type="number"
                  min={0}
                  step={1}
                  value={String(clearance.offsetFront)}
                  onChange={(e) =>
                    updateField('offsetFront', Math.max(0, Number(e.target.value || 0)))
                  }
                  className="text-xs"
                />
              </label>
              <label className="flex items-center gap-1.5 text-xs text-pf-text-secondary">
                <span className="w-10">Back</span>
                <Input
                  type="number"
                  min={0}
                  step={1}
                  value={String(clearance.offsetBack)}
                  onChange={(e) =>
                    updateField('offsetBack', Math.max(0, Number(e.target.value || 0)))
                  }
                  className="text-xs"
                />
              </label>
            </div>
            <label className="flex items-center gap-1.5 text-xs text-pf-text-secondary mt-1.5">
              <span className="w-24">Height clearance</span>
              <Input
                type="number"
                min={0}
                step={1}
                value={String(clearance.clearanceHeight)}
                onChange={(e) =>
                  updateField('clearanceHeight', Math.max(0, Number(e.target.value || 0)))
                }
                className="text-xs"
              />
            </label>
          </div>

          {/* Feasibility badge */}
          <div className="flex items-center gap-2 mb-2">
            <span
              className={clsx(
                'inline-flex items-center gap-1 px-2 py-0.5 rounded-sm text-xs font-medium',
                printOrder.feasible
                  ? 'bg-green-500/15 text-green-400'
                  : 'bg-red-500/15 text-red-400',
              )}
            >
              {printOrder.feasible ? '✓ Valid order' : '⚠ Collisions detected'}
            </span>
            <span className="text-xs text-pf-text-muted">
              {printOrder.order.length} object{printOrder.order.length !== 1 ? 's' : ''}
            </span>
          </div>

          {/* Print order list */}
          <div className="mb-2 max-h-36 overflow-y-auto">
            <div className="text-xs text-pf-text-muted mb-1 font-medium">
              Print Order
            </div>
            <ol className="space-y-0.5">
              {printOrder.order.map((modelId, idx) => {
                const name = modelNames.get(modelId) ?? modelId;
                const yPos = modelPositions.get(modelId);
                return (
                  <li
                    key={modelId}
                    className="flex items-center gap-2 text-xs px-1.5 py-1 rounded bg-pf-bg-0/50"
                  >
                    <span className="font-mono text-pf-text-muted w-4 text-right">
                      {idx + 1}.
                    </span>
                    <span className="text-pf-text-primary truncate flex-1">
                      {name}
                    </span>
                    {yPos != null && (
                      <span className="text-pf-text-muted font-mono shrink-0">
                        Y: {yPos.toFixed(0)}mm
                      </span>
                    )}
                  </li>
                );
              })}
            </ol>
          </div>

          {/* Collision warnings */}
          {collisionPairs.length > 0 && (
            <div className="border-t border-pf-border pt-2">
              <div className="text-xs text-red-400 mb-1 font-medium">
                Collisions
              </div>
              <ul className="space-y-0.5 mb-2">
                {collisionPairs.map((pair, idx) => (
                  <li
                    key={idx}
                    className="text-xs text-red-300/80 flex items-center gap-1"
                  >
                    <span>⚠</span>
                    <span>
                      {pair.a} ↔ {pair.b}
                    </span>
                  </li>
                ))}
              </ul>
              <p className="text-xs text-pf-text-muted italic">
                Move models further apart or reduce clearance values.
              </p>
            </div>
          )}
        </>
      )}
    </div>
  );
}

export default SequentialPrintPanel;
