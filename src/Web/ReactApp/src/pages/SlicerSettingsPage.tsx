import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import React, { useState } from 'react';
import { PageTemplate } from '@/components/PageTemplate';
import { Settings } from 'lucide-react';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

// Lightweight mapping to match server DTOs
type PerEngineSetting = { path?: string | null; argsTemplate?: string | null };
type SlicerSettingsDto = { enabled: boolean; perEngine: Record<string, PerEngineSetting>; jitterPercent?: number };

export const SlicerSettingsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { data, isLoading } = useQuery<SlicerSettingsDto, Error>({
    queryKey: ['slicerSettings'],
    queryFn: async () => {
      const res = await fetch(`${getApiBaseUrl()}/slicer/settings`, {
        headers: getAuthHeaders()
      });
      if (!res.ok) throw new Error('Failed to fetch settings');
      return res.json();
    }
  });

  const [validationError, setValidationError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  // Enhance save mutation to surface server messages
  const saveMutation = useMutation<void, Error, SlicerSettingsDto>({
    mutationFn: async (payload: SlicerSettingsDto) => {
      const res = await fetch(`${getApiBaseUrl()}/slicer/settings`, { 
        method: 'POST', 
        headers: { 
          'Content-Type': 'application/json',
          ...getAuthHeaders()
        }, 
        body: JSON.stringify(payload) 
      });
      if (!res.ok) {
        const text = await res.text();
        throw new Error(text || `Save failed with status ${res.status}`);
      }
    },
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['slicerSettings'] }); setSaveError(null); },
    onError: (err: unknown) => setSaveError(err instanceof Error ? err.message : String(err))
  });

  const [local, setLocal] = useState<SlicerSettingsDto | null>(null);
  React.useEffect(() => { if (data) setLocal(data); else setLocal(null); }, [data]);

  const [openExamplesEngine, setOpenExamplesEngine] = useState<string | null>(null);

  if (isLoading || !local) return (
    <PageTemplate
      title="Slicer Worker Settings"
      subtitle="Configure local slicer executables and enable/disable the local worker"
      icon={Settings}
      maxWidth="max-w-4xl"
    >
      <div>Loading...</div>
    </PageTemplate>
  );

  const updateEngineField = (engine: string, field: 'path' | 'argsTemplate', value: string) => {
    setLocal(prev => {
      if (!prev) return prev;
      const copy = { ...prev, perEngine: { ...prev.perEngine } } as SlicerSettingsDto;
      copy.perEngine[engine] = { ...(copy.perEngine[engine] ?? {}), [field]: value };
      return copy;
    });
  };

  const onJitterChange = (valueStr: string) => {
    const v = parseFloat(valueStr);
    setLocal(prev => ({ ...(prev as SlicerSettingsDto), jitterPercent: Number.isNaN(v) ? undefined : v }));
    if (Number.isNaN(v)) {
      setValidationError('Jitter percent must be a number');
    } else if (v < 0 || v > 100) {
      setValidationError('Jitter percent must be between 0 and 100');
    } else {
      setValidationError(null);
    }
  };

  return (
    <PageTemplate
      title="Slicer Worker Settings"
      subtitle="Configure local slicer executables and enable/disable the local worker"
      icon={Settings}
      maxWidth="max-w-4xl"
    >

      <div className="card">
        <div className="form-group inline">
          <label className="form-label" htmlFor="enable-worker">
            <input id="enable-worker" type="checkbox" checked={local.enabled} onChange={(e: React.ChangeEvent<HTMLInputElement>) => setLocal({ ...local, enabled: e.target.checked })} className="mr-2" />
            Enable local slicer worker (process jobs locally)
          </label>
        </div>
      </div>

      <div className="card">
        <h3 className="font-medium mb-3">Per-engine executables</h3>
        <div className="gap-md flex-col">
          {(['PrusaSlicer', 'OrcaSlicer', 'SuperSlicer', 'Cura'] as string[]).map(engine => (
            <div key={engine} className="grid grid-cols-3 gap-3 items-center">
              <div className="font-medium">{engine}</div>
              <div>
                <input type="text" value={local.perEngine[engine]?.path ?? ''} onChange={(e: React.ChangeEvent<HTMLInputElement>) => updateEngineField(engine, 'path', e.target.value)} placeholder="Path to binary" className="input-base w-full" />
                <div className="form-helper mt-1">Path to slicer executable (leave empty to attempt PATH discovery)</div>
              </div>
              <div>
                <input type="text" value={local.perEngine[engine]?.argsTemplate ?? ''} onChange={(e: React.ChangeEvent<HTMLInputElement>) => updateEngineField(engine, 'argsTemplate', e.target.value)} placeholder="Args template" className="input-base w-full" />
                <div className="form-helper mt-1">Args template — use {'{input}'} and {'{output}'} placeholders</div>

                {engine === 'OrcaSlicer' && (
                  <div className="mt-2">
                    <button type="button" className="btn-link" onClick={() => setOpenExamplesEngine(openExamplesEngine === engine ? null : engine)}>
                      {openExamplesEngine === engine ? 'Hide examples' : 'Show Orca examples'}
                    </button>
                    {openExamplesEngine === engine && (
                      <div className="panel mt-2">
                        <div className="form-helper mb-2">Recommended Orca templates (you can edit after inserting):</div>
                        <div className="gap-sm flex-row">
                          <button type="button" className="btn-base btn-sm btn-secondary" onClick={() => updateEngineField(engine, 'argsTemplate', '--config "{config}" --output "{output}" {input}')}>Insert config example</button>
                          <button type="button" className="btn-base btn-sm btn-secondary" onClick={() => updateEngineField(engine, 'argsTemplate', '--export-gcode -o {output} {input}')}>Insert simple export</button>
                        </div>
                        <div className="form-helper mt-2">Placeholders: <code>{'{input}'}</code> &ndash; model file; <code>{'{output}'}</code> &ndash; gcode output; <code>{'{config}'}</code> &ndash; generated config path (worker replaces this when present)</div>
                      </div>
                    )}
                  </div>
                )}

              </div>
            </div>
          ))}
        </div>
      </div>

      <div className="card">
        <div className="form-group">
          <label className="form-label" htmlFor="jitter-percent">Retry jitter percent</label>
          <div className="form-helper">Percentage +/- applied to retry backoff delays (e.g. 15 = +/-15%)</div>
          <input id="jitter-percent" type="number" step="0.1" min={0} max={100} value={local.jitterPercent ?? 15} onChange={(e: React.ChangeEvent<HTMLInputElement>) => onJitterChange(e.target.value)} className="input-base mt-2 w-32" />
          {validationError && <div className="form-error mt-1">{validationError}</div>}
        </div>
      </div>

      <div className="card-footer">
        <button onClick={() => local && saveMutation.mutate(local)} disabled={!!validationError || !local} className="btn-base btn-md btn-primary ms-auto">Save Settings</button>
      </div>

      {(saveMutation.error || saveError) && <div className="alert-base alert-error">{(saveError) ?? (saveMutation.error as Error)?.message ?? 'Failed to save settings'}</div>}
    </PageTemplate>
  );
};

export default SlicerSettingsPage;
