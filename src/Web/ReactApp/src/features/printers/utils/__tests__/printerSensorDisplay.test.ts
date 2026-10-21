import { describe, expect, it } from 'vitest';
import { formatPrinterSensorName, isHiddenPrinterSensorId } from '@/features/printers/utils/printerSensorDisplay';

describe('printer sensor display helpers', () => {
  it.each([
    ['heater_box1', 'Heater Box1'],
    ['Chamber_Thermal_Protection_Sensor', 'Chamber Thermal Protection Sensor'],
    ['GD32', 'GD32'],
    ['heater_temp_a_box1', 'Heater Temp A Box1'],
  ])('formats %s as %s without changing the remaining character casing', (input, expected) => {
    expect(formatPrinterSensorName(input)).toBe(expected);
  });

  it.each([
    ['temperature_sensor _hidden', true],
    ['temperature_sensor _hidden zone', true],
    ['heater_generic heater_box1', false],
    ['_hidden', true],
    ['temperature_sensor heater_box1', false],
  ])('identifies hidden status for %s', (id, hidden) => {
    expect(isHiddenPrinterSensorId(id)).toBe(hidden);
  });
});
