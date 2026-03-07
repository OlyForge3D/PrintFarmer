import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Card, Button, Input, Select, FormField, Toggle, Spinner } from '@/common/components/ui';
import { apiClient } from '@/services/api';

export interface DispatchSettings {
  autoDispatchEnabled: boolean;
  autoDispatchMode: string;
  idleThresholdSeconds: number;
  minimumScoreThreshold: number;
  maxConcurrentDispatches: number;
  loadBalancingStrategy: string;
}

const DISPATCH_SETTINGS_KEY = ['dispatch-settings'] as const;

const DISPATCH_MODES = [
  { value: 'Manual', label: 'Manual — Operator controls all dispatch' },
  { value: 'Suggest', label: 'Suggest — Notifications with recommendations' },
  { value: 'Auto', label: 'Auto — Fully automated dispatch' },
];

const LOAD_BALANCING_STRATEGIES = [
  { value: 'BestFit', label: 'Best Fit — Assign to highest-scoring printer' },
  { value: 'RoundRobin', label: 'Round Robin — Distribute evenly across printers' },
  { value: 'LeastBusy', label: 'Least Busy — Prefer printers with shortest queue' },
];

export const DispatchSettingsPanel: React.FC = () => {
  const queryClient = useQueryClient();

  const { data: settings, isLoading, error } = useQuery({
    queryKey: DISPATCH_SETTINGS_KEY,
    queryFn: async () => {
      const response = await apiClient.get<DispatchSettings>('/dispatch-settings');
      return response;
    },
    staleTime: 60_000,
  });

  const [formOverrides, setFormOverrides] = useState<Partial<DispatchSettings>>({});
  const [dirty, setDirty] = useState(false);

  const form: DispatchSettings = {
    autoDispatchEnabled: formOverrides.autoDispatchEnabled ?? settings?.autoDispatchEnabled ?? false,
    autoDispatchMode: formOverrides.autoDispatchMode ?? settings?.autoDispatchMode ?? 'Manual',
    idleThresholdSeconds: formOverrides.idleThresholdSeconds ?? settings?.idleThresholdSeconds ?? 30,
    minimumScoreThreshold: formOverrides.minimumScoreThreshold ?? settings?.minimumScoreThreshold ?? 0.5,
    maxConcurrentDispatches: formOverrides.maxConcurrentDispatches ?? settings?.maxConcurrentDispatches ?? 3,
    loadBalancingStrategy: formOverrides.loadBalancingStrategy ?? settings?.loadBalancingStrategy ?? 'BestFit',
  };

  const saveMutation = useMutation({
    mutationFn: async (data: DispatchSettings) => {
      return apiClient.put<DispatchSettings>('/dispatch-settings', data);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: DISPATCH_SETTINGS_KEY });
      toast.success('Dispatch settings saved');
      setFormOverrides({});
      setDirty(false);
    },
    onError: (err: Error) => {
      toast.error(`Failed to save dispatch settings: ${err.message}`);
    },
  });

  const handleChange = <K extends keyof DispatchSettings>(key: K, value: DispatchSettings[K]) => {
    setFormOverrides(prev => ({ ...prev, [key]: value }));
    setDirty(true);
  };

  const handleSave = () => {
    if (form.idleThresholdSeconds < 5) {
      toast.error('Idle threshold must be at least 5 seconds');
      return;
    }
    if (form.maxConcurrentDispatches < 1) {
      toast.error('Max concurrent dispatches must be at least 1');
      return;
    }
    saveMutation.mutate(form);
  };

  if (isLoading) {
    return (
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold text-pf-text-primary">Dispatch Settings</h2>
        </Card.Header>
        <Card.Body className="flex justify-center py-8">
          <Spinner size="lg" />
        </Card.Body>
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <Card.Header>
          <h2 className="text-lg font-semibold text-pf-text-primary">Dispatch Settings</h2>
        </Card.Header>
        <Card.Body>
          <div className="text-pf-error p-4">
            Failed to load dispatch settings: {String(error)}
          </div>
        </Card.Body>
      </Card>
    );
  }

  return (
    <Card>
      <Card.Header>
        <h2 className="text-lg font-semibold text-pf-text-primary">Dispatch Settings</h2>
      </Card.Header>
      <Card.Body className="space-y-6 p-6">
        <FormField label="Auto Dispatch Enabled" htmlFor="autoDispatchEnabled">
          <Toggle
            id="autoDispatchEnabled"
            checked={form.autoDispatchEnabled}
            onChange={e => handleChange('autoDispatchEnabled', e.target.checked)}
          />
        </FormField>

        <FormField label="Auto Dispatch Mode" htmlFor="autoDispatchMode">
          <Select
            id="autoDispatchMode"
            value={form.autoDispatchMode}
            onChange={e => handleChange('autoDispatchMode', e.target.value)}
            disabled={!form.autoDispatchEnabled}
          >
            {DISPATCH_MODES.map(mode => (
              <option key={mode.value} value={mode.value}>
                {mode.label}
              </option>
            ))}
          </Select>
        </FormField>

        <FormField
          label="Load Balancing Strategy"
          htmlFor="loadBalancingStrategy"
          helper="How jobs are distributed across eligible printers during auto-dispatch."
        >
          <Select
            id="loadBalancingStrategy"
            value={form.loadBalancingStrategy}
            onChange={e => handleChange('loadBalancingStrategy', e.target.value)}
            disabled={!form.autoDispatchEnabled}
          >
            {LOAD_BALANCING_STRATEGIES.map(strategy => (
              <option key={strategy.value} value={strategy.value}>
                {strategy.label}
              </option>
            ))}
          </Select>
        </FormField>

        <FormField
          label="Idle Threshold (seconds)"
          htmlFor="idleThresholdSeconds"
          helper="Time a printer must be idle before auto-dispatch considers it. Minimum 5 seconds."
        >
          <Input
            id="idleThresholdSeconds"
            type="number"
            min={5}
            value={form.idleThresholdSeconds}
            onChange={e => handleChange('idleThresholdSeconds', Number(e.target.value))}
            disabled={!form.autoDispatchEnabled}
          />
        </FormField>

        <FormField
          label="Minimum Score Threshold"
          htmlFor="minimumScoreThreshold"
          helper="Minimum dispatch score (0–1) required for a printer to be considered."
        >
          <Input
            id="minimumScoreThreshold"
            type="number"
            min={0}
            max={1}
            step={0.05}
            value={form.minimumScoreThreshold}
            onChange={e => handleChange('minimumScoreThreshold', Number(e.target.value))}
            disabled={!form.autoDispatchEnabled}
          />
        </FormField>

        <FormField
          label="Max Concurrent Dispatches"
          htmlFor="maxConcurrentDispatches"
          helper="Maximum number of simultaneous auto-dispatch operations."
        >
          <Input
            id="maxConcurrentDispatches"
            type="number"
            min={1}
            value={form.maxConcurrentDispatches}
            onChange={e => handleChange('maxConcurrentDispatches', Number(e.target.value))}
            disabled={!form.autoDispatchEnabled}
          />
        </FormField>
      </Card.Body>
      <Card.Footer className="flex justify-end gap-3 p-4">
        <Button
          variant="primary"
          onClick={handleSave}
          loading={saveMutation.isPending}
          disabled={!dirty || saveMutation.isPending}
        >
          Save Settings
        </Button>
      </Card.Footer>
    </Card>
  );
};
