import { useState } from 'react';
import { Badge, Button, Card } from '@/common/components/ui';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { useSetToolheadSpool, useClearToolheadSpool } from '@/common/hooks/useApi';
import type { ToolheadDto } from '@/types/api';

interface ToolheadSpoolPickerProps {
  printerId: string;
  toolheads: ToolheadDto[];
  onSpoolChange?: () => void;
}

/**
 * Multi-toolhead spool assignment interface.
 * Shows a list of toolheads with current spool assignments and allows changing/clearing them.
 */
export function ToolheadSpoolPicker({ printerId, toolheads, onSpoolChange }: ToolheadSpoolPickerProps) {
  const [selectedToolheadIndex, setSelectedToolheadIndex] = useState<number | null>(null);
  const setSpoolMutation = useSetToolheadSpool();
  const clearSpoolMutation = useClearToolheadSpool();

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

    handleClosePicker();
    onSpoolChange?.();
  };

  const handleClearSpool = (toolheadIndex: number) => {
    clearSpoolMutation.mutate({
      printerId,
      toolheadIndex,
    });
    onSpoolChange?.();
  };

  const activeToolhead = selectedToolheadIndex !== null ? toolheads[selectedToolheadIndex] : undefined;

  return (
    <div className="space-y-3">
      <div className="text-sm text-pf-text-secondary mb-2">
        Assign spools to each toolhead for accurate filament tracking
      </div>

      {toolheads.map((toolhead, index) => {
        const isMmuGate = String(toolhead.toolheadType) === 'MmuGate';
        const hasAssignment = toolhead.currentSpoolId != null;

        return (
          <Card key={index}>
            <Card.Body className="flex items-center gap-4 py-3">
              {/* Toolhead Label & Type Badge */}
              <div className="flex items-center gap-2 min-w-[100px]">
                <span className="font-mono text-pf-text-primary font-medium">T{index}</span>
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
                  onClick={() => handleOpenPicker(index)}
                  disabled={setSpoolMutation.isPending || clearSpoolMutation.isPending}
                >
                  {hasAssignment ? 'Change' : 'Assign'}
                </Button>
                {hasAssignment && (
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => handleClearSpool(index)}
                    disabled={setSpoolMutation.isPending || clearSpoolMutation.isPending}
                  >
                    Clear
                  </Button>
                )}
              </div>
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
