/**
 * TextTool — floating panel for 3D text annotation
 *
 * Provides controls for composing text, choosing a font, and entering
 * placement mode.  The actual raycast-based placement is handled by the
 * parent workspace which passes a placement callback.
 */
import { useState, useCallback } from 'react';
import { Button, Input, Select } from '@/common/components/ui';
import { FormField } from '@/common/components/ui';
import type { FontFamily } from '@/features/models3d/utils/textGeometry';

export interface TextToolConfig {
  text: string;
  fontSize: number;
  extrusionDepth: number;
  fontFamily: FontFamily;
}

export interface TextToolProps {
  /** Whether the tool is in placement mode (waiting for surface click) */
  placementMode: boolean;
  /** Enter placement mode with the current config */
  onStartPlacement: (config: TextToolConfig) => void;
  /** Cancel / close the text tool */
  onCancel: () => void;
}

const MIN_FONT_SIZE = 1;
const MAX_FONT_SIZE = 50;
const MIN_DEPTH = 0.2;
const MAX_DEPTH = 10;
const MAX_TEXT_LENGTH = 100;

export function TextTool({ placementMode, onStartPlacement, onCancel }: TextToolProps) {
  const [text, setText] = useState('');
  const [fontSize, setFontSize] = useState(10);
  const [extrusionDepth, setExtrusionDepth] = useState(1);
  const [fontFamily, setFontFamily] = useState<FontFamily>('sans-serif');

  const canPlace = text.trim().length > 0 && !placementMode;

  const handlePlace = useCallback(() => {
    if (!canPlace) return;
    onStartPlacement({
      text: text.trim(),
      fontSize,
      extrusionDepth,
      fontFamily,
    });
  }, [canPlace, text, fontSize, extrusionDepth, fontFamily, onStartPlacement]);

  return (
    <div
      className="absolute bottom-4 left-4 bg-pf-bg-2/95 backdrop-blur-sm rounded-lg border border-pf-border shadow-xl p-4 w-72 z-10"
      role="region"
      aria-label="3D Text Annotation"
    >
      <h3 className="text-sm font-semibold text-pf-text-primary mb-3">
        3D Text
      </h3>

      <div className="space-y-3">
        <FormField label="Text" htmlFor="text-tool-input" required>
          <Input
            id="text-tool-input"
            value={text}
            onChange={(e) => setText(e.target.value.slice(0, MAX_TEXT_LENGTH))}
            placeholder="Enter text…"
            maxLength={MAX_TEXT_LENGTH}
            disabled={placementMode}
          />
        </FormField>

        <div className="grid grid-cols-2 gap-2">
          <FormField label="Font size (mm)" htmlFor="text-tool-size">
            <Input
              id="text-tool-size"
              type="number"
              min={MIN_FONT_SIZE}
              max={MAX_FONT_SIZE}
              step={0.5}
              value={String(fontSize)}
              onChange={(e) => {
                const v = Math.min(MAX_FONT_SIZE, Math.max(MIN_FONT_SIZE, Number(e.target.value) || MIN_FONT_SIZE));
                setFontSize(v);
              }}
              disabled={placementMode}
            />
          </FormField>

          <FormField label="Depth (mm)" htmlFor="text-tool-depth">
            <Input
              id="text-tool-depth"
              type="number"
              min={MIN_DEPTH}
              max={MAX_DEPTH}
              step={0.1}
              value={String(extrusionDepth)}
              onChange={(e) => {
                const v = Math.min(MAX_DEPTH, Math.max(MIN_DEPTH, Number(e.target.value) || MIN_DEPTH));
                setExtrusionDepth(v);
              }}
              disabled={placementMode}
            />
          </FormField>
        </div>

        <FormField label="Font" htmlFor="text-tool-font">
          <Select
            id="text-tool-font"
            value={fontFamily}
            onChange={(e) => setFontFamily(e.target.value as FontFamily)}
            disabled={placementMode}
          >
            <option value="sans-serif">Sans-serif</option>
            <option value="serif">Serif</option>
            <option value="monospace">Monospace</option>
          </Select>
        </FormField>

        {placementMode && (
          <p className="text-xs text-pf-accent animate-pulse">
            Click on a model surface to place the text…
          </p>
        )}
      </div>

      <div className="flex gap-2 mt-4">
        <Button
          variant="primary"
          size="sm"
          onClick={handlePlace}
          disabled={!canPlace}
          className="flex-1"
        >
          {placementMode ? 'Placing…' : 'Place on Model'}
        </Button>
        <Button variant="secondary" size="sm" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
