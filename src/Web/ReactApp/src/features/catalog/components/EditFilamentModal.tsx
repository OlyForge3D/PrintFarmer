import { useState, useMemo, useId } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, Input, Textarea, FormField, ColorPicker } from '@/common/components/ui';
import { useSpoolmanVendors, useUpdateFilament, useSpoolmanMaterials } from '@/common/hooks/useApi';
import type { SpoolmanFilament, SpoolmanUpdateFilamentRequest } from '@/types/api';
import { toast } from 'sonner';

interface EditFilamentModalProps {
  isOpen: boolean;
  onClose: () => void;
  filament: SpoolmanFilament | null;
  onSuccess: () => void;
}

/**
 * Modal for editing a single Spoolman filament's properties.
 * Pre-populates fields from the filament being edited.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export function EditFilamentModal({ isOpen, onClose, filament, onSuccess }: EditFilamentModalProps) {
  // Key-based remount: when the filament changes the inner form re-mounts with fresh state
  return (
    <EditFilamentFormModal
      key={filament?.id ?? 'none'}
      isOpen={isOpen}
      onClose={onClose}
      filament={filament}
      onSuccess={onSuccess}
    />
  );
}

function EditFilamentFormModal({ isOpen, onClose, filament, onSuccess }: EditFilamentModalProps) {
  const formId = useId();
  const htmlFormId = `edit-filament-form-${filament?.id ?? 'none'}`;
  const { data: vendors = [], isLoading: vendorsLoading } = useSpoolmanVendors({ enabled: isOpen && filament !== null });
  const { data: materials = [], isLoading: materialsLoading } = useSpoolmanMaterials({ enabled: isOpen && filament !== null });
  const updateMutation = useUpdateFilament();

  // Resolve initial vendor ID from vendor name
  const resolvedVendorId = useMemo(() => {
    if (!filament?.vendor || vendors.length === 0) return '';
    const match = vendors.find(v => v.name === filament.vendor);
    return match ? String(match.id) : '';
  }, [filament, vendors]);

  const [name, setName] = useState(filament?.name ?? '');
  const [vendorId, setVendorId] = useState('');
  const [vendorTouched, setVendorTouched] = useState(false);
  const [material, setMaterial] = useState(filament?.material ?? '');
  const [price, setPrice] = useState(filament?.price != null ? String(filament.price) : '');
  const [extruderTemp, setExtruderTemp] = useState(filament?.settingsExtruderTemp != null ? String(filament.settingsExtruderTemp) : '');
  const [bedTemp, setBedTemp] = useState(filament?.settingsBedTemp != null ? String(filament.settingsBedTemp) : '');
  const [colorHex, setColorHex] = useState(filament?.colorHex ?? '');
  const [density, setDensity] = useState(filament?.density != null ? String(filament.density) : '');
  const [diameter, setDiameter] = useState(filament?.diameter != null ? String(filament.diameter) : '');
  const [weight, setWeight] = useState(filament?.weight != null ? String(filament.weight) : '');
  const [spoolWeight, setSpoolWeight] = useState(filament?.spoolWeight != null ? String(filament.spoolWeight) : '');
  const [articleNumber, setArticleNumber] = useState(filament?.articleNumber ?? '');
  const [comment, setComment] = useState(filament?.comment ?? '');

  // Use resolved vendor ID until user manually changes it
  const effectiveVendorId = vendorTouched ? vendorId : resolvedVendorId;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!filament) return;

    const request: SpoolmanUpdateFilamentRequest = {};

    if (name.trim()) request.name = name.trim();
    if (effectiveVendorId) request.vendorId = Number(effectiveVendorId);
    if (material.trim()) request.material = material.trim();
    if (price) request.price = Number(price);
    if (extruderTemp) request.settingsExtruderTemp = Number(extruderTemp);
    if (bedTemp) request.settingsBedTemp = Number(bedTemp);
    if (colorHex.trim()) request.colorHex = colorHex.trim();
    if (density) request.density = Number(density);
    if (diameter) request.diameter = Number(diameter);
    if (weight) request.weight = Number(weight);
    if (spoolWeight) request.spoolWeight = Number(spoolWeight);
    if (articleNumber.trim()) request.articleNumber = articleNumber.trim();
    if (comment.trim()) request.comment = comment.trim();

    try {
      await updateMutation.mutateAsync({ id: filament.id, request });
      toast.success(`Filament "${filament.name}" updated.`);
      onSuccess();
      onClose();
    } catch {
      toast.error('Failed to update filament.');
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Edit Filament: ${filament?.name ?? 'Unknown'}`}
      width="max-w-2xl"
      closeOnEscape
      footer={
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            type="submit"
            form={htmlFormId}
            disabled={updateMutation.isPending}
          >
            {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
          </Button>
        </div>
      }
    >
      {filament && (
        <form id={htmlFormId} onSubmit={handleSubmit} className="space-y-4 text-sm">
          <div className="grid grid-cols-2 gap-4">
            <FormField label="Name" htmlFor={`${formId}-name`}>
              <Input
                id={`${formId}-name`}
                type="text"
                value={name}
                onChange={e => setName(e.target.value)}
                placeholder="Filament name"
                aria-label="Filament name"
              />
            </FormField>
            <FormField label="Vendor" htmlFor={`${formId}-vendor`}>
              <Select
                id={`${formId}-vendor`}
                aria-label="Vendor"
                value={effectiveVendorId}
                onChange={e => { setVendorTouched(true); setVendorId(e.target.value); }}
                disabled={vendorsLoading}
              >
                <option value="">— Select vendor —</option>
                {[...vendors].sort((a, b) => a.name.localeCompare(b.name)).map(v => (
                  <option key={v.id} value={v.id}>{v.name}</option>
                ))}
              </Select>
            </FormField>
          </div>

          <div className="grid grid-cols-3 gap-4">
            <FormField label="Material" htmlFor={`${formId}-material`}>
              <Select
                id={`${formId}-material`}
                value={material}
                onChange={e => setMaterial(e.target.value)}
                disabled={materialsLoading}
                aria-label="Material"
              >
                <option value="">— Select material —</option>
                {[...materials].sort((a, b) => a.name.localeCompare(b.name)).map(m => (
                  <option key={m.id} value={m.name}>{m.name}</option>
                ))}
              </Select>
            </FormField>
            <FormField label="Color" htmlFor={`${formId}-color`}>
              <ColorPicker
                id={`${formId}-color`}
                value={colorHex}
                onChange={setColorHex}
                placeholder="FF5733"
                aria-label="Color hex code"
              />
            </FormField>
            <FormField label="Article Number" htmlFor={`${formId}-article`}>
              <Input
                id={`${formId}-article`}
                type="text"
                value={articleNumber}
                onChange={e => setArticleNumber(e.target.value)}
                placeholder="SKU"
                aria-label="Article number"
              />
            </FormField>
          </div>

          <div className="grid grid-cols-3 gap-4">
            <FormField label="Price" htmlFor={`${formId}-price`}>
              <Input
                id={`${formId}-price`}
                type="number"
                step="0.01"
                min="0"
                value={price}
                onChange={e => setPrice(e.target.value)}
                placeholder="24.99"
                aria-label="Price"
              />
            </FormField>
            <FormField label="Extruder Temp (°C)" htmlFor={`${formId}-extruder`}>
              <Input
                id={`${formId}-extruder`}
                type="number"
                min="0"
                max="500"
                value={extruderTemp}
                onChange={e => setExtruderTemp(e.target.value)}
                placeholder="215"
                aria-label="Extruder temperature"
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
                placeholder="60"
                aria-label="Bed temperature"
              />
            </FormField>
          </div>

          <div className="grid grid-cols-4 gap-4">
            <FormField label="Density (g/cm³)" htmlFor={`${formId}-density`}>
              <Input
                id={`${formId}-density`}
                type="number"
                step="0.01"
                min="0"
                value={density}
                onChange={e => setDensity(e.target.value)}
                placeholder="1.24"
                aria-label="Density"
              />
            </FormField>
            <FormField label="Diameter (mm)" htmlFor={`${formId}-diameter`}>
              <Input
                id={`${formId}-diameter`}
                type="number"
                step="0.01"
                min="0"
                value={diameter}
                onChange={e => setDiameter(e.target.value)}
                placeholder="1.75"
                aria-label="Diameter"
              />
            </FormField>
            <FormField label="Weight (g)" htmlFor={`${formId}-weight`}>
              <Input
                id={`${formId}-weight`}
                type="number"
                min="0"
                value={weight}
                onChange={e => setWeight(e.target.value)}
                placeholder="1000"
                aria-label="Net weight"
              />
            </FormField>
            <FormField label="Spool Wt (g)" htmlFor={`${formId}-spool-weight`}>
              <Input
                id={`${formId}-spool-weight`}
                type="number"
                min="0"
                value={spoolWeight}
                onChange={e => setSpoolWeight(e.target.value)}
                placeholder="Empty"
                aria-label="Spool weight"
              />
            </FormField>
          </div>

          <FormField label="Comment" htmlFor={`${formId}-comment`}>
            <Textarea
              id={`${formId}-comment`}
              value={comment}
              onChange={e => setComment(e.target.value)}
              placeholder="Optional notes..."
              rows={2}
              className="resize-none"
              aria-label="Comment"
            />
          </FormField>
        </form>
      )}
    </Modal>
  );
}
