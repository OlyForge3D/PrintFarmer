import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Button, Checkbox, Input, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useCreatePart, useUpdatePart } from '../hooks/usePartsInventory';
import type {
  BinDto,
  CreatePartInventoryRequest,
  PartInventoryDto,
  UpdatePartInventoryRequest,
} from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';

export interface PartFormModalProps {
  isOpen: boolean;
  onClose: () => void;
  /** When present the modal edits this part; otherwise it creates a new SKU. */
  part?: PartInventoryDto | null;
  /** Currently registered bins for the default-bin selector. */
  bins: BinDto[];
}

/**
 * PartFormModal — create or edit a printed-part SKU. Note that on-hand
 * count is never edited here; use {@link AdjustStockModal} for stock
 * changes, which routes through the adjustment ledger.
 */
export function PartFormModal({ isOpen, onClose, part, bins }: PartFormModalProps) {
  const isEdit = Boolean(part);
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={isEdit ? `Edit SKU: ${part?.sku}` : 'Add printed part SKU'}
      size="lg"
    >
      {/* key forces fresh state on open / target change */}
      <PartFormBody
        key={`${part?.id ?? 'new'}-${isOpen}`}
        part={part ?? null}
        bins={bins}
        onClose={onClose}
      />
    </Modal>
  );
}

interface PartFormBodyProps {
  part: PartInventoryDto | null;
  bins: BinDto[];
  onClose: () => void;
}

function PartFormBody({ part, bins, onClose }: PartFormBodyProps) {
  const isEdit = Boolean(part);
  const createPart = useCreatePart();
  const updatePart = useUpdatePart();

  const [sku, setSku] = useState(part?.sku ?? '');
  const [name, setName] = useState(part?.name ?? '');
  const [description, setDescription] = useState(part?.description ?? '');
  const [modelFileRef, setModelFileRef] = useState(part?.modelFileRef ?? '');
  const [defaultBinCode, setDefaultBinCode] = useState(part?.defaultBinCode ?? '');
  const [reorderPoint, setReorderPoint] = useState(String(part?.reorderPoint ?? 0));
  const [initialOnHand, setInitialOnHand] = useState('0');
  const [isActive, setIsActive] = useState(part?.isActive ?? true);

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    const trimmedSku = sku.trim();
    const trimmedName = name.trim();
    if (!trimmedSku) {
      toast.error('SKU is required');
      return;
    }
    if (!trimmedName) {
      toast.error('Name is required');
      return;
    }
    const reorderPointNum = Number(reorderPoint);
    if (!Number.isFinite(reorderPointNum) || reorderPointNum < 0 || !Number.isInteger(reorderPointNum)) {
      toast.error('Reorder point must be a non-negative whole number');
      return;
    }
    try {
      if (isEdit && part) {
        const request: UpdatePartInventoryRequest = {
          name: trimmedName,
          description: description.trim() || null,
          modelFileRef: modelFileRef.trim() || null,
          defaultBinCode: defaultBinCode.trim() || null,
          reorderPoint: reorderPointNum,
          isActive,
        };
        await updatePart.mutateAsync({ sku: part.sku, request });
        toast.success('SKU updated');
      } else {
        const initialOnHandNum = Number(initialOnHand);
        if (!Number.isFinite(initialOnHandNum) || initialOnHandNum < 0 || !Number.isInteger(initialOnHandNum)) {
          toast.error('Initial on-hand must be a non-negative whole number');
          return;
        }
        const request: CreatePartInventoryRequest = {
          sku: trimmedSku,
          name: trimmedName,
          description: description.trim() || null,
          modelFileRef: modelFileRef.trim() || null,
          defaultBinCode: defaultBinCode.trim() || null,
          initialOnHand: initialOnHandNum,
          reorderPoint: reorderPointNum,
        };
        await createPart.mutateAsync(request);
        toast.success('SKU created');
      }
      onClose();
    } catch (error) {
      toast.error(getErrorMessage(error, 'Failed to save SKU'));
    }
  };

  const isSaving = createPart.isPending || updatePart.isPending;

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor="part-sku" className="block text-sm font-medium text-pf-text-secondary mb-1">
            SKU <span className="text-pf-error">*</span>
          </label>
          <Input
            id="part-sku"
            value={sku}
            onChange={(event) => setSku(event.target.value)}
            placeholder="BRACKET-01"
            required
            maxLength={64}
            disabled={isEdit}
            aria-describedby="part-sku-help"
          />
          <p id="part-sku-help" className="text-xs text-pf-text-secondary mt-1">
            Unique identifier. Cannot be changed after creation.
          </p>
        </div>
        <div>
          <label htmlFor="part-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Name <span className="text-pf-error">*</span>
          </label>
          <Input
            id="part-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Corner bracket"
            required
            maxLength={200}
          />
        </div>
      </div>

      <div>
        <label htmlFor="part-desc" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Description
        </label>
        <Input
          id="part-desc"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          placeholder="Optional notes"
          maxLength={2000}
        />
      </div>

      <div>
        <label htmlFor="part-model-ref" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Model file reference
        </label>
        <Input
          id="part-model-ref"
          value={modelFileRef}
          onChange={(event) => setModelFileRef(event.target.value)}
          placeholder="Optional — e.g. STL path or file ID"
          maxLength={500}
        />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor="part-bin" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Default bin
          </label>
          <Select
            id="part-bin"
            value={defaultBinCode}
            onChange={(event) => setDefaultBinCode(event.target.value)}
            aria-describedby="part-bin-help"
          >
            <option value="">— No default bin —</option>
            {bins
              .filter((bin) => bin.isActive || bin.code === (part?.defaultBinCode ?? ''))
              .map((bin) => (
                <option key={bin.code} value={bin.code}>
                  {bin.code} — {bin.name}
                  {bin.location ? ` (${bin.location})` : ''}
                </option>
              ))}
          </Select>
          <p id="part-bin-help" className="text-xs text-pf-text-secondary mt-1">
            Harvested parts flow into this bin unless scanned elsewhere.
          </p>
        </div>
        {isEdit && (
          <div className="flex items-end">
            <Checkbox
              checked={isActive}
              onChange={(event) => setIsActive(event.target.checked)}
              label="Active"
            />
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor="part-reorder-pt" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Reorder point
          </label>
          <Input
            id="part-reorder-pt"
            type="number"
            min="0"
            step="1"
            value={reorderPoint}
            onChange={(event) => setReorderPoint(event.target.value)}
          />
        </div>
        {!isEdit && (
          <div>
            <label htmlFor="part-initial" className="block text-sm font-medium text-pf-text-secondary mb-1">
              Initial on-hand
            </label>
            <Input
              id="part-initial"
              type="number"
              min="0"
              step="1"
              value={initialOnHand}
              onChange={(event) => setInitialOnHand(event.target.value)}
              aria-describedby="part-initial-help"
            />
            <p id="part-initial-help" className="text-xs text-pf-text-secondary mt-1">
              Seeds the ledger. Later changes go through Adjust stock.
            </p>
          </div>
        )}
      </div>

      {isEdit && (
        <p className="text-xs text-pf-text-secondary" role="note">
          On-hand quantity is managed through the adjustment ledger. Use{' '}
          <span className="font-medium">Adjust stock</span> instead of editing this record.
        </p>
      )}

      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={isSaving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" size="sm" loading={isSaving}>
          {isEdit ? 'Save changes' : 'Create SKU'}
        </Button>
      </div>
    </form>
  );
}
