import { useMemo, useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Button, Input, Radio, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useCreateMapping } from '../hooks/usePartsInventory';
import type {
  CreatePartOutputMappingRequest,
  PartInventoryDto,
} from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';

export interface MappingFormModalProps {
  isOpen: boolean;
  onClose: () => void;
  parts: PartInventoryDto[];
  /**
   * Optional preselection for adding another SKU to an existing plate.
   * When set, the file source and IDs are pre-filled so operators can
   * quickly add multi-SKU mappings for the same source file.
   */
  presetSource?: {
    kind: 'gcode' | 'project';
    id: string;
    label: string;
  };
}

type SourceKind = 'gcode' | 'project';

/**
 * MappingFormModal — create a new project/G-code → SKU mapping. Exactly
 * one of `gcodeFileId` or `printProjectFileId` may be supplied to the
 * backend; this UI enforces that with a source-kind radio group.
 *
 * Multi-SKU plates are represented by multiple mappings sharing the same
 * source file. To ease that flow the parent may pass `presetSource` so
 * the operator only picks the additional SKU + copies.
 */
export function MappingFormModal({ isOpen, onClose, parts, presetSource }: MappingFormModalProps) {
  const titleHint = presetSource
    ? `Add SKU to plate: ${presetSource.label}`
    : 'Map job output to SKU';

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={titleHint} size="lg">
      {/* key forces fresh mount on open / preset change so state re-initializes */}
      <MappingFormBody
        key={`${presetSource?.kind ?? 'new'}-${presetSource?.id ?? ''}-${isOpen}`}
        parts={parts}
        presetSource={presetSource}
        onClose={onClose}
      />
    </Modal>
  );
}

interface MappingFormBodyProps {
  parts: PartInventoryDto[];
  presetSource?: MappingFormModalProps['presetSource'];
  onClose: () => void;
}

function MappingFormBody({ parts, presetSource, onClose }: MappingFormBodyProps) {
  const createMapping = useCreateMapping();

  const [partSku, setPartSku] = useState('');
  const [sourceKind, setSourceKind] = useState<SourceKind>(presetSource?.kind ?? 'gcode');
  const [sourceId, setSourceId] = useState(presetSource?.id ?? '');
  const [quantity, setQuantity] = useState('1');

  const activeParts = useMemo(() => parts.filter((part) => part.isActive), [parts]);
  const quantityNum = Number(quantity);
  const isValid =
    partSku.trim().length > 0 &&
    sourceId.trim().length > 0 &&
    Number.isFinite(quantityNum) &&
    quantityNum >= 1 &&
    Number.isInteger(quantityNum);

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!isValid) {
      toast.error('SKU, source file, and quantity (≥1) are required');
      return;
    }
    const request: CreatePartOutputMappingRequest = {
      sku: partSku.trim(),
      gcodeFileId: sourceKind === 'gcode' ? sourceId.trim() : null,
      printProjectFileId: sourceKind === 'project' ? sourceId.trim() : null,
      quantity: quantityNum,
    };
    try {
      await createMapping.mutateAsync(request);
      toast.success('Mapping created');
      onClose();
    } catch (error) {
      toast.error(getErrorMessage(error, 'Failed to create mapping'));
    }
  };

  const isSaving = createMapping.isPending;

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      <div>
        <label htmlFor="map-sku" className="block text-sm font-medium text-pf-text-secondary mb-1">
          SKU <span className="text-pf-error">*</span>
        </label>
        <Select
          id="map-sku"
          value={partSku}
          onChange={(event) => setPartSku(event.target.value)}
          required
        >
          <option value="">— Select SKU —</option>
          {activeParts.map((part) => (
            <option key={part.sku} value={part.sku}>
              {part.sku} — {part.name}
            </option>
          ))}
        </Select>
      </div>

      <fieldset className="space-y-2">
        <legend className="block text-sm font-medium text-pf-text-secondary">
          Source <span className="text-pf-error">*</span>
        </legend>
        <div className="flex gap-4" role="radiogroup" aria-label="Source file type">
          <Radio
            name="source-kind"
            value="gcode"
            checked={sourceKind === 'gcode'}
            onChange={() => setSourceKind('gcode')}
            disabled={Boolean(presetSource)}
            label="G-code file"
          />
          <Radio
            name="source-kind"
            value="project"
            checked={sourceKind === 'project'}
            onChange={() => setSourceKind('project')}
            disabled={Boolean(presetSource)}
            label="Print project file"
          />
        </div>
      </fieldset>

      <div>
        <label htmlFor="map-source-id" className="block text-sm font-medium text-pf-text-secondary mb-1">
          {sourceKind === 'gcode' ? 'G-code file ID' : 'Print project file ID'}{' '}
          <span className="text-pf-error">*</span>
        </label>
        <Input
          id="map-source-id"
          value={sourceId}
          onChange={(event) => setSourceId(event.target.value)}
          required
          placeholder="UUID"
          disabled={Boolean(presetSource)}
          aria-describedby="map-source-help"
        />
        <p id="map-source-help" className="text-xs text-pf-text-secondary mt-1">
          Paste the file ID from the project or G-code library page.
        </p>
      </div>

      <div>
        <label htmlFor="map-copies" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Quantity per print <span className="text-pf-error">*</span>
        </label>
        <Input
          id="map-copies"
          type="number"
          min="1"
          step="1"
          value={quantity}
          onChange={(event) => setQuantity(event.target.value)}
          required
          aria-describedby="map-copies-help"
        />
        <p id="map-copies-help" className="text-xs text-pf-text-secondary mt-1">
          How many of this SKU come off one successful print of the source file.
          For multi-SKU plates add another mapping for each distinct SKU.
        </p>
      </div>

      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={isSaving}>
          Cancel
        </Button>
        <Button
          type="submit"
          variant="primary"
          size="sm"
          loading={isSaving}
          disabled={!isValid}
        >
          Create mapping
        </Button>
      </div>
    </form>
  );
}

