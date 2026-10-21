import { useId, useMemo, useState, type ReactNode } from 'react';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { additionalPrinterSensors } from '@/features/printers/utils/additionalPrinterSensors';
import { formatPrinterSensorName, isHiddenPrinterSensorId } from '@/features/printers/utils/printerSensorDisplay';
import { CollapsibleSection } from '@/common/components/ui';
import { PrinterValueControl, type PrinterValueStatus } from '@/features/printers/components/PrinterValueControl';
import type {
  PrinterSensor, PrinterSensorQuantity, PrinterSensorReading, PrinterSensorUnit, PrinterSensorWritable, PrinterTemperatures,
} from '@/types/api';

interface PrinterSensorsPanelProps {
  printerId: string;
  sensors: PrinterSensor[] | undefined;
  canControl?: boolean;
  isOnline?: boolean;
  layout?: 'thermal' | 'plain';
  showThermalHeading?: boolean;
}

type SensorGroup = 'Temperatures' | 'Fans & outputs' | 'Filament' | 'Other sensors';
const GROUP_ORDER: SensorGroup[] = ['Temperatures', 'Fans & outputs', 'Filament', 'Other sensors'];
const ENVIRONMENTAL_QUANTITIES = new Set<PrinterSensorQuantity>(['Humidity', 'Pressure', 'Gas']);
const UNIT_SUFFIX: Record<PrinterSensorUnit, string> = {
  None: '', Celsius: '°C', Percent: '%', HectoPascal: 'hPa', Ohm: 'Ω', Rpm: 'RPM', Watt: 'W',
};
const QUANTITY_LABEL: Record<PrinterSensorQuantity, string> = {
  Other: 'Value', Temperature: 'Temperature', TargetTemperature: 'Target', Humidity: 'Humidity',
  Pressure: 'Pressure', Gas: 'Gas', FanSpeed: 'Speed', FanRpm: 'RPM', Power: 'Power', FilamentDetected: 'Filament',
};

function isThermalSensor(sensor: PrinterSensor): boolean {
  return ['Nozzle', 'Bed', 'Chamber', 'Mcu', 'Host', 'Ambient'].includes(sensor.kind) ||
    sensor.readings.some(reading => reading.quantity === 'Temperature' || reading.quantity === 'TargetTemperature' || ENVIRONMENTAL_QUANTITIES.has(reading.quantity)) ||
    (sensor.writable ?? []).some(descriptor => descriptor.quantity === 'TargetTemperature');
}

function getSensorGroup(sensor: PrinterSensor): SensorGroup {
  if (isThermalSensor(sensor)) return 'Temperatures';
  if (sensor.kind === 'Fan' || sensor.readings.some(r => ['FanSpeed', 'FanRpm', 'Power'].includes(r.quantity)) ||
    sensor.writable?.some(isOutputDescriptor)) return 'Fans & outputs';
  if (sensor.kind === 'FilamentRunout' || sensor.readings.some(r => r.quantity === 'FilamentDetected')) return 'Filament';
  return 'Other sensors';
}

function isOutputDescriptor(descriptor: PrinterSensorWritable): boolean {
  return ['FanSpeed', 'Power'].includes(descriptor.quantity) ||
    (descriptor.quantity === 'Other' && (descriptor.unit === 'Percent' || isBinaryDescriptor(descriptor)));
}

function isBinaryDescriptor(descriptor: PrinterSensorWritable): boolean {
  return descriptor.unit === 'None' && descriptor.min === 0 && descriptor.max === 1 && descriptor.step === 1;
}

function numericReading(reading: PrinterSensorReading | undefined): number | undefined {
  return typeof reading?.value === 'number' && Number.isFinite(reading.value) ? reading.value : undefined;
}

function formatSensorReading(reading: PrinterSensorReading): string {
  if (typeof reading.value === 'boolean') {
    return reading.quantity === 'FilamentDetected' ? (reading.value ? 'Detected' : 'Empty') : reading.value ? 'On' : 'Off';
  }
  const value = numericReading(reading);
  if (value === undefined) return '';
  const rounded = Math.round(value * 10) / 10;
  const suffix = UNIT_SUFFIX[reading.unit];
  return `${rounded}${suffix && !['Celsius', 'Percent'].includes(reading.unit) ? ' ' : ''}${suffix}`;
}

