import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Alert, Badge, Button, Input, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useAdjustPartStock } from '../hooks/usePartsInventory';
import {
  PART_ADJUSTMENT_REASONS,
  PART_ADJUSTMENT_REASON_LABELS,
} from '@/types/partsInventory';
import type {
  AdjustPartInventoryRequest,
  BinDto,
  PartAdjustmentReason,
  PartInventoryDto,
} from '@/types/partsInventory';
import { getErrorMessage, isWrongBinError } from '../utils/problemDetails';

export interface AdjustStockModalProps {
  isOpen: boolean;
  onClose: () => void;
  part: PartInventoryDto | null;
  bins: BinDto[];
}

/**
 * Adjust the on-hand quantity of a printed-part SKU through the ledger.
 *
 * The backend enforces the sign of the delta and prevents on-hand from
 * going below zero. We surface those errors as-is via toasts so operators
 * see the same reason string the ledger recorded.
 *
 * `operationKey` is generated per submission for idempotency across
 * accidental double-clicks or retries.
 */
export function AdjustStockModal({ isOpen, onClose, part, bins }: AdjustStockModalProps) {
  if (!part) return null;
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Adjust stock — ${part.sku}`}
      size="md"
    >
      {/* key forces a fresh mount each time the modal opens for a SKU, resetting form state */}
      <AdjustStockFormBody key={`${part.id}-${isOpen}`} part={part} bins={bins} onClose={onClose} />
    </Modal>
  );
}

interface AdjustStockFormBodyProps {
  part: PartInventoryDto;
  bins: BinDto[];
  onClose: () => void;
}

function AdjustStockFormBody({ part, bins, onClose }: AdjustStockFormBodyProps) {
  const adjust = useAdjustPartStock();
  const [reason, setReason] = useState<PartAdjustmentReason>('manual');
  const [delta, setDelta] = useState('1');
  const [binCode, setBinCode] = useState(part.defaultBinCode ?? '');
  const [notes, setNotes] = useState('');

  const deltaNum = Number(delta);
  const isValidDelta = Number.isFinite(deltaNum) && deltaNum !== 0 && Number.isInteger(deltaNum);
  const projectedOnHand = isValidDelta ? part.onHand + deltaNum : part.onHand;
  const willUnderflow = projectedOnHand < 0;

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!isValidDelta) {
      toast.error('Enter a non-zero whole number delta');
      return;
    }
    if (willUnderflow) {
      toast.error('Adjustment would take on-hand below zero');
      return;
    }
    const operationKey =
      typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
        ? crypto.randomUUID()
        : `adjust-${Date.now()}-${Math.random().toString(36).slice(2)}`;

    const request: AdjustPartInventoryRequest = {
      delta: deltaNum,
      reason,
      binCode: binCode.trim() || null,
      notes: notes.trim() || null,
      operationKey,
    };
    try {
      await adjust.mutateAsync({ sku: part.sku, request });
      toast.success(`Recorded ${deltaNum > 0 ? '+' : ''}${deltaNum} to ${part.sku}`);
      onClose();
    } catch (error) {
      if (isWrongBinError(error)) {
        toast.error('Scanned bin does not match the SKU’s default bin. Register or select the correct bin.');
      } else {
        toast.error(getErrorMessage(error, 'Failed to adjust stock'));
      }
    }
  };

  const isSaving = adjust.isPending;

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      <div className="flex items-center justify-between rounded border border-pf-border bg-pf-bg-2 px-3 py-2">
        <div>
          <div className="text-sm text-pf-text-secondary">Current on-hand</div>
          <div className="text-xl font-semibold text-pf-text">{part.onHand}</div>
        </div>
        <div className="text-right">
          <div className="text-sm text-pf-text-secondary">Projected</div>
          <div className={`text-xl font-semibold ${willUnderflow ? 'text-pf-error' : 'text-pf-text'}`}>
            {projectedOnHand}
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor="adj-delta" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Delta <span className="text-pf-error">*</span>
          </label>
          <Input
            id="adj-delta"
            type="number"
            step="1"
            value={delta}
            onChange={(event) => setDelta(event.target.value)}
            required
            aria-describedby="adj-delta-help"
            autoFocus
          />
          <p id="adj-delta-help" className="text-xs text-pf-text-secondary mt-1">
            Positive to add, negative to remove. Zero is not allowed.
          </p>
        </div>
        <div>
          <label htmlFor="adj-reason" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Reason <span className="text-pf-error">*</span>
          </label>
          <Select
            id="adj-reason"
            value={reason}
            onChange={(event) => setReason(event.target.value as PartAdjustmentReason)}
            required
          >
            {PART_ADJUSTMENT_REASONS.map((value) => (
              <option key={value} value={value}>
                {PART_ADJUSTMENT_REASON_LABELS[value]}
              </option>
            ))}
          </Select>
        </div>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor="adj-bin" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Bin
          </label>
          <Select
            id="adj-bin"
            value={binCode}
            onChange={(event) => setBinCode(event.target.value)}
          >
            <option value="">— Use default —</option>
            {bins
              .filter((bin) => bin.isActive || bin.code === binCode)
              .map((bin) => (
                <option key={bin.code} value={bin.code}>
                  {bin.code} — {bin.name}
                  {bin.location ? ` (${bin.location})` : ''}
                </option>
              ))}
          </Select>
          {part.defaultBinCode && (
            <p className="text-xs text-pf-text-secondary mt-1">
              Default: <Badge variant="default" size="sm">{part.defaultBinCode}</Badge>
            </p>
          )}
        </div>
        <div>
          <label htmlFor="adj-notes" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Notes
          </label>
          <Input
            id="adj-notes"
            value={notes}
            onChange={(event) => setNotes(event.target.value)}
            placeholder="Optional context"
            maxLength={500}
          />
        </div>
      </div>

      {willUnderflow && (
        <Alert type="error">
          This adjustment would take on-hand below zero. Reduce the delta.
        </Alert>
      )}

      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={isSaving}>
          Cancel
        </Button>
        <Button
          type="submit"
          variant="primary"
          size="sm"
          loading={isSaving}
          disabled={!isValidDelta || willUnderflow}
        >
          Record adjustment
        </Button>
      </div>
    </form>
  );
}

