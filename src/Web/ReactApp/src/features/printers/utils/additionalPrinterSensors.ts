import type { PrinterSensor, PrinterTemperatures } from '@/types/api';

// Exact backend mapper identities, not display names: tool1/T1/extruder1 remain independent.
const PRIMARY_NOZZLES = new Set(['nozzle', 'extruder', 'tool0', 'T0', 'TempOfNozzle']);
const PRIMARY_BEDS = new Set(['bed', 'heater_bed', 'TempOfHotbed']);

export function primaryHeater(sensor: PrinterSensor): 'hotend' | 'bed' | undefined {
  if (sensor.kind === 'Nozzle' && PRIMARY_NOZZLES.has(sensor.id)) return 'hotend';
  if (sensor.kind === 'Bed' && PRIMARY_BEDS.has(sensor.id)) return 'bed';
  return undefined;
}

export function primaryHeaterLimits(sensors: PrinterSensor[] | undefined, heater: 'hotend' | 'bed', persistedMax?: number) {
  const descriptor = sensors?.find(sensor => primaryHeater(sensor) === heater)?.writable?.find(item => item.quantity === 'TargetTemperature');
  const min = descriptor?.min ?? 0;
  const max = [descriptor?.max, persistedMax].find(value =>
    value !== undefined && Number.isFinite(value) && value >= 0 && value >= min);
  return { min, max, step: descriptor && max !== undefined ? descriptor.step : 1 };
}

export function primaryHeaterPower(sensors: PrinterSensor[] | undefined, heater: 'hotend' | 'bed'): string | undefined {
  const power = sensors?.find(sensor => primaryHeater(sensor) === heater)?.readings.find(reading => reading.quantity === 'Power');
  if (typeof power?.value !== 'number' || !Number.isFinite(power.value)) return undefined;
  const suffix = power.unit === 'Percent' ? '%' : power.unit === 'Watt' ? ' W' : '';
  return `${Math.round(power.value * 10) / 10}${suffix}`;
}

export function additionalPrinterSensors(sensors: PrinterSensor[] | undefined, temperatures: PrinterTemperatures): PrinterSensor[] {
  return (sensors ?? []).flatMap((sensor) => {
    const primary = primaryHeater(sensor) === 'hotend'
      ? { current: temperatures.hotendTemp, target: temperatures.hotendTarget }
      : primaryHeater(sensor) === 'bed'
        ? { current: temperatures.bedTemp, target: temperatures.bedTarget }
        : undefined;
    if (!primary) return [sensor];
    const readings = sensor.readings.filter((reading) =>
      !(reading.quantity === 'Temperature' && Number.isFinite(primary.current)) &&
      !(reading.quantity === 'TargetTemperature' && Number.isFinite(primary.target)) &&
      !(reading.quantity === 'Power' && (Number.isFinite(primary.current) || Number.isFinite(primary.target))));
    if (readings.length === sensor.readings.length) return [sensor];
    return readings.length ? [{ ...sensor, readings }] : [];
  });
}
