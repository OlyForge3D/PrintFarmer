import { useEffect, useState } from 'react';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import { Badge, Button, Input } from '@/common/components/ui';
import type {
  PrinterSensor,
  PrinterSensorQuantity,
  PrinterSensorReading,
  PrinterSensorUnit,
  PrinterSensorWritable,
} from '@/types/api';

type SensorGroup = 'Temperatures' | 'Environment' | 'Fans' | 'Filament';

const GROUP_ORDER: SensorGroup[] = ['Temperatures', 'Environment', 'Fans', 'Filament'];

const UNIT_SUFFIX: Record<PrinterSensorUnit, string> = {
  None: '',
  Celsius: '°C',
  Percent: '%',
  HectoPascal: 'hPa',
  Ohm: 'Ω',
  Rpm: 'RPM',
  Watt: 'W',
};

function getSensorGroup(sensor: PrinterSensor): SensorGroup {
  switch (sensor.kind) {
    case 'Fan':
      return 'Fans';
    case 'FilamentRunout':
      return 'Filament';
    case 'Ambient':
      return 'Environment';
    case 'Nozzle':
    case 'Bed':
    case 'Chamber':
    case 'Mcu':
    case 'Host':
      return 'Temperatures';
    default: {
      const quantities = (sensor.readings ?? []).map((r) => r.quantity);
      if (quantities.some((q) => q === 'FilamentDetected')) return 'Filament';
      if (quantities.some((q) => q === 'FanSpeed' || q === 'FanRpm')) return 'Fans';
      if (quantities.every((q) => q === 'Temperature' || q === 'TargetTemperature')) return 'Temperatures';
      return 'Environment';
    }
  }
}

const QUANTITY_LABEL: Record<PrinterSensorQuantity, string> = {
  Other: 'Value',
  Temperature: 'Temperature',
  TargetTemperature: 'Target',
  Humidity: 'Humidity',
  Pressure: 'Pressure',
  Gas: 'Gas',
  FanSpeed: 'Speed',
  FanRpm: 'RPM',
  Power: 'Power',
  FilamentDetected: 'Filament',
};

function formatSensorReading(reading: PrinterSensorReading): string {
  const { value, quantity, unit } = reading;
  if (typeof value === 'boolean') {
    return quantity === 'FilamentDetected' ? (value ? 'Detected' : 'Empty') : value ? 'On' : 'Off';
  }
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return '—';
  }
  const rounded = Math.round(value * 10) / 10;
  const suffix = UNIT_SUFFIX[unit] ?? '';
  if (!suffix) return String(rounded);
  return unit === 'Celsius' || unit === 'Percent' ? `${rounded}${suffix}` : `${rounded} ${suffix}`;
}

function describeReading(sensor: PrinterSensor, reading: PrinterSensorReading): string {
  return `${sensor.displayName} ${QUANTITY_LABEL[reading.quantity] ?? 'Value'}`;
}

function isStepAligned(value: number, min: number, step: number): boolean {
  const steps = (value - min) / step;
  return Math.abs(steps - Math.round(steps)) <= 1e-6;
}

function baselineOf(sensor: PrinterSensor, descriptor: PrinterSensorWritable): number | undefined {
  const value = (sensor.readings ?? []).find((r) => r.quantity === descriptor.quantity)?.value;
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function sensorKey(sensorId: string, quantity: PrinterSensorQuantity) {
  return `${sensorId}\u0000${quantity}`;
}

interface PrinterSensorsPanelProps {
  printerId: string;
  sensors: PrinterSensor[] | undefined;
  /** Whether the user and backend can send sensor writes (supportsSensorControl). */
  canControl?: boolean;
  isOnline?: boolean;
}

interface WritableControlProps {
  sensor: PrinterSensor;
  descriptor: PrinterSensorWritable;
  current: number | undefined;
  disabled: boolean;
  onSubmit: (sensor: PrinterSensor, descriptor: PrinterSensorWritable, value: number, baseline: number | undefined) => Promise<void>;
}

function WritableControl({ sensor, descriptor, current, disabled, onSubmit }: WritableControlProps) {
  const [draft, setDraft] = useState<string>('');
  const [editing, setEditing] = useState(false);
  const label = `Set ${sensor.displayName} ${QUANTITY_LABEL[descriptor.quantity] ?? 'value'}`;
  const unitText = UNIT_SUFFIX[descriptor.unit];
  const shown = editing ? draft : current === undefined ? '' : String(Math.round(current * 10) / 10);
  const parsed = draft.trim() === '' ? NaN : Number(draft);
  const inRange = Number.isFinite(parsed) && parsed >= descriptor.min && parsed <= descriptor.max;
  const stepAligned = !descriptor.step || descriptor.step <= 0 || isStepAligned(parsed, descriptor.min, descriptor.step);
  const valid = inRange && stepAligned;
  const rangeId = `${sensor.id}-${descriptor.quantity}-range`.replace(/\s+/g, '-');

  const submit = async () => {
    if (!valid || disabled) return;
    await onSubmit(sensor, descriptor, parsed, baselineOf(sensor, descriptor));
    setEditing(false);
  };

  return (
    <div className="flex items-center gap-2">
      <Input
        type="number"
        aria-label={label}
        aria-describedby={rangeId}
        aria-invalid={editing && draft !== '' && !valid ? true : undefined}
        className="w-20"
        min={descriptor.min}
        max={descriptor.max}
        step={descriptor.step ?? 'any'}
        value={shown}
        disabled={disabled}
        onFocus={() => {
          if (!editing) {
            setDraft(current === undefined ? '' : String(current));
            setEditing(true);
          }
        }}
        onChange={(event) => {
          setEditing(true);
          setDraft(event.target.value);
        }}
        onBlur={() => setEditing(false)}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.preventDefault();
            void submit();
          } else if (event.key === 'Escape') {
            setEditing(false);
          }
        }}
      />
      <span id={rangeId} className="text-xs text-pf-text-tertiary">
        {descriptor.min}–{descriptor.max}
        {unitText ? ` ${unitText}` : ''}
        {descriptor.step ? `, step ${descriptor.step}` : ''}
        {editing && draft !== '' && !valid ? ' — invalid value' : ''}
      </span>
      <Button
        size="sm"
        variant="secondary"
        aria-label={`Apply ${label}`}
        disabled={disabled || !valid}
        onClick={() => {
          void submit();
        }}
      >
        Set
      </Button>
    </div>
  );
}

