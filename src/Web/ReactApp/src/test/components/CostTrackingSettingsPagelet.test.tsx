import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { SettingsPagelet, SettingMetadata } from '@/common/components/SettingsPagelet';
import { SettingInputType } from '@/types/SettingInputType';

/**
 * Metadata shape matching what the backend returns for CostTrackingSettings.
 * This mirrors the [SettingDisplay] attributes on CostTrackingSettings.cs.
 */
const costTrackingMetadata: SettingMetadata = {
  key: 'CostTracking',
  className: 'CostTrackingSettings',
  displayName: 'Cost Tracking',
  description: 'Configure cost calculation for print jobs.',
  icon: 'pf-icon-cost',
  group: 'Operations',
  order: 1,
  properties: [
    {
      name: 'enableAutomaticCostCalculation',
      type: 'Boolean',
      attributes: [],
      display: {
        name: 'Enable Automatic Calculation',
        description: 'Automatically calculate costs when jobs complete.',
        inputType: SettingInputType.Boolean,
      },
    },
    {
      name: 'electricityRatePerKwh',
      type: 'Decimal',
      attributes: [],
      display: {
        name: 'Electricity Rate (per kWh)',
        description: 'Cost of electricity per kilowatt-hour (e.g., 0.12 for $0.12/kWh).',
        inputType: SettingInputType.Number,
        minValue: 0,
        maxValue: 10,
      },
    },
    {
      name: 'defaultMachineHourlyRate',
      type: 'Decimal',
      attributes: [],
      display: {
        name: 'Default Machine Hourly Rate',
        description: 'Default hourly rate for machine time (e.g., 0.50 for $0.50/hour).',
        inputType: SettingInputType.Number,
        minValue: 0,
        maxValue: 100,
      },
    },
    {
      name: 'laborMarkupPercent',
      type: 'Decimal',
      attributes: [],
      display: {
        name: 'Labor Markup Percent',
        description: 'Labor cost as percentage of material+energy+machine (e.g., 0 for no markup, 20 for 20%).',
        inputType: SettingInputType.Number,
        minValue: 0,
        maxValue: 200,
      },
    },
    {
      name: 'profitMarginTargetPercent',
      type: 'Decimal',
      attributes: [],
      display: {
        name: 'Profit Margin Target Percent',
        description: 'Target profit margin for pricing calculations (e.g., 30 for 30%).',
        inputType: SettingInputType.Number,
        minValue: 0,
        maxValue: 500,
      },
    },
    {
      name: 'averagePrinterWattage',
      type: 'Decimal',
      attributes: [],
      display: {
        name: 'Average Printer Wattage',
        description: 'Average power consumption of printers in watts (used if printer-specific data unavailable).',
        inputType: SettingInputType.Number,
        minValue: 0,
        maxValue: 5000,
      },
    },
  ],
};

const defaultValues = {
  enableAutomaticCostCalculation: true,
  electricityRatePerKwh: 0.12,
  defaultMachineHourlyRate: 0.5,
  laborMarkupPercent: 0,
  profitMarginTargetPercent: 30,
  averagePrinterWattage: 250,
};

describe('CostTracking SettingsPagelet', () => {
  it('renders the section title and all fields', () => {
    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={defaultValues}
        onChange={vi.fn()}
      />
    );

    expect(screen.getByText('Cost Tracking')).toBeInTheDocument();
    expect(screen.getByLabelText('Enable Automatic Calculation')).toBeInTheDocument();
    expect(screen.getByLabelText('Electricity Rate (per kWh)')).toBeInTheDocument();
    expect(screen.getByLabelText('Default Machine Hourly Rate')).toBeInTheDocument();
    expect(screen.getByLabelText('Labor Markup Percent')).toBeInTheDocument();
    expect(screen.getByLabelText('Profit Margin Target Percent')).toBeInTheDocument();
    expect(screen.getByLabelText('Average Printer Wattage')).toBeInTheDocument();
  });

  it('renders the boolean toggle as a checkbox', () => {
    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={defaultValues}
        onChange={vi.fn()}
      />
    );

    const toggle = screen.getByLabelText('Enable Automatic Calculation') as HTMLInputElement;
    expect(toggle.type).toBe('checkbox');
    expect(toggle.checked).toBe(true);
  });

  it('renders number inputs with correct default values', () => {
    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={defaultValues}
        onChange={vi.fn()}
      />
    );

    expect(screen.getByLabelText('Electricity Rate (per kWh)')).toHaveValue(0.12);
    expect(screen.getByLabelText('Default Machine Hourly Rate')).toHaveValue(0.5);
    expect(screen.getByLabelText('Labor Markup Percent')).toHaveValue(0);
    expect(screen.getByLabelText('Profit Margin Target Percent')).toHaveValue(30);
    expect(screen.getByLabelText('Average Printer Wattage')).toHaveValue(250);
  });

  it('calls onChange when a number field is updated', () => {
    const handleChange = vi.fn();
    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={defaultValues}
        onChange={handleChange}
      />
    );

    fireEvent.change(screen.getByLabelText('Electricity Rate (per kWh)'), {
      target: { value: '0.15' },
    });
    expect(handleChange).toHaveBeenCalledWith('electricityRatePerKwh', 0.15);
  });

  it('calls onChange when the toggle is flipped', () => {
    const handleChange = vi.fn();
    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={defaultValues}
        onChange={handleChange}
      />
    );

    fireEvent.click(screen.getByLabelText('Enable Automatic Calculation'));
    expect(handleChange).toHaveBeenCalledWith('enableAutomaticCostCalculation', false);
  });

  it('shows field-level validation errors', () => {
    const fieldErrors = {
      electricityRatePerKwh: 'Maximum is 10',
      averagePrinterWattage: 'Maximum is 5000',
    };

    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={{ ...defaultValues, electricityRatePerKwh: 15, averagePrinterWattage: 9999 }}
        onChange={vi.fn()}
        fieldErrors={fieldErrors}
      />
    );

    expect(screen.getByText('Maximum is 10')).toBeInTheDocument();
    expect(screen.getByText('Maximum is 5000')).toBeInTheDocument();
  });

  it('renders description tooltips for each field', () => {
    render(
      <SettingsPagelet
        metadata={costTrackingMetadata}
        values={defaultValues}
        onChange={vi.fn()}
      />
    );

    // InfoTooltip renders as a span with title attribute containing the description
    const tooltips = document.querySelectorAll('[title]');
    const tooltipTitles = Array.from(tooltips).map(el => el.getAttribute('title'));

    expect(tooltipTitles).toContain('Automatically calculate costs when jobs complete.');
    expect(tooltipTitles).toContain('Cost of electricity per kilowatt-hour (e.g., 0.12 for $0.12/kWh).');
  });
});
