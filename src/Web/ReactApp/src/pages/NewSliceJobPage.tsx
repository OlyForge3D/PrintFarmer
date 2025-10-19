import React, { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { sliceJobService, SubmitSliceJobRequest } from '@/services/sliceJobService';
import slicerProfilesService, { SlicerProfileListItem } from '@/services/slicerProfilesService';
// Lightweight model DTO interface for picker (subset of Model3DDto)
interface ModelListItem {
  id: string;
  fileName: string;
  originalFileName: string;
  fileFormat: number;
  uploadedAt: string;
  filePath?: string; // may not be exposed; we derive URL from id
}
import { PageTemplate } from '@/components/PageTemplate'; // Page layout component
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { FormField } from '@/components/ui/FormField';
import { Input } from '@/components/ui/Input';
import { Select } from '@/components/ui/Select';
import { Layers } from 'lucide-react';
import { useAuth } from '@/contexts/AuthHooks';

// Simple engine options (server will override when profileId provided)
const ENGINE_OPTIONS = [
  { label: 'PrusaSlicer', value: 1 },
  { label: 'OrcaSlicer', value: 0 }
];

export const NewSliceJobPage: React.FC = () => {
  const { user } = useAuth();
  const qc = useQueryClient();
  const [modelFileUrl, setModelFileUrl] = useState('');
  const [modelFileName, setModelFileName] = useState('');
  const [useModelPicker, setUseModelPicker] = useState(true);
  const [selectedModelId, setSelectedModelId] = useState<string>('');
  const [slicerEngine, setSlicerEngine] = useState<number>(1);
  const [useProfile, setUseProfile] = useState(true);
  const [selectedProfileId, setSelectedProfileId] = useState<string>('');
  const [rawProfileJson, setRawProfileJson] = useState('');
  const [requiredCapabilitiesJson, setRequiredCapabilitiesJson] = useState('[]');
  const [capabilitiesError, setCapabilitiesError] = useState<string | null>(null);
  const [parsedCapabilities, setParsedCapabilities] = useState<string[]>([]);
  const [priority, setPriority] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  // Restore persisted selections
  useEffect(() => {
    try {
      const savedCaps = localStorage.getItem('sliceJob.requiredCapabilities');
      if (savedCaps) setRequiredCapabilitiesJson(savedCaps);
      const savedProfileId = localStorage.getItem('sliceJob.selectedProfileId');
      if (savedProfileId) setSelectedProfileId(savedProfileId);
    } catch { /* ignore storage errors */ }
  }, []);

  // Persist capabilities & profile selection
  useEffect(() => {
    try { localStorage.setItem('sliceJob.requiredCapabilities', requiredCapabilitiesJson); } catch { /* ignore */ }
  }, [requiredCapabilitiesJson]);
  useEffect(() => {
    try {
      if (selectedProfileId) localStorage.setItem('sliceJob.selectedProfileId', selectedProfileId);
      else localStorage.removeItem('sliceJob.selectedProfileId');
    } catch { /* ignore */ }
  }, [selectedProfileId]);

  // Fetch extended profiles for selection
  const { data: profiles, isLoading: loadingProfiles } = useQuery<SlicerProfileListItem[], Error>({
    queryKey: ['slicerProfilesExtended'],
    queryFn: () => slicerProfilesService.listExtended(),
    staleTime: 15_000
  });

  // Fetch models for picker
  const { data: models, isLoading: loadingModels, error: modelsError } = useQuery<ModelListItem[], Error>({
    queryKey: ['modelsListBasic'],
    queryFn: async () => {
      const res = await fetch('/api/3d-models');
      if (!res.ok) throw new Error(await res.text() || 'Failed to load models');
      const json = await res.json();
      // Map to minimal list items
      return (json as unknown[]).map(obj => {
        const m = obj as { id: string; fileName?: string; displayName?: string; originalFileName?: string; fileFormat?: number; uploadedAt?: string; uploadedAtUtc?: string };
        return {
          id: m.id,
          fileName: m.fileName || m.displayName || m.originalFileName || 'model',
          originalFileName: m.originalFileName || m.fileName || m.displayName || 'model',
          fileFormat: m.fileFormat ?? 0,
          uploadedAt: m.uploadedAt ?? m.uploadedAtUtc ?? ''
        } as ModelListItem;
      });
    },
    staleTime: 20_000
  });

  // Derive model file URL when selected
  useEffect(() => {
    if (useModelPicker && selectedModelId) {
      // Construct API file URL; ModelController exposes /api/3d-models/{id}/file
      setModelFileUrl(`/api/3d-models/${selectedModelId}/file`);
      const mdl = models?.find(m => m.id === selectedModelId);
      if (mdl) {
        setModelFileName(mdl.fileName || mdl.originalFileName);
      }
    }
  }, [useModelPicker, selectedModelId, models]);

  // Capabilities JSON validation (live)
  useEffect(() => {
    const text = requiredCapabilitiesJson.trim();
    if (!text) {
      setParsedCapabilities([]);
      setCapabilitiesError(null);
      return;
    }
    try {
      const parsed = JSON.parse(text);
      if (!Array.isArray(parsed)) {
        setCapabilitiesError('Capabilities JSON must be an array');
        setParsedCapabilities([]);
      } else if (!parsed.every(x => typeof x === 'string')) {
        setCapabilitiesError('All capability entries must be strings');
        setParsedCapabilities([]);
      } else {
        setCapabilitiesError(null);
        setParsedCapabilities(parsed as string[]);
      }
    } catch {
      setCapabilitiesError('Invalid JSON syntax'); // Error handling for invalid JSON
      setParsedCapabilities([]);
    }
  }, [requiredCapabilitiesJson]);

  const submitMutation = useMutation({
    mutationFn: async (req: SubmitSliceJobRequest) => sliceJobService.submitJob(req),
    onSuccess: (res) => {
      setMessage(`Job queued (id ${res.jobId.substring(0,8)}) position ${res.queuePosition}`);
      setError(null);
      // Reset basic fields but keep capabilities / engine for convenience
      setModelFileUrl('');
      setModelFileName('');
      setRawProfileJson('');
      setSelectedProfileId('');
      qc.invalidateQueries({ queryKey: ['slice-jobs-my'] });
    },
    onError: (err: unknown) => {
      setError(err instanceof Error ? err.message : 'Failed to submit job');
    }
  });

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (useModelPicker) {
      if (!selectedModelId) {
        setError('Select a model or switch to manual URL mode');
        return;
      }
    } else {
      if (!modelFileUrl.trim()) {
        setError('Model file URL is required');
        return;
      }
      if (!modelFileName.trim()) {
        setError('Model file name is required');
        return;
      }
    }

    if (useProfile && !selectedProfileId) {
      setError('Select a profile or switch to raw JSON mode');
      return;
    }
    if (!useProfile && !rawProfileJson.trim()) {
      setError('Provide raw profile JSON or switch to profile mode');
      return;
    }

    if (capabilitiesError) {
      setError('Fix capabilities JSON errors before submitting');
      return;
    }
    // Canonicalize capabilities JSON
    const capabilities = parsedCapabilities.length > 0 ? JSON.stringify(parsedCapabilities) : '[]';

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      printerId: undefined,
      modelFileUrl: modelFileUrl,
      modelFileName: modelFileName,
      slicerEngine,
      slicerProfileJson: useProfile ? '{}' : rawProfileJson, // server ignores if profileId set
      slicerProfileId: useProfile ? selectedProfileId : undefined,
      requiredCapabilitiesJson: capabilities,
      priority
    };

    submitMutation.mutate(request);
  };

  return (
    <PageTemplate
      title="New Slice Job"
      subtitle="Submit a distributed slicing job using a stored profile or custom JSON"
      icon={Layers}
      maxWidth="max-w-4xl"
    >
      <form onSubmit={onSubmit} className="space-y-6 bg-pf-panel border border-pf-border rounded shadow p-4">
        <div className="space-y-4">
          <fieldset className="border border-pf-border rounded p-3">
            <legend className="text-sm font-semibold px-1">Model Source</legend>
            <FormField
              label="Use Model Picker"
              helper={useModelPicker ? 'Select an uploaded model; URL & name auto-fill.' : 'Disable picker to manually enter file URL/name.'}
              inline
            >
              <input
                id="useModelPicker"
                type="checkbox"
                checked={useModelPicker}
                onChange={() => {
                  setUseModelPicker(v => !v);
                  if (useModelPicker) {
                    setSelectedModelId('');
                    setModelFileUrl('');
                    setModelFileName('');
                  }
                }}
                aria-label="Toggle model picker"
                title="Toggle model picker"
              />
            </FormField>
          </fieldset>
          {useModelPicker ? (
            <div className="flex flex-col gap-3">
              <FormField
                label="Model"
                helper={loadingModels ? 'Loading models…' : modelsError ? undefined : (models && models.length === 0 ? 'No models available. Upload one first.' : 'Selecting a model auto-fills URL & name.')}
                error={modelsError ? modelsError.message : undefined}
              >
                {models && models.length > 0 ? (
                  <Select
                    value={selectedModelId}
                    onChange={e => setSelectedModelId(e.target.value)}
                    aria-label="Model picker"
                  >
                    <option value="">-- Select model --</option>
                    {models.map(m => (
                      <option key={m.id} value={m.id}>{m.fileName} ({m.originalFileName})</option>
                    ))}
                  </Select>
                ) : (
                  <select disabled className="border rounded p-2 text-sm bg-pf-disabled" aria-label="Model picker disabled" title="Model picker disabled">
                    <option>-- No models --</option>
                  </select>
                )}
              </FormField>
              <div className="grid md:grid-cols-2 gap-4 pt-2">
                <div className="flex flex-col gap-2">
                  <label className="text-sm font-medium">Model File URL</label>
                  <input
                    type="text"
                    value={modelFileUrl}
                    disabled
                    className="border rounded p-2 text-sm bg-pf-disabled"
                    aria-disabled="true"
                    aria-label="Selected model file URL"
                    title="Selected model file URL"
                    placeholder="Model file URL"
                  />
                </div>
                <div className="flex flex-col gap-2">
                  <label className="text-sm font-medium">Model File Name</label>
                  <input
                    type="text"
                    value={modelFileName}
                    disabled
                    className="border rounded p-2 text-sm bg-pf-disabled"
                    aria-disabled="true"
                    aria-label="Selected model file name"
                    title="Selected model file name"
                    placeholder="Model file name"
                  />
                </div>
              </div>
            </div>
          ) : (
            <div className="grid md:grid-cols-2 gap-4">
              <FormField label="Model File URL" required={!useModelPicker} helper={!useModelPicker ? 'Provide a direct path or remote URL.' : undefined}>
                <Input
                  type="text"
                  value={modelFileUrl}
                  onChange={e => setModelFileUrl(e.target.value)}
                  placeholder="e.g. /storage/models/cube.stl or https://..."
                  disabled={useModelPicker}
                />
              </FormField>
              <FormField label="Model File Name" required={!useModelPicker} helper={!useModelPicker ? 'Used for job identification.' : undefined}>
                <Input
                  type="text"
                  value={modelFileName}
                  onChange={e => setModelFileName(e.target.value)}
                  placeholder="cube.stl"
                  disabled={useModelPicker}
                />
              </FormField>
            </div>
          )}
        </div>

        <div className="grid md:grid-cols-3 gap-4">
          <div className="flex flex-col gap-2">
            <label className="text-sm font-medium">Engine (fallback)</label>
            <Select
              value={slicerEngine}
              onChange={e => setSlicerEngine(Number(e.target.value))}
              aria-label="Slicer engine"
            >
              {ENGINE_OPTIONS.map(opt => <option key={opt.value} value={opt.value}>{opt.label}</option>)}
            </Select>
            <div className="text-xs text-pf-text-muted">Actual engine overridden if a profile is selected.</div>
          </div>
          <div className="flex flex-col gap-2">
            <label className="text-sm font-medium">Priority</label>
            <Select
              value={priority}
              onChange={e => setPriority(Number(e.target.value))}
              aria-label="Job priority"
            >
              <option value={0}>Low</option>
              <option value={1}>Normal</option>
              <option value={2}>High</option>
              <option value={3}>Critical</option>
            </Select>
          </div>
          <div className="flex flex-col gap-2">
            <label className="text-sm font-medium">Mode</label>
            <div className="flex gap-4 items-center mt-1">
              <label className="inline-flex items-center gap-1 text-sm">
                <input type="radio" name="mode" checked={useProfile} onChange={() => setUseProfile(true)} />
                <span>Use Profile</span>
              </label>
              <label className="inline-flex items-center gap-1 text-sm">
                <input type="radio" name="mode" checked={!useProfile} onChange={() => setUseProfile(false)} />
                <span>Raw JSON</span>
              </label>
            </div>
          </div>
        </div>

         {useProfile ? (
           <FormField
             label="Slicer Profile"
             helper={loadingProfiles ? 'Loading profiles…' : (profiles && profiles.length === 0 ? 'No profiles available. Import one first.' : 'Overrides engine; snapshot stored with job.')}
           >
             {profiles && profiles.length > 0 ? (
               <Select
                 value={selectedProfileId}
                 onChange={e => setSelectedProfileId(e.target.value)}
                 aria-label="Slicer profile"
                 title="Slicer profile"
               >
                 <option value="">-- Select profile --</option>
                 {profiles.map(p => (
                   <option key={p.id} value={p.id}>
                     {p.name} • {p.slicerType} • {p.layerHeight.toFixed(2)}mm • {p.infillPercentage}%
                   </option>
                 ))}
               </Select>
             ) : (
               <Select disabled className="bg-pf-disabled" aria-label="No profiles" title="No profiles">
                 <option>-- No profiles --</option>
               </Select>
             )}
           </FormField>
         ) : (
           <FormField label="Raw Profile JSON" helper="Paste sanitized slicer config JSON; consider importing for reuse." required>
             <textarea
               value={rawProfileJson}
               onChange={e => setRawProfileJson(e.target.value)}
               rows={8}
               className="border rounded p-2 font-mono text-xs"
               placeholder={'{\n  "layer_height": 0.2, ...\n}'}
             />
           </FormField>
         )}

        <FormField
          label="Required Capabilities (JSON array)"
          helper={capabilitiesError ? undefined : 'Workers must match all listed capabilities.'}
          error={capabilitiesError || undefined}
        >
          <textarea
            value={requiredCapabilitiesJson}
            onChange={e => setRequiredCapabilitiesJson(e.target.value)}
            rows={3}
            className="border rounded p-2 font-mono text-xs"
            placeholder='["orcaslicer","multi-material"]'
          />
          {!capabilitiesError && parsedCapabilities.length > 0 && (
            <div className="text-xs text-pf-success">Parsed {parsedCapabilities.length} capability{parsedCapabilities.length === 1 ? '' : 'ies'}.</div>
          )}
        </FormField>

        {error && <Alert type="error">{error}</Alert>}
        {message && <Alert type="success">{message}</Alert>}

        <div className="flex gap-3">
          <Button type="submit" loading={submitMutation.isPending} variant="primary">Submit Slice Job</Button>
          <Button
            type="button"
            variant="secondary"
            onClick={() => {
              if (!useModelPicker) {
                setModelFileUrl('');
                setModelFileName('');
              }
              setRawProfileJson('');
              setSelectedProfileId('');
              setError(null); setMessage(null);
            }}
          >Reset</Button>
        </div>
      </form>
    </PageTemplate>
  );
};

export default NewSliceJobPage;
