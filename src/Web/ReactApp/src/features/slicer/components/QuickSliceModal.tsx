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
import { slicerService, type SlicerEngineInfo } from '@/services/slicerService';
import {
  slicerProfilesService,
  type OrcaMachineProfile,
  type OrcaFilamentProfile,
  type OrcaProcessProfile,
} from '@/services/slicerProfilesService';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { getErrorMessage } from '@/common/utils/apiErrors';
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

  // Issue #578: dual-engine registry so QuickSlice can pin to the newest
  // AVAILABLE OrcaSlicer version rather than accepting whatever worker claims first.
  const { data: registeredEngines } = useQuery<SlicerEngineInfo[]>({
    queryKey: ['slicer-engines-registry'],
    queryFn: () => slicerService.listEngines(),
    staleTime: 300_000,
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

  // Issue #578: profile queries must route to the same version QuickSlice will
  // pin the job to, otherwise we would show 2.4.1 profile shapes while dispatching
  // to a 2.3.1 worker (or vice versa). `effectiveEngineVersion` mirrors the
  // pinnedVersion computed at submit-time.
  const effectiveEngineVersion = useMemo(() => {
    const orca = registeredEngines?.find(
      e => e.engine.toLowerCase() === 'orcaslicer',
    );
    return orca?.latest ?? undefined;
  }, [registeredEngines]);

  // Fetch machine profiles for selected printer's model
  const { data: machineProfiles = [] } = useQuery<OrcaMachineProfile[]>({
    queryKey: ['machineProfilesForModel', printerModelId, effectiveEngineVersion ?? null],
    queryFn: () => slicerProfilesService.getMachineProfilesForModel(printerModelId!, effectiveEngineVersion),
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
    queryKey: ['filamentProfilesForMachines', machineNames, effectiveEngineVersion ?? null],
    queryFn: () => slicerProfilesService.getFilamentProfilesForMachines(machineNames, effectiveEngineVersion),
    enabled: machineNames.length > 0,
    staleTime: 30_000,
  });

  // Fetch process profiles
  const { data: processProfiles = [] } = useQuery<OrcaProcessProfile[]>({
    queryKey: ['processProfilesForMachines', machineNames, effectiveEngineVersion ?? null],
    queryFn: () => slicerProfilesService.getProcessProfilesForMachines(machineNames, effectiveEngineVersion),
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
      navigate('/admin/manage?tab=operations&sub=workers&workerTab=jobs');
    },
    onError: (err: unknown) => {
      setError(getErrorMessage(err, 'Failed to submit slice job'));
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

    // Issue #578 dual-engine (Hicks R3, refined R4): the engines registry
    // query MUST have populated before we submit. Without it we would fall
    // through to an unpinned Orca job that any registered version could
    // claim — including the specific race condition Hicks flagged where the
    // older engine grabs work built against newer profiles. If the query is
    // still pending or failed we tell the user to retry rather than submit
    // blind.
    if (registeredEngines === undefined) {
      setError('Slicer registry not yet loaded. Please retry in a moment.');
      return;
    }
    const orcaEngine = registeredEngines.find(
      e => (e?.engine ?? '').toLowerCase() === 'orcaslicer',
    );
    // Backend returns latest=null in TWO shapes (Hicks R4 #3, Vasquez R4):
    //   1. Legacy / fresh-install: NO SlicerService rows registered — every
    //      versionEntry.available is true so the UI selector remains usable
    //      but we leave the job UNPINNED so a generic-capability legacy
    //      worker can claim it.
    //   2. All-offline: rows exist but nothing is fresh+online — every
    //      versionEntry.available is false and the job would sit
    //      unclaimable in the queue.
    // The presence of at least one `available` entry is the only reliable
    // signal that distinguishes legacy from all-offline.
    const pinnedVersion = orcaEngine?.latest ?? undefined;
    const hasAnyAvailableVersion = orcaEngine
      ? (orcaEngine.versionEntries ?? []).some(v => v.available)
      : true;
    if (
      orcaEngine
      && orcaEngine.versions.length > 0
      && !pinnedVersion
      && !hasAnyAvailableVersion
    ) {
      setError('No online OrcaSlicer worker is available to accept this job.');
      return;
    }

    const apiBase = getApiBaseUrl();
    const modelFileUrl = `${apiBase}/3d-models/file/${model.id}`;

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      modelFileUrl,
      model3DId: model.id,
      modelFileName: model.fileName || model.name,
      slicerEngine: 0, // OrcaSlicer
      ...(pinnedVersion ? { slicerEngineVersion: pinnedVersion } : {}),
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
    registeredEngines,
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
