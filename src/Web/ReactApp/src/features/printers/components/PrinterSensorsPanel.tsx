import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { additionalPrinterSensors } from '@/features/printers/utils/additionalPrinterSensors';
import { formatPrinterSensorName, isHiddenPrinterSensorId } from '@/features/printers/utils/printerSensorDisplay';
import { CollapsibleSection, Input } from '@/common/components/ui';
import type {
  PrinterSensor,
  PrinterSensorQuantity,
  PrinterSensorReading,
  PrinterSensorUnit,
  PrinterSensorWritable,
  PrinterTemperatures,
} from '@/types/api';

export function PrinterSensorsSection({
  temperatures, supportsTelemetry, children, ...props
}: PrinterSensorsPanelProps & { temperatures: PrinterTemperatures; supportsTelemetry?: boolean; children?: ReactNode }) {
  const sensors = additionalPrinterSensors(props.sensors, temperatures)
    .filter(sensor => !isHiddenPrinterSensorId(sensor.id));
  const thermals = sensors.filter(isThermalSensor);
  const remaining = sensors.filter(sensor => !isThermalSensor(sensor));
  return (
    <>
      {(children || thermals.length > 0) && <CollapsibleSection title="Temperatures">
        {children}
        {thermals.length > 0 && <AuthorizedSensorsPanel {...props} sensors={thermals} layout="thermal" />}
      </CollapsibleSection>}
      {(['Fans & outputs', 'Filament', 'Other sensors'] as const).map(group => {
        const items = remaining.filter(sensor => getSensorGroup(sensor) === group);
        return items.length > 0 && <CollapsibleSection key={group} title={group}>
          <AuthorizedSensorsPanel {...props} sensors={items} layout="plain" />
        </CollapsibleSection>;
      })}
      {!sensors.length && !props.sensors?.length && supportsTelemetry &&
        <p className="text-sm text-pf-text-secondary">No sensors reported.</p>}
    </>
  );
}

function AuthorizedSensorsPanel(props: PrinterSensorsPanelProps) {
  const { hasPermission } = useAuth();
  return <PrinterSensorsPanel {...props} canControl={props.canControl && hasPermission('queue', 'start')} />;
}

type SensorGroup = 'Temperatures' | 'Fans & outputs' | 'Filament' | 'Other sensors';

const GROUP_ORDER: SensorGroup[] = ['Temperatures', 'Fans & outputs', 'Filament', 'Other sensors'];

const ENVIRONMENTAL_QUANTITIES = new Set<PrinterSensorQuantity>(['Humidity', 'Pressure', 'Gas']);
const PRIMARY_THERMAL_QUANTITIES = new Set<PrinterSensorQuantity>(['Temperature', 'TargetTemperature', 'Power']);

function isThermalSensor(sensor: PrinterSensor): boolean {
  const knownThermalKind = ['Nozzle', 'Bed', 'Chamber', 'Mcu', 'Host', 'Ambient'].includes(sensor.kind);
  return sensor.kind !== 'Fan' && (
    knownThermalKind ||
    sensor.readings.some(reading => reading.quantity === 'Temperature' || reading.quantity === 'TargetTemperature') ||
    sensor.readings.some(reading => ENVIRONMENTAL_QUANTITIES.has(reading.quantity)) ||
    (sensor.writable ?? []).some(descriptor => descriptor.quantity === 'TargetTemperature'));
}

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
      return 'Fans & outputs';
    case 'FilamentRunout':
      return 'Filament';
    case 'Ambient':
      return 'Temperatures';
    default: {
      const quantities = (sensor.readings ?? []).map((r) => r.quantity);
      if (quantities.some((q) => q === 'FilamentDetected')) return 'Filament';
      if (quantities.some((q) => q === 'FanSpeed' || q === 'FanRpm' || q === 'Power')) return 'Fans & outputs';
      return isThermalSensor(sensor) ? 'Temperatures' : 'Other sensors';
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
  return `${formatPrinterSensorName(sensor.displayName)} ${QUANTITY_LABEL[reading.quantity] ?? 'Value'}`;
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
  layout?: 'thermal' | 'plain';
}

