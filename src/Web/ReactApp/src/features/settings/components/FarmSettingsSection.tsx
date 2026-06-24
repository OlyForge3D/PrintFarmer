import { useState } from 'react';
import { toast } from 'sonner';
import { AlertCircleIcon, LockIcon } from '@/common/components/icons/MdiIcons';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Alert, Button, Card, FormField, Input } from '@/common/components/ui';
import { useFarmSettings, useUpdateFarmSettings } from '@/features/settings/hooks/useFarmSettings';
import type { FarmSettingsResponse } from '@/features/settings/types';

const disabledInputClassName = 'disabled:border-pf-border disabled:bg-pf-bg-2 disabled:text-pf-text-secondary disabled:cursor-not-allowed';

export function FarmSettingsSection() {
  const { data, isLoading, error, refetch, isFetching } = useFarmSettings();
  const mutation = useUpdateFarmSettings();

  if (isLoading) {
    return <FarmSettingsSkeleton />;
  }

  if (error) {
    return (
      <Alert type="error" title="Unable to load farm settings">
        <div className="flex items-start gap-3">
          <AlertCircleIcon className="mt-0.5 h-5 w-5 shrink-0" ariaLabel="Error" />
          <div className="space-y-3">
            <p>Farm-wide defaults could not be loaded right now.</p>
            <Button type="button" variant="secondary" size="sm" loading={isFetching} onClick={() => void refetch()}>
              Retry
            </Button>
          </div>
        </div>
      </Alert>
    );
  }

  if (!data) {
    return null;
  }

  return <FarmSettingsForm data={data} mutation={mutation} />;
}

function FarmSettingsSkeleton() {
  return (
    <Card>
      <Card.Header>
        <Skeleton width="28%" />
        <Skeleton width="52%" />
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          {Array.from({ length: 3 }).map((_, index) => (
            <div key={`farm-settings-skeleton-${index}`} className="space-y-2">
              <Skeleton width="55%" />
              <Skeleton height={40} />
            </div>
          ))}
        </div>
      </Card.Body>
      <Card.Footer>
        <div className="flex justify-end">
          <Skeleton width="160px" height={40} />
        </div>
      </Card.Footer>
    </Card>
  );
}

function FarmSettingsForm({
  data,
  mutation,
}: {
  data: FarmSettingsResponse;
  mutation: ReturnType<typeof useUpdateFarmSettings>;
}) {
  const [electricityRate, setElectricityRate] = useState(String(data.electricityRatePerKwh));
  const [hourlyRate, setHourlyRate] = useState(String(data.defaultMachineHourlyRate));
  const [wattage, setWattage] = useState(String(data.averagePrinterWattage));
  const [slicerMode, setSlicerMode] = useState<'Simple' | 'Advanced'>(data.slicerMode ?? 'Simple');

  const canWrite = data.canWrite;

  const handleSave = () => {
    const payload = {
      electricityRatePerKwh: Number(electricityRate),
      defaultMachineHourlyRate: Number(hourlyRate),
      averagePrinterWattage: Number(wattage),
      rowVersion: data.rowVersion,
      slicerMode,
    };

    if (payload.electricityRatePerKwh < 0 || payload.electricityRatePerKwh > 10) {
      toast.error('Electricity rate must be between 0 and 10.');
      return;
    }
    if (payload.defaultMachineHourlyRate < 0 || payload.defaultMachineHourlyRate > 100) {
      toast.error('Machine hourly rate must be between 0 and 100.');
      return;
    }
    if (payload.averagePrinterWattage < 0 || payload.averagePrinterWattage > 5000) {
      toast.error('Average wattage must be between 0 and 5000.');
      return;
    }

    mutation.mutate(payload, {
      onSuccess: () => toast.success('Farm settings saved.'),
    });
  };

  return (
    <Card>
      <Card.Header>
        <div className="flex items-center gap-2">
          <h2 className="text-lg font-semibold text-pf-text-primary">Farm Settings</h2>
          {!canWrite && (
            <span className="inline-flex items-center gap-1 rounded-md border border-pf-border bg-pf-bg-2 px-2 py-0.5 text-xs text-pf-text-secondary">
              <LockIcon className="h-3 w-3" />
              Admin only
            </span>
          )}
        </div>
        <p className="mt-1 text-sm text-pf-text-secondary">
          Farm-wide cost and energy defaults. {!canWrite && 'Contact an administrator to change these values.'}
        </p>
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <FormField label="Electricity Rate (per kWh)">
            <Input
              type="number"
              min={0}
              max={10}
              step={0.01}
              value={electricityRate}
              onChange={(e) => setElectricityRate(e.target.value)}
              disabled={!canWrite}
              aria-label="Electricity rate per kWh"
              className={!canWrite ? disabledInputClassName : undefined}
            />
          </FormField>
          <FormField label="Default Machine Hourly Rate">
            <Input
              type="number"
              min={0}
              max={100}
              step={0.01}
              value={hourlyRate}
              onChange={(e) => setHourlyRate(e.target.value)}
              disabled={!canWrite}
              aria-label="Default machine hourly rate"
              className={!canWrite ? disabledInputClassName : undefined}
            />
          </FormField>
          <FormField label="Average Printer Wattage">
            <Input
              type="number"
              min={0}
              max={5000}
              step={1}
              value={wattage}
              onChange={(e) => setWattage(e.target.value)}
              disabled={!canWrite}
              aria-label="Average printer wattage"
              className={!canWrite ? disabledInputClassName : undefined}
            />
          </FormField>
        </div>

        <div className="mt-4 border-t border-pf-border pt-4">
          <FormField
            label="Browser Slicer Mode"
            helper="Simple exposes only profile selection and basic print overrides. Advanced unlocks the full OrcaSlicer parameter editor."
          >
            <div className="flex gap-2">
              {(['Simple', 'Advanced'] as const).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  disabled={!canWrite}
                  onClick={() => canWrite && setSlicerMode(mode)}
                  className={[
                    'flex-1 rounded-md border px-3 py-1.5 text-sm font-medium transition-colors',
                    slicerMode === mode
                      ? 'border-pf-accent bg-pf-accent/10 text-pf-accent'
                      : 'border-pf-border bg-pf-bg-2 text-pf-text-secondary hover:border-pf-accent/50 hover:text-pf-text-primary',
                    !canWrite && 'cursor-not-allowed opacity-50',
                  ].join(' ')}
                  aria-pressed={slicerMode === mode}
                >
                  {mode}
                </button>
              ))}
            </div>
          </FormField>
        </div>
      </Card.Body>
      {canWrite ? (
        <Card.Footer>
          <div className="flex justify-end">
            <Button variant="primary" onClick={handleSave} disabled={mutation.isPending}>
              {mutation.isPending ? 'Saving...' : 'Save Farm Settings'}
            </Button>
          </div>
        </Card.Footer>
      ) : null}
    </Card>
  );
}
