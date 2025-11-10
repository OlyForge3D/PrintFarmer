import React, { useState, useEffect, Suspense } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { sliceJobService, SubmitSliceJobRequest } from '@/services/sliceJobService';
import slicerProfilesService, { SlicerProfileListItem } from '@/services/slicerProfilesService';
import workersService from '@/services/workersService';
import { slicerRegistry } from '@/services/slicerRegistry';
import { WorkerResponse } from '@/types/worker';
import { hasRequiredCapabilities } from '@/types/worker';
import * as signalR from '@microsoft/signalr';
import { getHubUrl, getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { lazyWithPreload } from '@/utils/lazyWithPreload';
import type { ModelViewerProps } from '@/components/3d/ModelViewer3D';
import { ViewerSkeleton } from '@/components/3d/ViewerSkeleton';

// Lazy load the 3D model viewer for better performance
const ModelViewer3D = React.lazy(() => 
  import('@/components/3d/ModelViewer3D').then(mod => ({ default: mod.ModelViewer }))
);

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
import { WorkerSelector } from '@/components/WorkerSelector';
import { Layers } from 'lucide-react';
import { useAuth } from '@/contexts/AuthHooks';

export const NewSliceJobPage: React.FC = () => {
  const { user } = useAuth();
  const qc = useQueryClient();
  const [searchParams] = useSearchParams();
  const modelIdFromUrl = searchParams.get('modelId') || '';

  const [modelFileUrl, setModelFileUrl] = useState('');
  const [modelFileName, setModelFileName] = useState('');
  const [useModelPicker, setUseModelPicker] = useState(true);
  const [selectedModelId, setSelectedModelId] = useState<string>(modelIdFromUrl);
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
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | undefined>(undefined);

  // Fetch available workers
  const { data: availableWorkers = [], isLoading: loadingWorkers } = useQuery<WorkerResponse[], Error>({
    queryKey: ['workers-available'],
    queryFn: () => workersService.getAvailableWorkers(),
    staleTime: 10_000,
    refetchInterval: 15_000, // Auto-refresh every 15 seconds
  });

  // Fetch available slicer services
  const { data: availableSlicers = [] } = useQuery({
    queryKey: ['slicers-available'],
    queryFn: () => slicerRegistry.getSlicers(),
    staleTime: 10_000,
    refetchInterval: 15_000, // Auto-refresh every 15 seconds
  });

  // Build engine options from available slicers
  const engineOptions = React.useMemo(() => {
    return availableSlicers.map(slicer => ({
      label: slicer.name || slicer.slicerType || 'Unknown',
      value: slicer.slicerType === 'PrusaSlicer' ? 1 : 0
    }));
  }, [availableSlicers]);

  // Filter workers by required capabilities
  const filteredWorkers = React.useMemo(() => {
    if (parsedCapabilities.length === 0) {
      return availableWorkers;
    }
    return availableWorkers.filter(worker => hasRequiredCapabilities(worker, parsedCapabilities));
  }, [availableWorkers, parsedCapabilities]);

  // Connect to SlicerHub for real-time worker updates
  useEffect(() => {
    try {
      // Defensive: in test environments the SignalR package or builder may be mocked
      // in a way that doesn't implement the full fluent API. Guard against that
      // so tests don't crash when the builder is unavailable.
      if (!signalR || typeof signalR.HubConnectionBuilder !== 'function') {
        return;
      }

      const builder = new signalR.HubConnectionBuilder();
      if (!builder || typeof builder.withUrl !== 'function') {
        return;
      }

      const hubConnection = builder
        .withUrl(getHubUrl('/hubs/slicer-registry'))
        .withAutomaticReconnect()
        .build();

      // Handle slicer registered event
      hubConnection.on('SlicerRegistered', () => {
        qc.invalidateQueries({ queryKey: ['workers-available'] });
      });

      // Handle slicer heartbeat event
      hubConnection.on('SlicerHeartbeat', () => {
        qc.invalidateQueries({ queryKey: ['workers-available'] });
      });

      // Handle slicer deregistered event
      hubConnection.on('SlicerDeregistered', () => {
        qc.invalidateQueries({ queryKey: ['workers-available'] });
      });

      hubConnection
        .start()
        .then(() => {
          console.log('Connected to SlicerHub for real-time worker updates');
        })
        .catch(err => {
          console.error('Failed to connect to SlicerHub:', err);
        });

      return () => {
        hubConnection.stop();
      };
    } catch {
      // Swallow errors in test environments where globals/mocks may differ.
      return;
    }
  }, [qc]);

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
      const baseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;
      const apiBase = !baseUrl || baseUrl.trim() === '' ? '/api' : baseUrl;
      const token = localStorage.getItem('auth-token');
      const headers: HeadersInit = { 'Content-Type': 'application/json' };
      if (token) headers['Authorization'] = `Bearer ${token}`;

      const res = await fetch(`${apiBase}/3d-models`, { headers });
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
      const baseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;
      const apiBase = !baseUrl || baseUrl.trim() === '' ? '/api' : baseUrl;
      setModelFileUrl(`${apiBase}/3d-models/${selectedModelId}/file`);
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
      setMessage(`Job queued (id ${res.jobId.substring(0, 8)}) position ${res.queuePosition}`);
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

  // Determine file type for 3D viewer
  const getFileType = (): 'stl' | '3mf' | 'obj' | 'ply' => {
    if (modelFileName) {
      const ext = modelFileName.split('.').pop()?.toLowerCase();
      if (ext === '3mf') return '3mf';
      if (ext === 'obj') return 'obj';
      if (ext === 'ply') return 'ply';
    }
    return 'stl';
  };

  const [showAdvanced, setShowAdvanced] = useState(false);
  const [expandedSection, setExpandedSection] = useState<string | null>('model');

  const toggleSection = (section: string) => {
    setExpandedSection(expandedSection === section ? null : section);
  };

  return (
    <PageTemplate
      title="New Slice Job"
      subtitle="Submit a distributed slicing job with 3D preview"
      icon={Layers}
      maxWidth="max-w-7xl"
    >
      <form onSubmit={onSubmit} className="flex flex-col lg:flex-row gap-6 h-full">
        {/* LEFT SIDEBAR: Form Controls */}
        <div className="w-full lg:w-80 space-y-4 flex-shrink-0">
          {/* Basic/Advanced Toggle */}
          <div className="sticky top-0 z-10 bg-pf-background pb-2 border-b border-pf-border">
            <button
              type="button"
              onClick={() => setShowAdvanced(!showAdvanced)}
              className="text-sm font-medium text-pf-accent hover:text-pf-accent-hover transition-colors"
            >
              {showAdvanced ? '← Basic' : 'Advanced →'}
            </button>
          </div>

          {/* Model Selection Section */}
          <div className="card bg-pf-panel border border-pf-border">
            <button
              type="button"
              onClick={() => toggleSection('model')}
              className="w-full card-header flex items-center justify-between hover:bg-pf-hover transition-colors cursor-pointer"
            >
              <span className="font-semibold text-pf-text">Model</span>
              <span className="text-pf-text-muted">{expandedSection === 'model' ? '−' : '+'}</span>
            </button>
            {expandedSection === 'model' && (
              <div className="card-body space-y-3">
                <FormField
                  label="Use Model Picker"
                  helper={useModelPicker ? 'Select from uploaded models' : 'Enter URL manually'}
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
                  />
                </FormField>

                {useModelPicker ? (
                  <>
                    <FormField
                      label="Model"
                      error={modelsError ? modelsError.message : undefined}
                    >
                      {models && models.length > 0 ? (
                        <Select
                          value={selectedModelId}
                          onChange={e => setSelectedModelId(e.target.value)}
                        >
                          <option value="">-- Select model --</option>
                          {models.map(m => (
                            <option key={m.id} value={m.id}>{m.fileName}</option>
                          ))}
                        </Select>
                      ) : (
                        <select disabled className="border rounded p-2 text-sm bg-pf-disabled w-full">
                          <option>-- No models --</option>
                        </select>
                      )}
                    </FormField>
                  </>
                ) : (
                  <>
                    <FormField label="File URL" required>
                      <Input
                        type="text"
                        value={modelFileUrl}
                        onChange={e => setModelFileUrl(e.target.value)}
                        placeholder="https://... or /storage/..."
                      />
                    </FormField>
                    <FormField label="File Name" required>
                      <Input
                        type="text"
                        value={modelFileName}
                        onChange={e => setModelFileName(e.target.value)}
                        placeholder="model.stl"
                      />
                    </FormField>
                  </>
                )}
              </div>
            )}
          </div>

          {/* Slicer Configuration Section */}
          <div className="card bg-pf-panel border border-pf-border">
            <button
              type="button"
              onClick={() => toggleSection('slicer')}
              className="w-full card-header flex items-center justify-between hover:bg-pf-hover transition-colors cursor-pointer"
            >
              <span className="font-semibold text-pf-text">Slicer Config</span>
              <span className="text-pf-text-muted">{expandedSection === 'slicer' ? '−' : '+'}</span>
            </button>
            {expandedSection === 'slicer' && (
              <div className="card-body space-y-3">
                <div className="space-y-3">
                  <FormField label="Engine (fallback)">
                    <Select
                      value={slicerEngine}
                      onChange={e => setSlicerEngine(Number(e.target.value))}
                    >
                      {engineOptions.map(opt => <option key={opt.value} value={opt.value}>{opt.label}</option>)}
                    </Select>
                    <div className="text-xs text-pf-text-muted mt-1">Overridden by profile if selected.</div>
                  </FormField>

                  <div className="flex gap-3">
                    <label className="inline-flex items-center gap-2 text-sm">
                      <input type="radio" name="mode" checked={useProfile} onChange={() => setUseProfile(true)} />
                      <span>Profile</span>
                    </label>
                    <label className="inline-flex items-center gap-2 text-sm">
                      <input type="radio" name="mode" checked={!useProfile} onChange={() => setUseProfile(false)} />
                      <span>JSON</span>
                    </label>
                  </div>
                </div>

                {useProfile ? (
                  <FormField label="Profile">
                    {profiles && profiles.length > 0 ? (
                      <Select
                        value={selectedProfileId}
                        onChange={e => setSelectedProfileId(e.target.value)}
                      >
                        <option value="">-- Select --</option>
                        {profiles.map(p => (
                          <option key={p.id} value={p.id}>
                            {p.name} ({p.slicerType})
                          </option>
                        ))}
                      </Select>
                    ) : (
                      <Select disabled className="bg-pf-disabled">
                        <option>-- No profiles --</option>
                      </Select>
                    )}
                  </FormField>
                ) : (
                  <FormField label="Raw JSON">
                    <textarea
                      value={rawProfileJson}
                      onChange={e => setRawProfileJson(e.target.value)}
                      rows={4}
                      className="border rounded p-2 font-mono text-xs w-full"
                      placeholder='{"layer_height": 0.2}'
                    />
                  </FormField>
                )}
              </div>
            )}
          </div>

          {/* Worker Selection Section */}
          <div className="card bg-pf-panel border border-pf-border">
            <button
              type="button"
              onClick={() => toggleSection('worker')}
              className="w-full card-header flex items-center justify-between hover:bg-pf-hover transition-colors cursor-pointer"
            >
              <span className="font-semibold text-pf-text">Worker</span>
              <span className="text-pf-text-muted">{expandedSection === 'worker' ? '−' : '+'}</span>
            </button>
            {expandedSection === 'worker' && (
              <div className="card-body space-y-3">
                <WorkerSelector
                  workers={filteredWorkers}
                  selectedWorkerId={selectedWorkerId}
                  onWorkerSelect={setSelectedWorkerId}
                  loading={loadingWorkers}
                  showCapabilities={true}
                  highlightAvailable={true}
                />
              </div>
            )}
          </div>

          {/* Advanced Section */}
          {showAdvanced && (
            <div className="card bg-pf-panel border border-pf-border">
              <button
                type="button"
                onClick={() => toggleSection('advanced')}
                className="w-full card-header flex items-center justify-between hover:bg-pf-hover transition-colors cursor-pointer"
              >
                <span className="font-semibold text-pf-text">Advanced</span>
                <span className="text-pf-text-muted">{expandedSection === 'advanced' ? '−' : '+'}</span>
              </button>
              {expandedSection === 'advanced' && (
                <div className="card-body space-y-3">
                  <FormField label="Priority">
                    <Select
                      value={priority}
                      onChange={e => setPriority(Number(e.target.value))}
                    >
                      <option value={0}>Low</option>
                      <option value={1}>Normal</option>
                      <option value={2}>High</option>
                      <option value={3}>Critical</option>
                    </Select>
                  </FormField>

                  <FormField
                    label="Capabilities"
                    error={capabilitiesError || undefined}
                  >
                    <textarea
                      value={requiredCapabilitiesJson}
                      onChange={e => setRequiredCapabilitiesJson(e.target.value)}
                      rows={3}
                      className="border rounded p-2 font-mono text-xs w-full"
                      placeholder='["orcaslicer"]'
                    />
                  </FormField>
                </div>
              )}
            </div>
          )}

          {/* Status Messages */}
          {error && <Alert type="error">{error}</Alert>}
          {message && <Alert type="success">{message}</Alert>}

          {/* Action Buttons */}
          <div className="flex flex-col gap-2 sticky bottom-0 bg-pf-background pt-2 border-t border-pf-border">
            <Button type="submit" loading={submitMutation.isPending} variant="primary" className="w-full">
              Submit Job
            </Button>
            <Button
              type="button"
              variant="secondary"
              className="w-full"
              onClick={() => {
                setModelFileUrl('');
                setModelFileName('');
                setRawProfileJson('');
                setSelectedProfileId('');
                setError(null);
                setMessage(null);
              }}
            >
              Reset
            </Button>
          </div>
        </div>

        {/* RIGHT SIDE: 3D Model Preview */}
        <div className="flex-1 hidden lg:flex flex-col gap-4 min-h-96">
          <div className="card bg-pf-panel border border-pf-border flex-1 overflow-hidden">
            <div className="card-header">
              <h3 className="font-semibold text-pf-text">
                {modelFileName ? `Preview: ${modelFileName}` : 'Model Preview'}
              </h3>
            </div>
            <div className="card-body p-0 flex-1">
              {modelFileUrl ? (
                <Suspense fallback={<ViewerSkeleton variant="model" className="h-full w-full" />}>
                  <ModelViewer3D
                    modelUrl={modelFileUrl}
                    fileType={getFileType()}
                    showGrid={true}
                    showAxes={true}
                    autoRotate={false}
                    className="h-full w-full"
                  />
                </Suspense>
              ) : (
                <div className="h-full w-full flex items-center justify-center text-pf-text-muted">
                  <div className="text-center">
                    <p className="text-sm">Select a model to view 3D preview</p>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </form>
    </PageTemplate>
  );
};

export default NewSliceJobPage;