export function PrinterSensorsPanel({ printerId, sensors, canControl = false, isOnline = true }: PrinterSensorsPanelProps) {
  // Optimistic values keyed by sensor+quantity. A failed write rolls back immediately; a successful
  // one is shown until the live reading changes from the value it replaced, after which the
  // authoritative value from the shared printerupdated subscription wins.
  const [optimistic, setOptimistic] = useState<Record<string, { value: number; baseline: number | undefined }>>({});
  const [pending, setPending] = useState<Record<string, boolean>>({});

  // Retire an optimistic entry permanently once the live reading moves off its baseline, so it
  // cannot resurface if the reading later returns to that baseline value.
  useEffect(() => {
    setOptimistic((prev) => {
      const keys = Object.keys(prev);
      if (keys.length === 0) return prev;
      const next = { ...prev };
      let changed = false;
      for (const sensor of sensors ?? []) {
        for (const descriptor of sensor.writable ?? []) {
          const key = sensorKey(sensor.id, descriptor.quantity);
          if (next[key] && baselineOf(sensor, descriptor) !== next[key].baseline) {
            delete next[key];
            changed = true;
          }
        }
      }
      return changed ? next : prev;
    });
  }, [sensors]);

  const handleSubmit = async (
    sensor: PrinterSensor,
    descriptor: PrinterSensorWritable,
    value: number,
    baseline: number | undefined,
  ) => {
    const key = sensorKey(sensor.id, descriptor.quantity);
    const rollback = () =>
      setOptimistic((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
    setOptimistic((prev) => ({ ...prev, [key]: { value, baseline } }));
    setPending((prev) => ({ ...prev, [key]: true }));
    try {
      const result = await apiClient.setPrinterSensor(printerId, sensor.id, {
        quantity: descriptor.quantity,
        value,
      });
      if (result && result.success === false) {
        rollback();
        toast.error(result.error || result.message || `Failed to set ${sensor.displayName}`);
      }
    } catch (error) {
      rollback();
      toast.error(mutationErrorMessage(error, `Failed to set ${sensor.displayName}`));
    } finally {
      setPending((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
    }
  };

  if (!sensors || sensors.length === 0) {
    return <p className="text-sm text-pf-text-secondary">No sensors reported.</p>;
  }

  const grouped = GROUP_ORDER.map((group) => ({
    group,
    items: sensors.filter((sensor) => getSensorGroup(sensor) === group),
  })).filter(({ items }) => items.length > 0);

  return (
    <div className="space-y-4">
      {grouped.map(({ group, items }) => (
        <section key={group} aria-label={group} className="space-y-1">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-pf-text-secondary">{group}</h4>
          <ul className="divide-y divide-pf-border">
            {items.map((sensor) => {
              const unavailable = !sensor.available;
              const writableByQuantity = new Map<PrinterSensorQuantity, PrinterSensorWritable>(
                (sensor.writable ?? []).map((w) => [w.quantity, w]),
              );
              const controllable = canControl && isOnline && !unavailable;
              return (
                <li key={sensor.id} className="py-2 space-y-1" data-testid={`sensor-${sensor.id}`}>
                  <div className="flex items-center justify-between gap-2">
                    <span className={unavailable ? 'text-sm text-pf-text-tertiary' : 'text-sm text-pf-text-primary'}>
                      {sensor.displayName}
                    </span>
                    {unavailable && (
                      <Badge variant="warning" size="sm">
                        Unavailable
                      </Badge>
                    )}
                  </div>
                  <dl className="space-y-1">
                    {(sensor.readings ?? []).map((reading) => {
                      const descriptor = writableByQuantity.get(reading.quantity);
                      const key = sensorKey(sensor.id, reading.quantity);
                      const numeric = typeof reading.value === 'number' && Number.isFinite(reading.value) ? reading.value : undefined;
                      const pendingValue = optimistic[key];
                      const showOptimistic = pendingValue !== undefined && pendingValue.baseline === numeric;
                      const current = showOptimistic ? pendingValue.value : numeric;
                      const shownReading = showOptimistic ? { ...reading, value: pendingValue.value } : reading;
                      return (
                        <div key={reading.quantity} className="flex items-center justify-between gap-2">
                          <dt className="text-xs text-pf-text-secondary">{QUANTITY_LABEL[reading.quantity] ?? 'Value'}</dt>
                          <dd
                            className={unavailable ? 'text-sm text-pf-text-tertiary' : 'text-sm font-medium text-pf-text-primary'}
                            aria-label={`${describeReading(sensor, reading)}: ${formatSensorReading(shownReading)}${unavailable ? ' (unavailable)' : ''}`}
                          >
                            {formatSensorReading(shownReading)}
                          </dd>
                          {descriptor && (
                            <WritableControl
                              sensor={sensor}
                              descriptor={descriptor}
                              current={current}
                              disabled={!controllable || pending[key] === true}
                              onSubmit={handleSubmit}
                            />
                          )}
                        </div>
                      );
                    })}
                  </dl>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}
