import { useState } from 'react';
import { toast } from 'sonner';
import { Card, Button, Input, FormField, Spinner } from '@/common/components/ui';
import { LockIcon } from '@/common/components/icons/MdiIcons';
import { useFarmSettings, useUpdateFarmSettings } from '@/features/settings/hooks/useFarmSettings';
import type { FarmSettingsResponse } from '@/features/settings/types';

export function FarmSettingsSection() {
  const { data, isLoading, error } = useFarmSettings();
  const mutation = useUpdateFarmSettings();

  if (isLoading) return <Spinner className="mx-auto" />;
  if (error) return <div className="text-pf-error">Failed to load farm settings.</div>;
  if (!data) return null;

  return <FarmSettingsForm data={data} mutation={mutation} />;
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

  const canWrite = data.canWrite;

  const handleSave = () => {
    const payload = {
      electricityRatePerKwh: Number(electricityRate),
      defaultMachineHourlyRate: Number(hourlyRate),
      averagePrinterWattage: Number(wattage),
      rowVersion: data.rowVersion,
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
            <span className="inline-flex items-center gap-1 rounded-md bg-pf-bg-2 px-2 py-0.5 text-xs text-pf-text-secondary border border-pf-border">
              <LockIcon className="w-3 h-3" />
              Admin only
            </span>
          )}
        </div>
        <p className="text-sm text-pf-text-secondary mt-1">
          Farm-wide cost and energy defaults. {!canWrite && 'Contact an administrator to change these values.'}
        </p>
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
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
            />
          </FormField>
        </div>
      </Card.Body>
      {canWrite && (
        <Card.Footer>
          <div className="flex justify-end">
            <Button
              variant="primary"
              onClick={handleSave}
              disabled={mutation.isPending}
            >
              {mutation.isPending ? 'Saving...' : 'Save Farm Settings'}
            </Button>
          </div>
        </Card.Footer>
      )}
    </Card>
  );
}