export function PrinterSensorsSection({
  temperatures, supportsTelemetry, children, ...props
}: PrinterSensorsPanelProps & { temperatures: PrinterTemperatures; supportsTelemetry?: boolean; children?: ReactNode }) {
  const { hasPermission } = useAuth();
  const sensors = additionalPrinterSensors(props.sensors, temperatures).filter(sensor => !isHiddenPrinterSensorId(sensor.id));
  const thermals = sensors.filter(isThermalSensor);
  const remaining = sensors.filter(sensor => !isThermalSensor(sensor));
  const authorized = { ...props, canControl: props.canControl && hasPermission('queue', 'start') };
  return <>
    {(children || thermals.length > 0) && <CollapsibleSection title="Temperatures">
      {children}
      {thermals.length > 0 && <PrinterSensorsPanel {...authorized} sensors={thermals} layout="thermal" showThermalHeading={!children} />}
    </CollapsibleSection>}
    {GROUP_ORDER.slice(1).map(group => {
      const items = remaining.filter(sensor => getSensorGroup(sensor) === group);
      return items.length > 0 && <CollapsibleSection key={group} title={group}>
        <PrinterSensorsPanel {...authorized} sensors={items} layout="plain" />
      </CollapsibleSection>;
    })}
    {!sensors.length && !props.sensors?.length && supportsTelemetry && <p className="text-sm text-pf-text-secondary">No sensors reported.</p>}
  </>;
}

export function PrinterThermalTable({ children, showHeading = true }: { children: ReactNode; showHeading?: boolean }) {
  return <table aria-label="Temperature readings"
    className="w-full table-fixed text-xs [&_tr]:border-0! [&_td]:p-0! [&_thead]:border-0! [&_thead]:bg-transparent!">
    <colgroup><col /><col className="w-10" /><col className="w-16" /><col className="w-20" /></colgroup>
    <thead className={showHeading ? 'text-pf-text-secondary' : 'sr-only'}>
      <tr>{['Name', 'State', 'Current', 'Target'].map(name =>
        <th key={name} scope="col" className={`p-0! pb-2! font-medium! normal-case! tracking-normal! text-pf-text-secondary! ${name === 'Name' ? 'text-left!' : 'text-right!'}`}>{name}</th>)}</tr>
    </thead>
    <tbody>{children}</tbody>
  </table>;
}

export function PrinterThermalRow({ name, state, current, target, testId, warning }: {
  name: ReactNode; state: ReactNode; current: ReactNode;
  target: (status: PrinterValueStatus) => ReactNode; testId?: string; warning?: string;
}) {
  const id = useId();
  const [message, setMessage] = useState({ text: '', error: false });
  const status = useMemo(() => ({ id, onChange: (text: string, error: boolean) => setMessage({ text, error }) }), [id]);
  return <>
    <tr data-testid={testId} className="h-11">
      <th scope="row" className="py-2 pr-1 text-left font-medium">{name}</th>
      <td className="text-right text-pf-text-secondary">{state}</td>
      <td className="text-right">{current}</td>
      <td className="py-1 pl-1">{target(status)}</td>
    </tr>
    <tr><td colSpan={4}>
      <p id={id} role="status" className={`empty:hidden py-1 text-xs break-words ${message.error ? 'text-pf-error' : 'text-pf-text-secondary'}`}>
        {warning && <span>{warning}</span>}
        {warning && message.text && <br />}
        {message.text && <span>{message.text}</span>}
      </p>
    </td></tr>
  </>;
}

function SensorName({ sensor }: { sensor: PrinterSensor }) {
  const role = sensor.id.startsWith('heater_generic ') ? 'Heater' : sensor.id.startsWith('temperature_sensor ') ? 'Sensor' : '';
  return <span className="min-w-0 break-words [overflow-wrap:anywhere]">
    {formatPrinterSensorName(sensor.displayName)}
    {role && <span className="block text-[10px] font-normal text-pf-text-secondary">{role}</span>}
    {!sensor.available && <span className="block text-xs text-pf-text-secondary">Unavailable</span>}
  </span>;
}

