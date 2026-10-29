import type { PrinterSensor, PrinterTemperatures } from '@/types/api';

// Exact backend mapper identities, not display names: tool1/T1/extruder1 remain independent.
const PRIMARY_NOZZLES = new Set(['nozzle', 'extruder', 'tool0', 'T0', 'TempOfNozzle']);
const PRIMARY_BEDS = new Set(['bed', 'heater_bed', 'TempOfHotbed']);

export function primaryHeater(sensor: PrinterSensor, activeExtruderId?: string | null): 'hotend' | 'bed' | undefined {
  if (sensor.kind === 'Nozzle' && (activeExtruderId === undefined
    ? PRIMARY_NOZZLES.has(sensor.id) : sensor.id === activeExtruderId)) return 'hotend';
  if (sensor.kind === 'Bed' && PRIMARY_BEDS.has(sensor.id)) return 'bed';
  return undefined;
}

export function primaryHeaterLimits(sensors: PrinterSensor[] | undefined, heater: 'hotend' | 'bed', persistedMax?: number, activeExtruderId?: string | null) {
  const descriptor = sensors?.find(sensor => primaryHeater(sensor, activeExtruderId) === heater)?.writable?.find(item => item.quantity === 'TargetTemperature');
  const min = descriptor?.min ?? 0;
  const max = [descriptor?.max, persistedMax].find(value =>
    value !== undefined && Number.isFinite(value) && value >= 0 && value >= min);
  return { min, max, step: descriptor && max !== undefined ? descriptor.step : 1, allowZero: descriptor?.allowZero ?? false };
}

export function primaryHeaterPower(sensors: PrinterSensor[] | undefined, heater: 'hotend' | 'bed', activeExtruderId?: string | null): string | undefined {
  const power = sensors?.find(sensor => primaryHeater(sensor, activeExtruderId) === heater)?.readings.find(reading => reading.quantity === 'Power');
  if (typeof power?.value !== 'number' || !Number.isFinite(power.value)) return undefined;
  const suffix = power.unit === 'Percent' ? '%' : power.unit === 'Watt' ? ' W' : '';
  return `${Math.round(power.value * 10) / 10}${suffix}`;
}

export function additionalPrinterSensors(sensors: PrinterSensor[] | undefined, temperatures: PrinterTemperatures): PrinterSensor[] {
  return (sensors ?? []).flatMap((sensor) => {
    const primary = primaryHeater(sensor, temperatures.activeExtruderId) === 'hotend'
      ? { current: temperatures.hotendTemp, target: temperatures.hotendTarget }
      : primaryHeater(sensor, temperatures.activeExtruderId) === 'bed'
        ? { current: temperatures.bedTemp, target: temperatures.bedTarget }
        : undefined;
    if (!primary) return [sensor];
    const readings = sensor.readings.filter((reading) => temperatures.activeExtruderId !== undefined
      ? !['Temperature', 'TargetTemperature', 'Power'].includes(reading.quantity)
      : !(reading.quantity === 'Temperature' && Number.isFinite(primary.current)) &&
        !(reading.quantity === 'TargetTemperature' && Number.isFinite(primary.target)) &&
        !(reading.quantity === 'Power' && (Number.isFinite(primary.current) || Number.isFinite(primary.target))));
    if (readings.length === sensor.readings.length) return [sensor];
    return readings.length ? [{ ...sensor, readings }] : [];
  });
}
