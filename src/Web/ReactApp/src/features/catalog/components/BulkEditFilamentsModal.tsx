import { useState, useId } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, Input, Textarea, FormField } from '@/common/components/ui';
import { useSpoolmanVendors, useBulkUpdateFilaments, useSpoolmanMaterials } from '@/common/hooks/useApi';
import type { SpoolmanBulkUpdateFilamentsRequest, SpoolmanBulkUpdateResult } from '@/types/api';
import { toast } from 'sonner';

interface BulkEditFilamentsModalProps {
  isOpen: boolean;
  onClose: () => void;
  selectedIds: number[];
  onSuccess: () => void;
}

/**
 * Modal for bulk-editing selected Spoolman filaments.
 * Only fields that are explicitly set (non-empty) are sent in the update.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export function BulkEditFilamentsModal({ isOpen, onClose, selectedIds, onSuccess }: BulkEditFilamentsModalProps) {
  const formId = useId();
  const { data: vendors = [], isLoading: vendorsLoading } = useSpoolmanVendors({ enabled: isOpen });
  const { data: materials = [], isLoading: materialsLoading } = useSpoolmanMaterials({ enabled: isOpen });
  const bulkUpdate = useBulkUpdateFilaments();

  const [vendorId, setVendorId] = useState('');
  const [material, setMaterial] = useState('');
  const [price, setPrice] = useState('');
  const [extruderTemp, setExtruderTemp] = useState('');
  const [bedTemp, setBedTemp] = useState('');
  const [comment, setComment] = useState('');

  const resetForm = () => {
    setVendorId('');
    setMaterial('');
    setPrice('');
    setExtruderTemp('');
    setBedTemp('');
    setComment('');
  };

  const handleSubmit = async () => {
    const request: SpoolmanBulkUpdateFilamentsRequest = {
      filamentIds: selectedIds,
    };

    // Only include fields the user explicitly set
    if (vendorId) request.vendorId = Number(vendorId);
    if (material.trim()) request.material = material.trim();
    if (price) request.price = Number(price);
    if (extruderTemp) request.settingsExtruderTemp = Number(extruderTemp);
    if (bedTemp) request.settingsBedTemp = Number(bedTemp);
    if (comment.trim()) request.comment = comment.trim();

    const hasChanges = vendorId || material.trim() || price || extruderTemp || bedTemp || comment.trim();
    if (!hasChanges) {
      toast.error('No fields set. Select at least one field to update.');
      return;
    }

    try {
      const result: SpoolmanBulkUpdateResult = await bulkUpdate.mutateAsync(request);
      if (result.errorCount > 0) {
        toast.warning(`Updated ${result.updatedCount}, ${result.errorCount} errors: ${result.errors.slice(0, 3).join('; ')}`);
      } else {
        toast.success(`Successfully updated ${result.updatedCount} filament${result.updatedCount !== 1 ? 's' : ''}.`);
      }
      resetForm();
      onSuccess();
      onClose();
    } catch {
      toast.error('Bulk update failed.');
    }
  };

  const handleClose = () => {
    resetForm();
    onClose();
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={`Bulk Edit ${selectedIds.length} Filament${selectedIds.length !== 1 ? 's' : ''}`}
      size="md"
      closeOnEscape
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="secondary" size="sm" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={handleSubmit}
            disabled={bulkUpdate.isPending}
          >
            {bulkUpdate.isPending ? 'Updating...' : 'Apply Changes'}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm">
        <p className="text-pf-text-secondary">
          Only fields you fill in below will be applied. Leave a field blank to keep existing values.
        </p>

        <FormField label="Vendor" htmlFor={`${formId}-vendor`}>
          <Select
            id={`${formId}-vendor`}
            aria-label="Bulk update vendor"
            value={vendorId}
            onChange={e => setVendorId(e.target.value)}
            disabled={vendorsLoading}
          >
            <option value="">— No change —</option>
            {[...vendors].sort((a, b) => a.name.localeCompare(b.name)).map(v => (
              <option key={v.id} value={v.id}>{v.name}</option>
            ))}
          </Select>
        </FormField>

        <FormField label="Material" htmlFor={`${formId}-material`}>
          <Select
            id={`${formId}-material`}
            value={material}
            onChange={e => setMaterial(e.target.value)}
            disabled={materialsLoading}
            aria-label="Bulk update material"
          >
            <option value="">— No change —</option>
            {[...materials].sort((a, b) => a.name.localeCompare(b.name)).map(m => (
              <option key={m.id} value={m.name}>{m.name}</option>
            ))}
          </Select>
        </FormField>

        <FormField label="Price" htmlFor={`${formId}-price`}>
          <Input
            id={`${formId}-price`}
            type="number"
            step="0.01"
            min="0"
            value={price}
            onChange={e => setPrice(e.target.value)}
            placeholder="e.g. 24.99"
            aria-label="Bulk update price"
          />
        </FormField>

        <div className="grid grid-cols-2 gap-4">
          <FormField label="Extruder Temp (°C)" htmlFor={`${formId}-extruder`}>
            <Input
              id={`${formId}-extruder`}
              type="number"
              min="0"
              max="500"
              value={extruderTemp}
              onChange={e => setExtruderTemp(e.target.value)}
              placeholder="e.g. 215"
              aria-label="Bulk update extruder temperature"
            />
          </FormField>
          <FormField label="Bed Temp (°C)" htmlFor={`${formId}-bed`}>
            <Input
              id={`${formId}-bed`}
              type="number"
              min="0"
              max="200"
              value={bedTemp}
              onChange={e => setBedTemp(e.target.value)}
              placeholder="e.g. 60"
              aria-label="Bulk update bed temperature"
            />
          </FormField>
        </div>

        <FormField label="Comment" htmlFor={`${formId}-comment`}>
          <Textarea
            id={`${formId}-comment`}
            value={comment}
            onChange={e => setComment(e.target.value)}
            placeholder="Optional note..."
            rows={2}
            className="resize-none"
            aria-label="Bulk update comment"
          />
        </FormField>
      </div>
    </Modal>
  );
}
