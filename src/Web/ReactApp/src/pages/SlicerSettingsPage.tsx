import { SlicerEngineType } from '@/services/types';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import React, { useState } from 'react';

// Lightweight mapping to match server DTOs
type PerEngineSetting = { path?: string | null; argsTemplate?: string | null };
type SlicerSettingsDto = { enabled: boolean; perEngine: Record<string, PerEngineSetting>; jitterPercent?: number };

export const SlicerSettingsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { data, isLoading } = useQuery<SlicerSettingsDto, Error>({
    queryKey: ['slicerSettings'],
    queryFn: async () => {
      const res = await fetch('/api/slicer/settings');
      if (!res.ok) throw new Error('Failed to fetch settings');
      return res.json();
    }
  });

  const [validationError, setValidationError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  // Enhance save mutation to surface server messages
  const saveMutation = useMutation<void, Error, SlicerSettingsDto>({
    mutationFn: async (payload: SlicerSettingsDto) => {
      const res = await fetch('/api/slicer/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
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

  if (isLoading || !local) return <div>Loading...</div>;

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
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold">Slicer Worker Settings</h1>
        <p className="text-gray-500">Configure local slicer executables and enable/disable the local worker.</p>
      </div>

      <div className="bg-white rounded shadow p-4">
        <label className="inline-flex items-center">
          <input type="checkbox" checked={local.enabled} onChange={(e: React.ChangeEvent<HTMLInputElement>) => setLocal({ ...local, enabled: e.target.checked })} className="mr-2" />
          <span>Enable local slicer worker (process jobs locally)</span>
        </label>
      </div>

      <div className="bg-white rounded shadow p-4">
        <h3 className="font-medium mb-3">Per-engine executables</h3>
        <div className="space-y-3">
          {(['PrusaSlicer', 'OrcaSlicer', 'SuperSlicer', 'Cura'] as string[]).map(engine => (
            <div key={engine} className="grid grid-cols-3 gap-3 items-center">
              <div className="font-medium">{engine}</div>
              <div>
                <input type="text" value={local.perEngine[engine]?.path ?? ''} onChange={(e: React.ChangeEvent<HTMLInputElement>) => updateEngineField(engine, 'path', e.target.value)} placeholder="Path to binary" className="w-full border rounded px-2 py-1" />
                <div className="text-xs text-gray-500 mt-1">Path to slicer executable (leave empty to attempt PATH discovery)</div>
              </div>
              <div>
                <input type="text" value={local.perEngine[engine]?.argsTemplate ?? ''} onChange={(e: React.ChangeEvent<HTMLInputElement>) => updateEngineField(engine, 'argsTemplate', e.target.value)} placeholder="Args template" className="w-full border rounded px-2 py-1" />
                <div className="text-xs text-gray-500 mt-1">Args template — use {'{input}'} and {'{output}'} placeholders</div>

                {engine === 'OrcaSlicer' && (
                  <div className="mt-2">
                    <button type="button" className="text-sm text-blue-600 underline mr-3" onClick={() => setOpenExamplesEngine(openExamplesEngine === engine ? null : engine)}>
                      {openExamplesEngine === engine ? 'Hide examples' : 'Show Orca examples'}
                    </button>
                    {openExamplesEngine === engine && (
                      <div className="mt-2 bg-gray-50 p-2 rounded">
                        <div className="text-xs text-gray-700 mb-2">Recommended Orca templates (you can edit after inserting):</div>
                        <div className="flex gap-2">
                          <button type="button" className="px-2 py-1 bg-gray-200 rounded text-sm" onClick={() => updateEngineField(engine, 'argsTemplate', '--config "{config}" --output "{output}" {input}')}>Insert config example</button>
                          <button type="button" className="px-2 py-1 bg-gray-200 rounded text-sm" onClick={() => updateEngineField(engine, 'argsTemplate', '--export-gcode -o {output} {input}')}>Insert simple export</button>
                        </div>
                        <div className="text-xs text-gray-500 mt-2">Placeholders: <code>{'{input}'}</code> &ndash; model file; <code>{'{output}'}</code> &ndash; gcode output; <code>{'{config}'}</code> &ndash; generated config path (worker replaces this when present)</div>
                      </div>
                    )}
                  </div>
                )}

              </div>
            </div>
          ))}
        </div>
      </div>

      <div className="bg-white rounded shadow p-4">
        <label className="block">
          <div className="font-medium">Retry jitter percent</div>
          <div className="text-xs text-gray-500">Percentage +/- applied to retry backoff delays (e.g. 15 = +/-15%)</div>
          <input type="number" step="0.1" min={0} max={100} value={local.jitterPercent ?? 15} onChange={(e: React.ChangeEvent<HTMLInputElement>) => onJitterChange(e.target.value)} className="mt-2 w-32 border rounded px-2 py-1" />
          {validationError && <div className="text-sm text-red-600 mt-1">{validationError}</div>}
        </label>
      </div>

      <div className="flex justify-end">
        <button onClick={() => local && saveMutation.mutate(local)} disabled={!!validationError || !local} className="px-4 py-2 bg-blue-600 text-white rounded">Save Settings</button>
      </div>

      {(saveMutation.error || saveError) && <div className="text-red-600">{(saveError) ?? (saveMutation.error as Error)?.message ?? 'Failed to save settings'}</div>}
    </div>
  );
};

export default SlicerSettingsPage;