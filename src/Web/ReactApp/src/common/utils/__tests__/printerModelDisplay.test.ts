import { describe, it, expect } from 'vitest';
import { formatPrinterModelSubtitle } from '../printerModelDisplay';

describe('formatPrinterModelSubtitle', () => {
  it('collapses the catalog "Unknown" manufacturer + "Unknown Model" model pair into a single label', () => {
    expect(formatPrinterModelSubtitle('Unknown', 'Unknown Model')).toBe('Unknown model');
  });

  it('is case-insensitive when detecting the unknown sentinel values', () => {
    expect(formatPrinterModelSubtitle('unknown', 'UNKNOWN MODEL')).toBe('Unknown model');
  });

  it('collapses missing (undefined) manufacturer/model metadata into the same coherent fallback', () => {
    expect(formatPrinterModelSubtitle(undefined, undefined)).toBe('Unknown model');
  });

  it('collapses missing (null) manufacturer/model metadata into the same coherent fallback', () => {
    expect(formatPrinterModelSubtitle(null, null)).toBe('Unknown model');
  });

  it('collapses blank/whitespace-only manufacturer/model metadata into the same coherent fallback', () => {
    expect(formatPrinterModelSubtitle('  ', '   ')).toBe('Unknown model');
  });

  it('renders both fields together when real manufacturer/model metadata is present', () => {
    expect(formatPrinterModelSubtitle('Prusa', 'MK4')).toBe('Prusa MK4');
  });

  it('renders only the model when the manufacturer is unknown but the model is known', () => {
    expect(formatPrinterModelSubtitle('Unknown', 'MK4')).toBe('MK4');
  });

  it('renders only the manufacturer when the model is unknown but the manufacturer is known', () => {
    expect(formatPrinterModelSubtitle('Prusa', 'Unknown Model')).toBe('Prusa');
  });

  it('never renders the duplicated "Unknown Unknown Model" string regardless of input combination', () => {
    const combos: [string | null | undefined, string | null | undefined][] = [
      ['Unknown', 'Unknown Model'],
      [undefined, 'Unknown Model'],
      ['Unknown', undefined],
      [null, null],
    ];

    for (const [manufacturer, model] of combos) {
      expect(formatPrinterModelSubtitle(manufacturer, model)).not.toMatch(/unknown.*unknown/i);
    }
  });
});
