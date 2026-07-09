import { useMemo, useState } from 'react';
import { Badge, Button, Card } from '@/common/components/ui';
import { AlertCircleIcon, CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { useSetToolheadSpool, useClearToolheadSpool } from '@/common/hooks/useApi';
import type { ToolheadDto } from '@/types/api';
import {
  assignSpoolsToToolheads,
  buildFilamentMatchTargets,
  type SpoolMatchConfidence,
  type ToolheadSpoolMatch,
} from '@/features/printers/utils/toolheadSpoolMatching';

interface ToolheadSpoolPickerProps {
  printerId: string;
  toolheads: ToolheadDto[];
  onSpoolChange?: () => void;
  targetFilamentColorHex?: string[];
  targetFilamentType?: string[];
}

function confidenceLabel(confidence: SpoolMatchConfidence): string {
  switch (confidence) {
    case 'exact':
      return 'Exact color';
    case 'close':
      return 'Close color';
    case 'poor':
      return 'Poor color';
    default:
      return 'No color match';
  }
}

function confidenceVariant(confidence: SpoolMatchConfidence): 'default' | 'success' | 'warning' {
  switch (confidence) {
    case 'exact':
      return 'success';
    case 'close':
      return 'default';
    default:
      return 'warning';
  }
}

function formatDeltaE(deltaE: number | null): string {
  return deltaE == null ? '—' : deltaE.toFixed(1);
}

/**
 * Multi-toolhead spool assignment interface.
 * Shows a list of toolheads with current spool assignments and allows changing/clearing them.
 * Works with both physical toolheads (Snapmaker U1) and MMU gates (QidiBox, HappyHare, AFC).
 */
export function ToolheadSpoolPicker({
  printerId,
  toolheads,
  onSpoolChange,
  targetFilamentColorHex,
  targetFilamentType,
}: ToolheadSpoolPickerProps) {
  // Track by toolhead.index (the real API index), not array position
  const [selectedToolheadIndex, setSelectedToolheadIndex] = useState<number | null>(null);
  const [manualOverrideIndexes, setManualOverrideIndexes] = useState<Set<number>>(() => new Set());
  const setSpoolMutation = useSetToolheadSpool();
  const clearSpoolMutation = useClearToolheadSpool();
  const showAutoMatch = targetFilamentColorHex !== undefined;

  const suggestions = useMemo(() => {
    if (!showAutoMatch) return [];

    return assignSpoolsToToolheads(
      buildFilamentMatchTargets(targetFilamentColorHex, targetFilamentType),
      toolheads
        .filter(toolhead => toolhead.currentSpoolId != null)
        .map(toolhead => ({
          spoolId: toolhead.currentSpoolId!,
          colorHex: toolhead.currentFilamentColor,
          material: toolhead.currentMaterial,
        })),
    );
  }, [showAutoMatch, targetFilamentColorHex, targetFilamentType, toolheads]);

  const suggestionsByToolhead = useMemo(() => new Map(
    suggestions.map(suggestion => [suggestion.toolheadIndex, suggestion]),
  ), [suggestions]);

  const handleOpenPicker = (toolheadIndex: number) => {
    setSelectedToolheadIndex(toolheadIndex);
  };

  const handleClosePicker = () => {
    setSelectedToolheadIndex(null);
  };

  const handleSpoolSelect = async (spoolId: number) => {
    if (selectedToolheadIndex === null) return;

    await setSpoolMutation.mutateAsync({
      printerId,
      toolheadIndex: selectedToolheadIndex,
      spoolId,
    });

    setManualOverrideIndexes(prev => new Set(prev).add(selectedToolheadIndex));
    handleClosePicker();
    onSpoolChange?.();
  };

  const handleApplySuggestion = async (toolheadIndex: number, spoolId: number) => {
    await setSpoolMutation.mutateAsync({
      printerId,
      toolheadIndex,
      spoolId,
    });

    onSpoolChange?.();
  };

  const handleApplyAllSuggestions = async () => {
    const updates = toolheads
      .map(toolhead => ({ toolhead, suggestion: suggestionsByToolhead.get(toolhead.index) }))
      .filter((item): item is { toolhead: ToolheadDto; suggestion: ToolheadSpoolMatch } =>
        item.suggestion?.spoolId != null
        && item.toolhead.currentSpoolId !== item.suggestion.spoolId
        && !manualOverrideIndexes.has(item.toolhead.index));

    for (const { toolhead, suggestion } of updates) {
      await setSpoolMutation.mutateAsync({
        printerId,
        toolheadIndex: toolhead.index,
        spoolId: suggestion.spoolId!,
      });
    }

    if (updates.length > 0) onSpoolChange?.();
  };

  const handleClearSpool = (toolheadIndex: number) => {
    clearSpoolMutation.mutate({
      printerId,
      toolheadIndex,
    });
    setManualOverrideIndexes(prev => new Set(prev).add(toolheadIndex));
    onSpoolChange?.();
  };

  const activeToolhead = selectedToolheadIndex !== null
    ? toolheads.find(t => t.index === selectedToolheadIndex)
    : undefined;
  const applicableSuggestionCount = toolheads.filter(toolhead => {
    const suggestion = suggestionsByToolhead.get(toolhead.index);
    return suggestion?.spoolId != null
      && suggestion.spoolId !== toolhead.currentSpoolId
      && !manualOverrideIndexes.has(toolhead.index);
  }).length;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-start justify-between gap-2 text-sm text-pf-text-secondary mb-2">
        <div>
          Assign spools to each toolhead for accurate filament tracking
          {showAutoMatch && (
            <div className="mt-1 text-xs text-pf-text-tertiary">
              File colors are matched to loaded spools with one spool suggested per tool where possible.
            </div>
          )}
        </div>
        {showAutoMatch && (
          <Button
            variant="secondary"
            size="sm"
            onClick={handleApplyAllSuggestions}
            disabled={applicableSuggestionCount === 0 || setSpoolMutation.isPending || clearSpoolMutation.isPending}
            aria-label={`Auto-match all available suggestions, ${applicableSuggestionCount} unapplied`}
          >
            Auto-match all
          </Button>
        )}
      </div>

      {toolheads.map((toolhead) => {
        const isMmuGate = String(toolhead.toolheadType) === 'MmuGate';
        const hasAssignment = toolhead.currentSpoolId != null;
        const suggestion = suggestionsByToolhead.get(toolhead.index);
        const suggestedSpoolId = suggestion?.spoolId;
        const hasSuggestion = suggestedSpoolId != null;
        const suggestionApplied = hasSuggestion && suggestedSpoolId === toolhead.currentSpoolId;
        const isManualOverride = manualOverrideIndexes.has(toolhead.index);
        const targetColorHex = targetFilamentColorHex?.[toolhead.index];
        const targetMaterial = targetFilamentType?.[toolhead.index];

        return (
          <Card key={toolhead.index}>
            <Card.Body className="flex flex-col gap-3 py-3">
              <div className="flex items-center gap-4">
              {/* Toolhead Label & Type Badge */}
              <div className="flex items-center gap-2 min-w-[100px]">
                <span className="font-mono text-pf-text-primary font-medium">T{toolhead.index}</span>
                <Badge
                  variant={isMmuGate ? 'primary' : 'default'}
                  size="sm"
                >
                  {isMmuGate ? 'Gate' : 'Tool'}
                </Badge>
              </div>

              {/* Current Spool Info */}
              <div className="flex-1 flex items-center gap-3">
                {hasAssignment ? (
                  <>
                    {/* Filament Color Swatch */}
                    {toolhead.currentFilamentColor && (
                      <div
                        className="w-5 h-5 rounded-full border-2 border-pf-border shrink-0"
                        style={{ backgroundColor: toolhead.currentFilamentColor }}
                        title={toolhead.currentFilamentColor}
                      />
                    )}
                    {/* Material & Spool ID */}
                    <div className="flex flex-col">
                      <span className="text-sm text-pf-text-primary">
                        {toolhead.currentMaterial || 'Unknown Material'}
                      </span>
                      <span className="text-xs text-pf-text-tertiary">
                        Spool #{toolhead.currentSpoolId}
                      </span>
                    </div>
                  </>
                ) : (
                  <span className="text-sm text-pf-text-tertiary italic">No spool assigned</span>
                )}
              </div>

              {/* Action Buttons */}
              <div className="flex items-center gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => handleOpenPicker(toolhead.index)}
                  disabled={setSpoolMutation.isPending || clearSpoolMutation.isPending}
                >
                  {hasAssignment ? 'Change' : 'Assign'}
                </Button>
                {hasAssignment && (
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => handleClearSpool(toolhead.index)}
                    disabled={setSpoolMutation.isPending || clearSpoolMutation.isPending}
                  >
                    Clear
                  </Button>
                )}
              </div>
              </div>

              {showAutoMatch && (
                <div className="rounded-lg border border-pf-border/70 bg-pf-bg-0 px-3 py-2 text-xs">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium text-pf-text-secondary">File target:</span>
                    {targetColorHex ? (
                      <span className="inline-flex items-center gap-1.5 text-pf-text-primary">
                        <span
                          className="h-3.5 w-3.5 rounded-full border border-pf-border"
                          style={{ backgroundColor: targetColorHex }}
                          role="img"
                          aria-label={`File color ${targetColorHex}`}
                        />
                        {targetColorHex}
                      </span>
                    ) : (
                      <span className="text-pf-text-tertiary">No usable file color</span>
                    )}
                    {targetMaterial && (
                      <Badge variant="default" size="sm">{targetMaterial}</Badge>
                    )}
                  </div>

                  <div className="mt-2 flex flex-wrap items-center gap-2">
                    {suggestion && suggestedSpoolId != null ? (
                      <>
                        <span className="font-medium text-pf-text-secondary">
                          Suggested spool #{suggestedSpoolId}
                        </span>
                        <Badge
                          variant={suggestion.materialMismatch ? 'warning' : confidenceVariant(suggestion.confidence)}
                          size="sm"
                        >
                          {suggestion.materialMismatch ? 'Material mismatch' : confidenceLabel(suggestion.confidence)}
                        </Badge>
                        <span className="text-pf-text-tertiary">
                          ΔE {formatDeltaE(suggestion.deltaE)}
                        </span>
                        {suggestion.materialMismatch && (
                          <span className="inline-flex items-center gap-1 text-pf-warning-text">
                            <AlertCircleIcon className="h-3.5 w-3.5" ariaLabel="Material mismatch warning" />
                            Expected {suggestion.targetMaterial}, loaded {suggestion.spoolMaterial}
                          </span>
                        )}
                        {suggestionApplied ? (
                          <span className="inline-flex items-center gap-1 text-pf-success-text">
                            <CheckCircleIcon className="h-3.5 w-3.5" ariaLabel="Suggestion applied" />
                            Applied
                          </span>
                        ) : (
                          <Button
                            variant="subtle"
                            size="sm"
                            onClick={() => handleApplySuggestion(toolhead.index, suggestedSpoolId)}
                            disabled={setSpoolMutation.isPending || clearSpoolMutation.isPending}
                            aria-label={`Apply suggested spool ${suggestedSpoolId} to tool ${toolhead.index}`}
                          >
                            Apply suggestion
                          </Button>
                        )}
                        {isManualOverride && (
                          <Badge variant="info" size="sm">Manual override</Badge>
                        )}
                      </>
                    ) : (
                      <span className="text-pf-text-tertiary">No loaded spool has a usable color for this file tool.</span>
                    )}
                  </div>
                </div>
              )}
            </Card.Body>
          </Card>
        );
      })}

      {/* Spool Picker Modal */}
      {selectedToolheadIndex !== null && activeToolhead && (
        <SpoolPickerModal
          isOpen={true}
          onClose={handleClosePicker}
          onSelect={handleSpoolSelect}
          printerId={printerId}
          activeSpoolId={activeToolhead.currentSpoolId}
        />
      )}
    </div>
  );
}
