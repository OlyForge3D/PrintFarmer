import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import clsx from 'clsx';
import { toast } from 'sonner';
import { Button } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { queryKeys } from '@/common/hooks/useApi';
import type { ToolheadDto, SpoolmanSpool } from '@/types/api';
import { SpoolPicker } from './SpoolPicker';

export interface SlotPopoverProps {
  toolhead: ToolheadDto;
  printerId: string;
  onClose: () => void;
}

export function SlotPopover({ toolhead, printerId, onClose }: SlotPopoverProps) {
  const [showPicker, setShowPicker] = useState(false);
  const queryClient = useQueryClient();

  const hasFilament = toolhead.currentSpoolId != null || toolhead.currentMaterial != null;

  const invalidateQueries = () => {
    queryClient.invalidateQueries({ queryKey: queryKeys.printerDetails(printerId) });
    queryClient.invalidateQueries({ queryKey: queryKeys.printer(printerId) });
  };

  const assignMutation = useMutation({
    mutationFn: (spoolId: number) =>
      apiClient.setToolheadSpool(printerId, toolhead.index, spoolId),
    onSuccess: () => {
      invalidateQueries();
      toast.success('Spool assigned');
      onClose();
    },
    onError: (err: Error) => toast.error(`Failed to assign spool: ${err.message}`),
  });

  const unassignMutation = useMutation({
    mutationFn: () =>
      apiClient.clearToolheadSpool(printerId, toolhead.index),
    onSuccess: () => {
      invalidateQueries();
      toast.success('Spool unassigned');
      onClose();
    },
    onError: (err: Error) => toast.error(`Failed to unassign spool: ${err.message}`),
  });

  const isPending = assignMutation.isPending || unassignMutation.isPending;

  const handleSelect = (spool: SpoolmanSpool) => {
    assignMutation.mutate(spool.id);
  };

  return (
    <div
      className={clsx(
        'absolute z-50 top-full left-1/2 -translate-x-1/2 mt-2',
        'bg-pf-surface-elevated border border-pf-border rounded-lg shadow-lg',
        showPicker ? 'w-64' : 'w-52',
        'animate-in fade-in-0 zoom-in-95 duration-150',
      )}
      role="dialog"
      aria-label={`Slot ${toolhead.index} actions`}
    >
      {/* Arrow */}
      <div className="absolute -top-1.5 left-1/2 -translate-x-1/2 w-3 h-3 rotate-45 bg-pf-surface-elevated border-l border-t border-pf-border" />

      <div className="relative p-3 space-y-2">
        {/* Current spool info */}
        {hasFilament ? (
          <div className="space-y-1">
            <div className="flex items-center gap-2">
              {toolhead.currentFilamentColor && (
                <span
                  className="w-4 h-4 rounded-full shrink-0 border border-pf-border"
                  style={{ backgroundColor: toolhead.currentFilamentColor }}
                />
              )}
              <span className="text-sm font-medium text-pf-text-primary truncate">
                {toolhead.currentMaterial || 'Loaded'}
              </span>
            </div>
            {toolhead.name && (
              <div className="text-xs text-pf-text-secondary">{toolhead.name}</div>
            )}
            {toolhead.currentSpoolId != null && (
              <div className="text-xs text-pf-text-tertiary">Spool #{toolhead.currentSpoolId}</div>
            )}
          </div>
        ) : (
          <div className="text-xs text-pf-text-tertiary">No spool assigned</div>
        )}

        {/* Actions or spool picker */}
        {showPicker ? (
          <div className="space-y-2 border-t border-pf-border pt-2">
            <div className="flex items-center justify-between">
              <span className="text-xs font-medium text-pf-text-secondary">
                {hasFilament ? 'Reassign Spool' : 'Assign Spool'}
              </span>
              <Button variant="ghost" size="sm" onClick={() => setShowPicker(false)} disabled={isPending}>
                ← Back
              </Button>
            </div>
            <SpoolPicker
              printerId={printerId}
              supportedMaterials={toolhead.supportedMaterials}
              currentSpoolId={toolhead.currentSpoolId}
              onSelect={handleSelect}
              disabled={isPending}
            />
          </div>
        ) : (
          <div className="flex gap-1.5 border-t border-pf-border pt-2">
            <Button
              variant={hasFilament ? 'secondary' : 'primary'}
              size="sm"
              onClick={() => setShowPicker(true)}
              disabled={isPending}
              className="flex-1"
            >
              {hasFilament ? 'Reassign' : 'Assign'}
            </Button>
            {hasFilament && (
              <Button
                variant="danger"
                size="sm"
                onClick={() => unassignMutation.mutate()}
                loading={unassignMutation.isPending}
                disabled={isPending}
              >
                Unassign
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
