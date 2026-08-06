import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import type { FilamentCheckResult } from '@/types/api';

interface FilamentOverrideModalProps {
  isOpen: boolean;
  filamentCheck: FilamentCheckResult | null;
  isPending: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}

export function FilamentOverrideModal({
  isOpen,
  filamentCheck,
  isPending,
  onCancel,
  onConfirm,
}: FilamentOverrideModalProps) {
  if (!filamentCheck) return null;

  const isUnknown = filamentCheck.outcome === 'Unknown';
  const title = isUnknown
    ? 'Filament Verification Required'
    : 'Filament Mismatch';

  return (
    <Modal
      isOpen={isOpen}
      onClose={onCancel}
      title={title}
      size="md"
      footer={(
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onCancel} disabled={isPending}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={onConfirm}
            loading={isPending}
            disabled={isPending}
          >
            Confirm and Dispatch Anyway
          </Button>
        </div>
      )}
    >
      <div className="space-y-3">
        <p className="text-sm text-pf-text-primary">
          {filamentCheck.message ?? 'The filament check could not confirm compatibility.'}
        </p>
        {(filamentCheck.loadedMaterial || filamentCheck.requiredMaterial) && (
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 rounded-lg border border-pf-border p-3 text-sm">
            <dt className="text-pf-text-secondary">Loaded</dt>
            <dd className="font-medium text-pf-text-primary">
              {filamentCheck.loadedMaterial ?? 'Unknown'}
            </dd>
            <dt className="text-pf-text-secondary">Required</dt>
            <dd className="font-medium text-pf-text-primary">
              {filamentCheck.requiredMaterial ?? 'Unknown'}
            </dd>
          </dl>
        )}
        <p className="text-xs text-pf-warning">
          Dispatch will remain blocked unless you explicitly confirm this override.
        </p>
      </div>
    </Modal>
  );
}