export function PrinterSensorsPanel({
  printerId, sensors, canControl = false, isOnline = true, layout, showThermalHeading = true,
}: PrinterSensorsPanelProps) {
  const visible = (sensors ?? []).filter(sensor => !isHiddenPrinterSensorId(sensor.id));
  if (!visible.length) return <p className="text-sm text-pf-text-secondary">No sensors reported.</p>;
  const groups = layout ? [{ group: 'Readings', items: visible }] : GROUP_ORDER.map(group => ({
    group, items: visible.filter(sensor => getSensorGroup(sensor) === group),
  })).filter(({ items }) => items.length);

  const readingCell = (sensor: PrinterSensor, reading: PrinterSensorReading | undefined) => {
    if (!reading) return null;
    const text = formatSensorReading(reading);
    return text ? <span className="font-mono tabular-nums"
      aria-label={`${formatPrinterSensorName(sensor.displayName)} ${QUANTITY_LABEL[reading.quantity]}: ${text}${sensor.available ? '' : ' (unavailable)'}`}>{text}</span> : null;
  };
  const control = (sensor: PrinterSensor, descriptor: PrinterSensorWritable, children?: ReactNode, mode?: 'slider' | 'switch', status?: PrinterValueStatus) => {
    const reading = sensor.readings.find(r => r.quantity === descriptor.quantity);
    const current = typeof reading?.value === 'boolean' ? Number(reading.value) : numericReading(reading);
    return <PrinterValueControl key={`${printerId}-${sensor.id}-${descriptor.quantity}`}
      label={`Set ${formatPrinterSensorName(sensor.displayName)} ${QUANTITY_LABEL[descriptor.quantity]}`}
      current={current} min={descriptor.min} max={descriptor.max} step={descriptor.step} unit={UNIT_SUFFIX[descriptor.unit]}
      disabled={!canControl || !isOnline || !sensor.available} mode={mode} status={status}
      onSubmit={value => apiClient.setPrinterSensor(printerId, sensor.id, { quantity: descriptor.quantity, value })}>
      {children}
    </PrinterValueControl>;
  };
  const quantityCell = (sensor: PrinterSensor, quantity: PrinterSensorQuantity, status?: PrinterValueStatus) => {
    const descriptor = sensor.writable?.find(w => w.quantity === quantity);
    return descriptor ? control(sensor, descriptor, undefined, undefined, status) : readingCell(sensor, sensor.readings.find(r => r.quantity === quantity));
  };
  const extraReadings = (sensor: PrinterSensor, excluded: Set<PrinterSensorQuantity>) => {
    const quantities = new Set([...sensor.readings.map(r => r.quantity), ...(sensor.writable ?? []).map(w => w.quantity)]);
    return [...quantities].filter(q => !excluded.has(q) &&
      (sensor.writable?.some(w => w.quantity === q) || sensor.readings.some(r => r.quantity === q && formatSensorReading(r) !== ''))).map(quantity =>
      <div key={quantity} className="flex flex-wrap items-center gap-1 text-xs font-normal text-pf-text-secondary">
        <span>{QUANTITY_LABEL[quantity]}</span>{quantityCell(sensor, quantity)}
      </div>);
  };
  return <div className="min-w-0 space-y-3">
    {groups.map(({ group, items }) => {
      const thermal = layout === 'thermal' || group === 'Temperatures';
      return <section key={group} aria-label={layout ? undefined : group} className="min-w-0 space-y-1">
        {!layout && <h4 className="text-xs font-medium text-pf-text-secondary">{group}</h4>}
        {thermal ? <PrinterThermalTable showHeading={showThermalHeading}>
          {items.map(sensor => {
            const stateQuantity = sensor.readings.some(r => r.quantity === 'Power') ? 'Power' : 'FanSpeed';
            return <PrinterThermalRow key={sensor.id} testId={`sensor-${sensor.id}`}
              name={<><SensorName sensor={sensor} />{extraReadings(sensor, new Set(['Temperature', 'TargetTemperature', stateQuantity]))}</>}
              state={quantityCell(sensor, stateQuantity)} current={quantityCell(sensor, 'Temperature')}
              target={status => quantityCell(sensor, 'TargetTemperature', status)} />;
          })}
        </PrinterThermalTable> : <ul>
          {items.map(sensor => {
            const output = sensor.writable?.find(isOutputDescriptor);
            const rpm = sensor.readings.find(r => r.quantity === 'FanRpm');
            const prefix = <>
              <span className="min-w-0 flex-1 text-sm font-medium"><SensorName sensor={sensor} /></span>
              <span className="text-xs text-pf-text-secondary">{readingCell(sensor, rpm)}</span>
            </>;
            const binary = output && isBinaryDescriptor(output);
            const slider = output?.unit === 'Percent' || sensor.kind === 'Fan';
            return <li key={sensor.id} data-testid={`sensor-${sensor.id}`} className="min-w-0 py-2">
              {output ? control(sensor, output, prefix, binary ? 'switch' : slider ? 'slider' : undefined) :
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                  {prefix}
                  {sensor.kind === 'Fan' && readingCell(sensor, sensor.readings.find(r => r.quantity === 'FanSpeed'))}
                  {extraReadings(sensor, new Set(sensor.kind === 'Fan' ? ['FanRpm', 'FanSpeed'] : ['FanRpm']))}
                </div>}
              {output && extraReadings(sensor, new Set([output.quantity, 'FanRpm']))}
            </li>;
          })}
        </ul>}
      </section>;
    })}
  </div>;
}