interface WritableControlProps {
  sensor: PrinterSensor;
  descriptor: PrinterSensorWritable;
  current: number | undefined;
  disabled: boolean;
  onSubmit: (sensor: PrinterSensor, descriptor: PrinterSensorWritable, value: number, baseline: number | undefined) => Promise<void>;
}

function WritableControl({ sensor, descriptor, current, disabled, onSubmit }: WritableControlProps) {
  const rangeId = useId();
  const [draft, setDraft] = useState<string>('');
  const [editing, setEditing] = useState(false);
  const active = useRef(false);
  const submitting = useRef(false);
  const initial = useRef(current);
  const displayName = formatPrinterSensorName(sensor.displayName);
  const label = `Set ${displayName} ${QUANTITY_LABEL[descriptor.quantity] ?? 'value'}`;
  const unitText = UNIT_SUFFIX[descriptor.unit];
  const shown = editing ? draft : current === undefined ? '' : String(Math.round(current * 10) / 10);
  const parsed = draft.trim() === '' ? NaN : Number(draft);
  const inRange = Number.isFinite(parsed) && parsed >= descriptor.min && parsed <= descriptor.max;
  const stepAligned = !descriptor.step || descriptor.step <= 0 || isStepAligned(parsed, descriptor.min, descriptor.step);
  const valid = editing && inRange && stepAligned;

  const submit = () => {
    if (!active.current || submitting.current) return;
    active.current = false;
    setEditing(false);
    if (!valid || disabled || parsed === initial.current || parsed === current) return;
    submitting.current = true;
    void onSubmit(sensor, descriptor, parsed, baselineOf(sensor, descriptor)).finally(() => {
      submitting.current = false;
    });
  };

  return (
    <div
      className="flex min-w-0 flex-wrap items-center gap-2"
    >
      <div className="w-[4.5rem]">
        <Input
        type="number"
        aria-label={label}
        aria-describedby={rangeId}
        aria-invalid={editing && draft !== '' && !valid ? true : undefined}
        className="font-mono tabular-nums"
        min={descriptor.min}
        max={descriptor.max}
        step={descriptor.step ?? 'any'}
        value={shown}
        disabled={disabled}
        onFocus={() => {
          if (!editing) {
            active.current = true;
            initial.current = current;
            setDraft(current === undefined ? '' : String(current));
            setEditing(true);
          }
        }}
        onChange={(event) => {
          if (!active.current) initial.current = current;
          active.current = true;
          setEditing(true);
          setDraft(event.target.value);
        }}
        onBlur={submit}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.preventDefault();
            void submit();
          } else if (event.key === 'Escape') {
            active.current = false;
            setEditing(false);
          }
        }}
        />
      </div>
      <span id={rangeId} className={editing ? 'basis-full text-right text-xs text-pf-text-secondary order-last' : 'sr-only'}>
        {descriptor.min}–{descriptor.max}
        {unitText ? ` ${unitText}` : ''}
        {descriptor.step ? `, step ${descriptor.step}` : ''}
        {editing && draft !== '' && !valid ? ' — invalid value' : ''}
      </span>
    </div>
  );
}

