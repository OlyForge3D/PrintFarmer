import { useState, useMemo, useCallback } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { FormField } from '@/common/components/ui/FormField';
import { Alert } from '@/common/components/ui/Alert';
import { LayersIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { sliceJobService, type SubmitSliceJobRequest } from '@/services/sliceJobService';
import {
  slicerProfilesService,
  type OrcaMachineProfile,
  type OrcaFilamentProfile,
  type OrcaProcessProfile,
} from '@/services/slicerProfilesService';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { BED_TYPE_OPTIONS } from '@/features/slicer/components/settings';
import type { Model } from '@/types/models';

export interface QuickSliceModalProps {
  isOpen: boolean;
  onClose: () => void;
  model: Model | null;
}

export function QuickSliceModal({ isOpen, onClose, model }: QuickSliceModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Quick Slice"
      titleIcon={<LayersIcon className="w-5 h-5" />}
      size="md"
    >
      {isOpen && model && (
        <QuickSliceForm model={model} onClose={onClose} />
      )}
    </Modal>
  );
}

interface PrinterOption {
  id: string;
  name: string;
  modelId?: string;
}

function QuickSliceForm({ model, onClose }: { model: Model; onClose: () => void }) {
  const { user } = useAuth();
  const navigate = useNavigate();
  const qc = useQueryClient();

  const [selectedPrinterId, setSelectedPrinterId] = useState('');
  const [selectedMachineProfileId, setSelectedMachineProfileId] = useState('');
  const [selectedFilamentProfileId, setSelectedFilamentProfileId] = useState('');
  const [selectedProcessProfileId, setSelectedProcessProfileId] = useState('');
  const [selectedBedType, setSelectedBedType] = useState('');
  const [error, setError] = useState<string | null>(null);

  // Fetch printers
  const { data: printers = [] } = useQuery<PrinterOption[]>({
    queryKey: ['printers-quick-slice'],
    queryFn: async () => {
      const list = await apiClient.getPrinters();
      return list.map(p => ({
        id: p.id,
        name: p.name,
        modelId: p.modelId,
      }));
    },
    staleTime: 30_000,
  });

  // Effective printer: user selection or first available
  const effectivePrinterId = selectedPrinterId || (printers.length > 0 ? printers[0].id : '');

  // Get printer details (includes modelId for profile lookup)
  const { data: printerDetails } = useQuery({
    queryKey: ['printerDetails', effectivePrinterId],
    queryFn: () => apiClient.getPrinterDetails(effectivePrinterId),
    enabled: !!effectivePrinterId,
    staleTime: 30_000,
  });

  const printerModelId = printerDetails?.modelId ?? null;

  // Fetch machine profiles for selected printer's model
  const { data: machineProfiles = [] } = useQuery<OrcaMachineProfile[]>({
    queryKey: ['machineProfilesForModel', printerModelId],
    queryFn: () => slicerProfilesService.getMachineProfilesForModel(printerModelId!),
    enabled: !!printerModelId,
    staleTime: 30_000,
  });

  // Effective machine: user selection or first available
  const effectiveMachineProfileId = selectedMachineProfileId
    || (machineProfiles.length > 0 ? machineProfiles[0].name : '');

  const machineNames = useMemo(() => {
    return effectiveMachineProfileId ? [effectiveMachineProfileId] : [];
  }, [effectiveMachineProfileId]);

  // Fetch filament profiles
  const { data: filamentProfiles = [] } = useQuery<OrcaFilamentProfile[]>({
    queryKey: ['filamentProfilesForMachines', machineNames],
    queryFn: () => slicerProfilesService.getFilamentProfilesForMachines(machineNames),
    enabled: machineNames.length > 0,
    staleTime: 30_000,
  });

  // Fetch process profiles
  const { data: processProfiles = [] } = useQuery<OrcaProcessProfile[]>({
    queryKey: ['processProfilesForMachines', machineNames],
    queryFn: () => slicerProfilesService.getProcessProfilesForMachines(machineNames),
    enabled: machineNames.length > 0,
    staleTime: 30_000,
  });

  // Effective filament/process: user selection or first available
  const effectiveFilamentProfileId = selectedFilamentProfileId
    || (filamentProfiles.length > 0 ? filamentProfiles[0].name : '');
  const effectiveProcessProfileId = selectedProcessProfileId
    || (processProfiles.length > 0 ? processProfiles[0].name : '');

  // Handler that cascades resets when printer changes
  const handlePrinterChange = useCallback((printerId: string) => {
    setSelectedPrinterId(printerId);
    setSelectedMachineProfileId('');
    setSelectedFilamentProfileId('');
    setSelectedProcessProfileId('');
  }, []);

  // Handler that cascades resets when machine changes
  const handleMachineChange = useCallback((machineId: string) => {
    setSelectedMachineProfileId(machineId);
    setSelectedFilamentProfileId('');
    setSelectedProcessProfileId('');
  }, []);

  const submitMutation = useMutation({
    mutationFn: (req: SubmitSliceJobRequest) => sliceJobService.submitJob(req),
    onSuccess: (res) => {
      toast.success(`Slice job queued — position ${res.queuePosition}`);
      qc.invalidateQueries({ queryKey: ['slice-jobs-my'] });
      qc.invalidateQueries({ queryKey: ['slice-jobs'] });
      onClose();
      navigate('/slice-jobs');
    },
    onError: (err: unknown) => {
      setError(err instanceof Error ? err.message : 'Failed to submit slice job');
    },
  });

  const handleSubmit = useCallback(() => {
    setError(null);

    if (!effectiveMachineProfileId) {
      setError('Select a machine profile');
      return;
    }
    if (!effectiveFilamentProfileId) {
      setError('Select a filament profile');
      return;
    }
    if (!effectiveProcessProfileId) {
      setError('Select a process profile');
      return;
    }

    const apiBase = getApiBaseUrl();
    const modelFileUrl = `${apiBase}/3d-models/file/${model.id}`;

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      modelFileUrl,
      modelFileName: model.fileName || model.name,
      slicerEngine: 0, // OrcaSlicer
      slicerProfileJson: JSON.stringify({
        machineProfileName: effectiveMachineProfileId,
        filamentProfileName: effectiveFilamentProfileId,
        processProfileName: effectiveProcessProfileId,
        overrides: {
          ...(selectedBedType ? { curr_bed_type: selectedBedType } : {}),
        },
      }),
      requiredCapabilitiesJson: '[]',
      priority: 1,
    };

    submitMutation.mutate(request);
  }, [
    model,
    effectiveMachineProfileId,
    effectiveFilamentProfileId,
    effectiveProcessProfileId,
    selectedBedType,
    submitMutation,
    user?.id,
  ]);

  const handleAdvanced = useCallback(() => {
    onClose();
    navigate(`/slicer?modelId=${model.id}`);
  }, [model.id, navigate, onClose]);

  return (
    <>
      <div className="space-y-4">
        <div className="rounded-md bg-pf-bg-2 px-3 py-2 text-sm text-pf-text-secondary">
          <span className="font-medium text-pf-text-primary">{model.name}</span>
        </div>

        <FormField label="Printer" htmlFor="qs-printer" required>
          <Select
            id="qs-printer"
            value={effectivePrinterId}
            onChange={(e) => handlePrinterChange(e.target.value)}
          >
            {printers.length === 0 && <option value="">Select printer…</option>}
            {printers.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </Select>
        </FormField>

        <FormField label="Machine Profile" htmlFor="qs-machine" required>
          <Select
            id="qs-machine"
            value={effectiveMachineProfileId}
            onChange={(e) => handleMachineChange(e.target.value)}
            disabled={machineProfiles.length === 0}
          >
            {machineProfiles.length === 0 && (
              <option value="">No profiles available</option>
            )}
            {machineProfiles.map((p) => (
              <option key={p.name} value={p.name}>{p.name}</option>
            ))}
          </Select>
        </FormField>

        <FormField label="Process Profile" htmlFor="qs-process" required>
          <Select
            id="qs-process"
            value={effectiveProcessProfileId}
            onChange={(e) => setSelectedProcessProfileId(e.target.value)}
            disabled={processProfiles.length === 0}
          >
            {processProfiles.length === 0 && (
              <option value="">No profiles available</option>
            )}
            {processProfiles.map((p) => (
              <option key={p.name} value={p.name}>
                {p.name}{p.layerHeight ? ` (${p.layerHeight}mm)` : ''}
              </option>
            ))}
          </Select>
        </FormField>

        <FormField label="Filament Profile" htmlFor="qs-filament" required>
          <Select
            id="qs-filament"
            value={effectiveFilamentProfileId}
            onChange={(e) => setSelectedFilamentProfileId(e.target.value)}
            disabled={filamentProfiles.length === 0}
          >
            {filamentProfiles.length === 0 && (
              <option value="">No profiles available</option>
            )}
            {filamentProfiles.map((p) => (
              <option key={p.name} value={p.name}>{p.name}</option>
            ))}
          </Select>
        </FormField>

        <FormField label="Bed Type" htmlFor="qs-bed-type">
          <Select
            id="qs-bed-type"
            value={selectedBedType}
            onChange={(e) => setSelectedBedType(e.target.value)}
          >
            <option value="">Inherit from profile</option>
            {BED_TYPE_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>{opt.label}</option>
            ))}
          </Select>
        </FormField>

        {error && <Alert variant="error">{error}</Alert>}
      </div>

      <div className="mt-6 flex items-center justify-between">
        <Button
          variant="subtle"
          size="sm"
          onClick={handleAdvanced}
        >
          Advanced Settings →
        </Button>

        <div className="flex gap-2">
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={submitMutation.isPending || !effectiveProcessProfileId}
          >
            {submitMutation.isPending ? 'Submitting…' : 'Slice'}
          </Button>
        </div>
      </div>
    </>
  );
}
