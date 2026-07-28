import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Toggle } from '@/common/components/ui/Toggle';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import { apiClient } from '@/services/api';

const DISPATCH_SETTINGS_KEY = ['dispatch-settings'] as const;

interface DispatchSettingsResponse {
  eTag?: string | null;
  autoDispatchEnabled: boolean;
  autoDispatchMode: string;
  idleThresholdSeconds: number;
  minimumScoreThreshold: number;
  maxConcurrentDispatches: number;
  loadBalancingStrategy: string;
  updatedAt: string;
}

export function AutoDispatchGlobalToggle() {
  const queryClient = useQueryClient();
  const [reviewRequired, setReviewRequired] = useState(false);

  const { data: settings, isError } = useQuery<DispatchSettingsResponse>({
    queryKey: DISPATCH_SETTINGS_KEY,
    queryFn: async () => {
      const response =
        await apiClient.get<DispatchSettingsResponse>('/dispatch-settings');
      return response.data;
    },
    staleTime: 30_000,
  });

  const toggleMutation = useMutation({
    mutationFn: async (enabled: boolean) => {
      if (!settings) return;
      if (!settings.eTag) {
        throw new Error('Dispatch settings changed. Refresh before confirming.');
      }
      const response = await apiClient.put<DispatchSettingsResponse>(
        '/dispatch-settings',
        {
          ...settings,
          autoDispatchEnabled: enabled,
          autoDispatchMode: enabled ? 'Auto' : 'Manual',
        },
        { headers: { 'If-Match': `"${settings.eTag}"` } }
      );
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: DISPATCH_SETTINGS_KEY });
    },
  });

  const isEnabled = settings?.autoDispatchEnabled ?? false;

  const handleToggle = async () => {
    if (reviewRequired) {
      const confirmed = window.confirm(
        'Dispatch settings changed after your previous attempt. Confirm this change using the refreshed settings?'
      );
      if (!confirmed) return;
      setReviewRequired(false);
    }

    const newEnabled = !isEnabled;
    try {
      await toggleMutation.mutateAsync(newEnabled);
      toast.success(
        newEnabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'
      );
    } catch (error) {
      const status = mutationErrorStatus(error);
      if (status === 412 || status === 428) {
        setReviewRequired(true);
        await queryClient.invalidateQueries({
          queryKey: DISPATCH_SETTINGS_KEY,
        });
        await queryClient.refetchQueries({
          queryKey: DISPATCH_SETTINGS_KEY,
          exact: true,
          type: 'active',
        });
      }
      toast.error(
        mutationErrorMessage(error, 'Failed to update auto-dispatch')
      );
    }
  };

  if (isError) {
    return (
      <div className="flex items-center gap-2 shrink-0">
        <Toggle
          checked={false}
          onChange={() => {}}
          disabled
          size="sm"
          aria-label="Auto-dispatch unavailable"
        />
        <span className="text-xs text-pf-text-secondary">
          Auto-dispatch unavailable
        </span>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-2 shrink-0">
      <Toggle
        checked={isEnabled}
        onChange={handleToggle}
        disabled={toggleMutation.isPending || !settings}
        size="sm"
        aria-label="Toggle system auto-dispatch"
      />
      <span className="text-xs font-medium text-pf-text-primary">
        Auto-dispatch
      </span>
    </div>
  );
}