export function PrinterSensorsPanel({ printerId, sensors, canControl = false, isOnline = true, layout }: PrinterSensorsPanelProps) {
  const visibleSensors = useMemo(
    () => (sensors ?? []).filter(sensor => !isHiddenPrinterSensorId(sensor.id)),
    [sensors],
  );
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
      for (const sensor of visibleSensors) {
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
  }, [visibleSensors]);

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
        toast.error(result.error || result.message || `Failed to set ${formatPrinterSensorName(sensor.displayName)}`);
      }
    } catch (error) {
      rollback();
      toast.error(mutationErrorMessage(error, `Failed to set ${formatPrinterSensorName(sensor.displayName)}`));
    } finally {
      setPending((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
    }
  };

  if (visibleSensors.length === 0) {
    return <p className="text-sm text-pf-text-secondary">No sensors reported.</p>;
  }

  const grouped = layout ? [{ group: 'Readings', items: visibleSensors }] : GROUP_ORDER.map((group) => ({
    group,
    items: visibleSensors.filter((sensor) => getSensorGroup(sensor) === group),
  })).filter(({ items }) => items.length > 0);

  return (
    <div className="space-y-3">
      {grouped.map(({ group, items }) => (
        <section key={group} aria-label={layout ? undefined : group} className="space-y-1">
          {!layout && <h4 className="text-xs font-medium text-pf-text-secondary">{group}</h4>}
          <ul>
            {items.map((sensor) => {
              const unavailable = !sensor.available;
              const writableByQuantity = new Map<PrinterSensorQuantity, PrinterSensorWritable>(
                (sensor.writable ?? []).map((w) => [w.quantity, w]),
              );
              const controllable = canControl && isOnline && !unavailable;
              if (layout === 'thermal') {
                const additionalReadings = sensor.readings.filter(reading => !PRIMARY_THERMAL_QUANTITIES.has(reading.quantity));
                const cell = (quantity: PrinterSensorQuantity) => {
                  const reading = sensor.readings.find(item => item.quantity === quantity);
                  const descriptor = writableByQuantity.get(quantity);
                  const key = sensorKey(sensor.id, quantity);
                  const numeric = typeof reading?.value === 'number' && Number.isFinite(reading.value) ? reading.value : undefined;
                  const current = optimistic[key]?.baseline === numeric ? optimistic[key]?.value ?? numeric : numeric;
                  return descriptor ? <WritableControl sensor={sensor} descriptor={descriptor} current={current}
                    disabled={!controllable || pending[key] === true} onSubmit={handleSubmit} />
                    : <span className="text-right font-mono tabular-nums text-xs"
                      aria-label={reading ? `${describeReading(sensor, reading)}: ${formatSensorReading(reading)}${unavailable ? ' (unavailable)' : ''}` : undefined}>
                      {reading ? formatSensorReading(reading) : '—'}
                    </span>;
                };
                return <li key={sensor.id} className="py-2" data-testid={`sensor-${sensor.id}`}>
                  <div className="grid grid-cols-[minmax(0,1fr)_2.5rem_3.5rem_4.5rem_minmax(0,1fr)] items-center gap-1">
                    <span className="min-w-0 break-words text-xs text-pf-text-secondary">
                      {formatPrinterSensorName(sensor.displayName)}{unavailable && <span className="block">Unavailable</span>}
                    </span>
                    {cell('Power')}
                    {cell('Temperature')}
                    {cell('TargetTemperature')}
                    <div className="flex min-w-0 flex-wrap items-center justify-end gap-x-2">
                      {additionalReadings.map(reading => (
                        <AdditionalThermalReading
                          key={reading.quantity}
                          sensor={sensor}
                          reading={reading}
                          descriptor={writableByQuantity.get(reading.quantity)}
                          disabled={!controllable}
                          pending={pending[sensorKey(sensor.id, reading.quantity)] === true}
                          optimistic={optimistic[sensorKey(sensor.id, reading.quantity)]}
                          onSubmit={handleSubmit}
                        />
                      ))}
                    </div>
                  </div>
                </li>;
              }

              return (
                <li key={sensor.id} className="py-2 space-y-2 min-w-0" data-testid={`sensor-${sensor.id}`}>
                  {sensor.readings.length !== 1 && <div className="flex items-start justify-between gap-2">
                    <span className={`min-w-0 break-words text-sm ${unavailable ? 'text-pf-text-secondary' : 'text-pf-text-primary'}`}>
                      {formatPrinterSensorName(sensor.displayName)}
                    </span>
                    {unavailable && (
                      <span className="shrink-0 text-xs text-pf-text-secondary">
                        Unavailable
                      </span>
                    )}
                  </div>}
                  <dl className="space-y-1">
                    {(sensor.readings ?? []).map((reading) => {
                      const descriptor = writableByQuantity.get(reading.quantity);
                      const key = sensorKey(sensor.id, reading.quantity);
                      const numeric = typeof reading.value === 'number' && Number.isFinite(reading.value) ? reading.value : undefined;
                      const pendingValue = optimistic[key];
                      const showOptimistic = pendingValue !== undefined && pendingValue.baseline === numeric;
                      const current = showOptimistic ? pendingValue.value : numeric;
                      const shownReading = showOptimistic ? { ...reading, value: pendingValue.value } : reading;
                      const redundantRpmLabel = sensor.readings.length > 1 && reading.quantity === 'FanRpm' &&
                        reading.unit === 'Rpm' && numeric !== undefined && !descriptor;
                      return (
                        <div key={reading.quantity} className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2">
                          <dt className={redundantRpmLabel ? 'sr-only' : 'min-w-0 flex-1 text-sm text-pf-text-secondary break-words'}>
                            {sensor.readings.length === 1 ? formatPrinterSensorName(sensor.displayName) : QUANTITY_LABEL[reading.quantity] ?? 'Value'}
                            {sensor.readings.length === 1 && unavailable && <span className="block text-xs">Unavailable</span>}
                          </dt>
                          <dd
                            className={`ml-auto flex items-center gap-1 font-mono tabular-nums text-sm ${unavailable ? 'text-pf-text-secondary' : 'text-pf-text-primary'}`}
                            aria-label={`${describeReading(sensor, reading)}: ${formatSensorReading(shownReading)}${unavailable ? ' (unavailable)' : ''}`}
                          >
                            {descriptor ? <>
                              <div>
                              <WritableControl
                                sensor={sensor}
                                descriptor={descriptor}
                                current={current}
                                disabled={!controllable || pending[key] === true}
                                onSubmit={handleSubmit}
                              />
                              </div>
                              <span className="text-xs">{UNIT_SUFFIX[descriptor.unit]}</span>
                            </> : formatSensorReading(shownReading)}
                          </dd>
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

interface AdditionalThermalReadingProps {
  sensor: PrinterSensor;
  reading: PrinterSensorReading;
  descriptor: PrinterSensorWritable | undefined;
  disabled: boolean;
  pending: boolean;
  optimistic: { value: number; baseline: number | undefined } | undefined;
  onSubmit: WritableControlProps['onSubmit'];
}

function AdditionalThermalReading({
  sensor, reading, descriptor, disabled, pending, optimistic, onSubmit,
}: AdditionalThermalReadingProps) {
  const numeric = typeof reading.value === 'number' && Number.isFinite(reading.value) ? reading.value : undefined;
  const showOptimistic = optimistic !== undefined && optimistic.baseline === numeric;
  const current = showOptimistic ? optimistic.value : numeric;
  const shownReading = showOptimistic ? { ...reading, value: optimistic.value } : reading;
  const label = `${describeReading(sensor, reading)}: ${formatSensorReading(shownReading)}${sensor.available ? '' : ' (unavailable)'}`;

  return (
    <div className="inline-flex items-center gap-1 text-right text-xs">
      <span className="text-pf-text-secondary">{QUANTITY_LABEL[reading.quantity]} </span>
      {descriptor ? <>
        <WritableControl
          sensor={sensor}
          descriptor={descriptor}
          current={current}
          disabled={disabled || pending}
          onSubmit={onSubmit}
        />
        <span>{UNIT_SUFFIX[descriptor.unit]}</span>
      </> : (
        <span className="font-mono tabular-nums" aria-label={label}>
          {formatSensorReading(shownReading)}
        </span>
      )}
    </div>
  );
}
