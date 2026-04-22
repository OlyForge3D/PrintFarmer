import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MetadataSettingRow } from '@/features/slicer/components/settings/MetadataSettingRow';
import type { SettingMetadata, FieldRef } from '@/features/slicer/components/settings/metadataTypes';
import { parseCoFloats, resolveControlType } from '@/features/slicer/components/settings/metadataTypes';

// ── parseCoFloats unit tests ────────────────────────────────────────────

describe('parseCoFloats', () => {
  const baseMeta: SettingMetadata = {
    key: 'machine_max_speed_x',
    type: 'float',
    coType: 'coFloats',
    label: 'Max speed X',
    default: '500',
  };

  it('parses a single value string', () => {
    expect(parseCoFloats('500', baseMeta)).toEqual([500]);
  });

  it('parses comma-separated values', () => {
    expect(parseCoFloats('500,200', baseMeta)).toEqual([500, 200]);
  });

  it('parses values with trailing dots and spaces', () => {
    expect(parseCoFloats('5000., 5000', baseMeta)).toEqual([5000, 5000]);
  });

  it('falls back to default when raw is null', () => {
    expect(parseCoFloats(null, baseMeta)).toEqual([500]);
  });

  it('falls back to 0 for unparseable parts', () => {
    expect(parseCoFloats('abc,200', baseMeta)).toEqual([0, 200]);
  });

  it('handles three extruders', () => {
    expect(parseCoFloats('100,200,300', baseMeta)).toEqual([100, 200, 300]);
  });
});

// ── resolveControlType for coFloats ─────────────────────────────────────

describe('resolveControlType', () => {
  it('returns coFloats for coType coFloats', () => {
    const meta: SettingMetadata = {
      key: 'machine_max_speed_x',
      type: 'float',
      coType: 'coFloats',
      label: 'Max speed X',
    };
    expect(resolveControlType(meta)).toBe('coFloats');
  });

  it('returns number for coType coFloat (single float)', () => {
    const meta: SettingMetadata = {
      key: 'some_setting',
      type: 'float',
      coType: 'coFloat',
      label: 'Some setting',
    };
    expect(resolveControlType(meta)).toBe('number');
  });
});

// ── MetadataSettingRow coFloats rendering ────────────────────────────────

describe('MetadataSettingRow coFloats', () => {
  const field: FieldRef = { key: 'machine_max_speed_x', compound: false };
  const meta: SettingMetadata = {
    key: 'machine_max_speed_x',
    type: 'float',
    coType: 'coFloats',
    label: 'Maximum speed X',
    tooltip: 'Maximum speed of the X axis',
    unit: 'mm/s',
    min: 0,
    default: '500',
  };
  const onUpdate = vi.fn();

  it('renders a single number input for single value', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '500' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    const input = screen.getByRole('spinbutton');
    expect(input).toBeInTheDocument();
    expect(input).toHaveValue(500);
  });

  it('renders multiple inputs for comma-separated values', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '500,200' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    const inputs = screen.getAllByRole('spinbutton');
    expect(inputs).toHaveLength(2);
    expect(inputs[0]).toHaveValue(500);
    expect(inputs[1]).toHaveValue(200);
  });

  it('labels extruder inputs with E1, E2', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '500,200' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    expect(screen.getByText('E1')).toBeInTheDocument();
    expect(screen.getByText('E2')).toBeInTheDocument();
  });

  it('updates the correct extruder value on change', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '500,200' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    const inputs = screen.getAllByRole('spinbutton');
    fireEvent.change(inputs[1], { target: { value: '300' } });
    expect(onUpdate).toHaveBeenCalledWith('machine_max_speed_x', '500,300');
  });

  it('shows reset button when value is modified', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '600,200' }}
        originalValues={{ machine_max_speed_x: '500,200' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    expect(screen.getByRole('button', { name: /reset/i })).toBeInTheDocument();
  });

  it('renders three inputs for three extruders', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '500,200,300' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    const inputs = screen.getAllByRole('spinbutton');
    expect(inputs).toHaveLength(3);
    expect(screen.getByText('E3')).toBeInTheDocument();
  });

  it('displays the unit for each extruder input', () => {
    render(
      <MetadataSettingRow
        field={field}
        meta={meta}
        values={{ machine_max_speed_x: '500,200' }}
        onUpdate={onUpdate}
        disabled={false}
      />,
    );
    const units = screen.getAllByText('mm/s');
    expect(units).toHaveLength(2);
  });
});
