import { useState, useCallback } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Spinner } from '@/common/components/ui/Spinner';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { SpoolmanSpool } from '@/types/api';
import { AlertCircleIcon, FilamentLoadIcon } from '@/common/components/icons/MdiIcons';
import type { SpoolValidationContext } from '@/features/queue/utils/spoolValidation';
import { detectSpoolIssue } from '@/features/queue/utils/spoolValidation';

interface SpoolValidationModalProps {
  isOpen: boolean;
  onClose: () => void;
  /** Called when validation passes and dispatch should proceed */
  onProceed: (jobId: string) => void;
  context: SpoolValidationContext | null;
}

/**
 * Modal shown before dispatch when the target printer has spool issues.
 * - No spool loaded: forces user to select one before continuing
 * - Material mismatch: warns and lets user override or pick a different spool
 *
 * Setting the spool via apiClient.setActiveSpool() syncs to Moonraker automatically.
 */
export function SpoolValidationModal({
  isOpen,
  onClose,
  onProceed,
  context,
}: SpoolValidationModalProps) {
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
  const [settingSpool, setSettingSpool] = useState(false);

  const issue = context ? detectSpoolIssue(context) : null;

  const handleSelectSpool = useCallback(
    async (spoolId: number, spool: SpoolmanSpool) => {
      if (!context) return;

      setSettingSpool(true);
      try {
        await apiClient.setActiveSpool(context.printerId, spoolId);
        toast.success(`Spool "${spool.filamentName || spool.name || `#${spoolId}`}" set on ${context.printerName}`);
        setShowSpoolPicker(false);
        // Spool is now set — proceed with dispatch
        onProceed(context.jobId);
      } catch (err) {
        toast.error(`Failed to set spool: ${err instanceof Error ? err.message : 'Unknown error'}`);
      } finally {
        setSettingSpool(false);
      }
    },
    [context, onProceed],
  );

  const handleProceedAnyway = useCallback(() => {
    if (!context) return;
    onProceed(context.jobId);
  }, [context, onProceed]);

  if (!context || !issue) return null;

  if (showSpoolPicker) {
    return (
      <SpoolPickerModal
        isOpen
        onClose={() => setShowSpoolPicker(false)}
        onSelect={handleSelectSpool}
        printerId={context.printerId}
        activeSpoolId={context.spoolInfo?.activeSpoolId}
      />
    );
  }

  const isNoSpool = issue === 'no-spool';

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={isNoSpool ? 'No Spool Loaded' : 'Material Mismatch'}
      size="md"
      footer={
        <div className="flex gap-2 justify-end">
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          {!isNoSpool && (
            <Button
              variant="ghost"
              onClick={handleProceedAnyway}
              disabled={settingSpool}
            >
              Print Anyway
            </Button>
          )}
          <Button
            variant="primary"
            onClick={() => setShowSpoolPicker(true)}
            disabled={settingSpool}
            iconLeft={settingSpool ? <Spinner size="sm" /> : undefined}
          >
            Select Spool
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        {isNoSpool ? (
          <>
            <div className="flex items-start gap-3 rounded-lg bg-pf-error/10 p-4">
              <AlertCircleIcon className="h-6 w-6 shrink-0 text-pf-error mt-0.5" />
              <div>
                <p className="font-medium text-pf-text-primary">
                  <strong>{context.printerName}</strong> has no spool loaded.
                </p>
                <p className="mt-1 text-sm text-pf-text-secondary">
                  A spool must be selected before printing so filament usage and costs can be tracked accurately.
                  {context.requiredMaterial && (
                    <> This job requires <strong>{context.requiredMaterial}</strong>.</>
                  )}
                </p>
              </div>
            </div>
          </>
        ) : (
          <>
            <div className="flex items-start gap-3 rounded-lg bg-pf-warning/10 p-4">
              <AlertCircleIcon className="h-6 w-6 shrink-0 text-pf-warning mt-0.5" />
              <div>
                <p className="font-medium text-pf-text-primary">
                  Material mismatch on <strong>{context.printerName}</strong>
                </p>
                <p className="mt-1 text-sm text-pf-text-secondary">
                  The loaded spool is <strong>{context.spoolInfo?.material}</strong>
                  {context.spoolInfo?.filamentName && (
                    <> ({context.spoolInfo.filamentName})</>
                  )}
                  , but this job requires <strong>{context.requiredMaterial}</strong>.
                </p>
              </div>
            </div>
            <div className="rounded-lg border border-pf-border p-3">
              <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
                <FilamentLoadIcon className="h-4 w-4" />
                <span>Currently loaded:</span>
                <span className="font-medium text-pf-text-primary">
                  {context.spoolInfo?.filamentName || context.spoolInfo?.material || 'Unknown'}
                </span>
                {context.spoolInfo?.colorHex && (
                  <span
                    className="inline-block h-3 w-3 rounded-full border border-white/20"
                    style={{ backgroundColor: context.spoolInfo.colorHex }}
                  />
                )}
              </div>
            </div>
          </>
        )}

        <p className="text-xs text-pf-text-tertiary">
          Selecting a spool will update both PrintFarmer and the printer&apos;s firmware (Moonraker/Spoolman).
        </p>
      </div>
    </Modal>
  );
}
