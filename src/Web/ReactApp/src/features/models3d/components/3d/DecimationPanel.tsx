import { useState } from 'react';
import { Button } from '@/common/components/ui/Button';
import { Badge } from '@/common/components/ui/Badge';

interface DecimationPanelProps {
  originalFaces: number;
  originalVertices: number;
  onPreview: (reduction: number) => void;
  onApply: () => void;
  onReset: () => void;
  previewResult?: {
    resultFaces: number;
    resultVertices: number;
    reductionPercent: number;
  };
  isProcessing: boolean;
}

export function DecimationPanel({
  originalFaces,
  originalVertices,
  onPreview,
  onApply,
  onReset,
  previewResult,
  isProcessing,
}: DecimationPanelProps) {
  const [reduction, setReduction] = useState(50);

  return (
    <div
      className="absolute bottom-4 left-4 bg-pf-bg-2/95 backdrop-blur-sm rounded-lg border border-pf-border shadow-xl p-4 w-72 z-10"
      role="region"
      aria-label="Mesh Simplification"
    >
      <h3 className="text-sm font-semibold text-pf-text-primary mb-3">
        Mesh Simplification
      </h3>

      {/* Stats */}
      <div className="grid grid-cols-2 gap-2 mb-3 text-xs">
        <div>
          <span className="text-pf-text-muted">Original:</span>
          <div className="font-mono text-pf-text-primary">
            {originalFaces.toLocaleString()} faces
          </div>
          <div className="font-mono text-pf-text-secondary">
            {originalVertices.toLocaleString()} verts
          </div>
        </div>
        {previewResult && (
          <div>
            <span className="text-pf-text-muted">Result:</span>
            <div className="font-mono text-pf-text-primary">
              {previewResult.resultFaces.toLocaleString()} faces
              <Badge variant="success" size="sm" className="ml-1">
                -{previewResult.reductionPercent.toFixed(0)}%
              </Badge>
            </div>
            <div className="font-mono text-pf-text-secondary">
              {previewResult.resultVertices.toLocaleString()} verts
            </div>
          </div>
        )}
      </div>

      {/* Reduction slider */}
      <label
        htmlFor="decimation-slider"
        className="block text-xs text-pf-text-muted mb-1"
      >
        Target Reduction: {reduction}%
      </label>
      <input
        id="decimation-slider"
        type="range"
        min={10}
        max={90}
        step={5}
        value={reduction}
        onChange={(e) => setReduction(Number(e.target.value))}
        className="w-full mb-3 accent-pf-accent"
      />

      {/* Buttons */}
      <div className="flex gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPreview(reduction / 100)}
          loading={isProcessing}
          className="flex-1"
        >
          Preview
        </Button>
        <Button
          variant="primary"
          size="sm"
          onClick={() => onApply()}
          loading={isProcessing}
          disabled={!previewResult}
          className="flex-1"
        >
          Download
        </Button>
        {previewResult && (
          <Button variant="subtle" size="sm" onClick={onReset}>
            Reset
          </Button>
        )}
      </div>
    </div>
  );
}
