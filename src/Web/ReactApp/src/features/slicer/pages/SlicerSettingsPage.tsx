import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import React, { useState, useMemo } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, FormField, Input, Checkbox } from '@/common/components/ui';
import { SettingsIcon } from '@/common/components/icons/MdiIcons';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { slicerRegistry } from '@/services/slicerRegistry';
import type { SlicerSettingsDto } from '@/types/slicer';

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

  // Fetch available slicers (registered workers)
  const { data: availableSlicers = [], isLoading: isLoadingSlicers } = useQuery({
    queryKey: ['slicers-available'],
    queryFn: () => slicerRegistry.getSlicers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });

  // Extract unique slicer types from available registered workers
  const slicerTypes = useMemo(() => {
    return availableSlicers
      .map(s => s.slicerType || s.name || '')
      .filter((v, i, arr) => v && arr.indexOf(v) === i)
      .sort();
  }, [availableSlicers]);

  // Check if there are any slicers available
  const hasSlicersAvailable = slicerTypes.length > 0;

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

  // Show loading state
  if (isLoading || isLoadingSlicers || !local) return (
    <PageTemplate
      title="Slicer Worker Settings"
      subtitle="Configure local slicer executables and enable/disable the local worker"
      icon={SettingsIcon}
      maxWidth="max-w-4xl"
    >
      <div className="card">
        <div className="card-body">
          <div className="text-center text-pf-text-secondary">Loading settings...</div>
        </div>
      </div>
    </PageTemplate>
  );

  // Show "no slicers available" message
  if (!hasSlicersAvailable) {
    return (
      <PageTemplate
        title="Slicer Worker Settings"
        subtitle="Configure local slicer executables and enable/disable the local worker"
        icon={SettingsIcon}
        maxWidth="max-w-4xl"
      >
        <div className="card">
          <div className="card-body">
            <div className="alert-base alert-info">
              <div>
                <div className="font-medium mb-2">No Slicer Workers Registered</div>
                <p className="text-sm">
                  No slicer workers have been registered with the system yet. Please register a slicer worker first using the{' '}
                  <a href="/admin/slicer-registry" className="text-pf-link underline hover:no-underline">
                    Slicer Registry
                  </a>
                  {' '}before configuring slicer settings.
                </p>
              </div>
            </div>
          </div>
        </div>
      </PageTemplate>
    );
  }

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
      icon={SettingsIcon}
      maxWidth="max-w-4xl"
    >
      {/* Enable Worker Card */}
      <div className="card">
        <div className="card-body">
          <div className="form-group inline">
            <label className="form-label" htmlFor="enable-worker">
              <Checkbox
                id="enable-worker"
                checked={local.enabled}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => setLocal({ ...local, enabled: e.target.checked })}
              />
              Enable local slicer worker (process jobs locally)
            </label>
          </div>
        </div>
      </div>

      {/* Per-Engine Settings Card */}
      <div className="card">
        <div className="card-header">
          <div className="card-header-title">Per-Engine Executables</div>
        </div>
        <div className="card-body gap-md flex-col">
          {slicerTypes.length === 0 ? (
            <div className="text-center text-pf-text-secondary py-4">
              No slicer workers registered. Configure slicers after they are discovered.
            </div>
          ) : (
            slicerTypes.map(engine => (
              <div key={engine} className="border border-pf-border rounded-md p-4 bg-pf-bg-1">
                <div className="font-medium mb-3 text-pf-text-primary">{engine}</div>

                <div className="gap-md flex flex-col">
                  {/* Path Input */}
                  <FormField
                    label="Executable Path"
                    helper="Path to slicer executable (leave empty to attempt PATH discovery)"
                    inline={false}
                  >
                    <Input
                      id={`path-${engine}`}
                      type="text"
                      value={local.perEngine[engine]?.path ?? ''}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) => updateEngineField(engine, 'path', e.target.value)}
                      placeholder="Path to binary"
                    />
                  </FormField>

                  {/* Args Template Input */}
                  <FormField
                    label="Arguments Template"
                    helper="Args template — use {'{input}'} and {'{output}'} placeholders"
                    inline={false}
                  >
                    <Input
                      id={`args-${engine}`}
                      type="text"
                      value={local.perEngine[engine]?.argsTemplate ?? ''}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) => updateEngineField(engine, 'argsTemplate', e.target.value)}
                      placeholder="Args template"
                    />
                  </FormField>

                  {/* OrcaSlicer Examples */}
                  {engine === 'OrcaSlicer' && (
                    <div className="mt-2 border-t border-pf-border pt-3">
                      <Button
                        type="button"
                        variant="subtle"
                        size="sm"
                        onClick={() => setOpenExamplesEngine(openExamplesEngine === engine ? null : engine)}
                        className="!justify-start"
                      >
                        {openExamplesEngine === engine ? '▼ Hide examples' : '▶ Show Orca examples'}
                      </Button>
                      {openExamplesEngine === engine && (
                        <div className="panel mt-3 gap-md flex flex-col">
                          <div className="form-helper">Recommended Orca templates (you can edit after inserting):</div>
                          <div className="flex gap-2 flex-wrap">
                            <Button
                              type="button"
                              variant="secondary"
                              size="sm"
                              onClick={() => updateEngineField(engine, 'argsTemplate', '--config "{config}" --output "{output}" {input}')}
                            >
                              Insert config example
                            </Button>
                            <Button
                              type="button"
                              variant="secondary"
                              size="sm"
                              onClick={() => updateEngineField(engine, 'argsTemplate', '--export-gcode -o {output} {input}')}
                            >
                              Insert simple export
                            </Button>
                          </div>
                          <div className="form-helper text-xs">
                            Placeholders: <code className="bg-pf-bg-0 px-1 rounded">{'{'}{'{input}'}</code> – model file; <code className="bg-pf-bg-0 px-1 rounded">{'{'}{'{output}'}</code> – gcode output; <code className="bg-pf-bg-0 px-1 rounded">{'{'}{'{config}'}</code> – generated config path
                          </div>
                        </div>
                      )}
                    </div>
                  )}
                </div>
              </div>
            ))
          )}
        </div>
      </div>

      {/* Jitter Percent Card */}
      <div className="card">
        <div className="card-body">
          <FormField
            label="Retry Jitter Percent"
            helper="Percentage +/- applied to retry backoff delays (e.g. 15 = +/-15%)"
            error={validationError}
            inline={false}
          >
            <Input
              id="jitter-percent"
              type="number"
              step="0.1"
              min={0}
              max={100}
              value={local.jitterPercent ?? 15}
              onChange={(e: React.ChangeEvent<HTMLInputElement>) => onJitterChange(e.target.value)}
            />
          </FormField>
        </div>
      </div>

      {/* Action Footer */}
      <div className="card-footer">
        <Button
          type="button"
          variant="primary"
          size="md"
          onClick={() => local && saveMutation.mutate(local)}
          disabled={!!validationError || !local || saveMutation.status === 'pending'}
        >
          {saveMutation.status === 'pending' ? 'Saving...' : 'Save Settings'}
        </Button>
      </div>

      {/* Error Alert */}
      {(saveMutation.error || saveError) && (
        <div className="alert-base alert-error">
          <div>{saveError ?? (saveMutation.error as Error)?.message ?? 'Failed to save settings'}</div>
        </div>
      )}
    </PageTemplate>
  );
};

export default SlicerSettingsPage;
