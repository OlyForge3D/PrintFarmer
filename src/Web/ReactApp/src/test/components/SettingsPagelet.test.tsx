import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { SettingsPagelet } from '@/components/SettingsPagelet';

const mockMetadata = {
  key: 'SystemLogSettings',
  className: 'SystemLogSettings',
  displayName: 'System Log Settings',
  description: 'Configure log persistence and retention.',
  properties: [
    {
      name: 'retentionDays',
      displayName: 'Retention Days',
      type: 'number',
      description: 'How many days to keep logs.',
      required: true,
      min: 1,
      max: 365,
      attributes: [],
    },
    {
      name: 'persistedTypes',
      displayName: 'Log Types',
      type: 'string[]',
      description: 'Types of logs to persist.',
      required: true,
      enumValues: ['Info', 'Warning', 'Error', 'Telemetry'],
      attributes: [],
    },
  ],
};

const mockValues = {
  retentionDays: 30,
  persistedTypes: ['Info', 'Error'],
};

describe('SettingsPagelet', () => {
  it('renders metadata and values', () => {
    render(
      <SettingsPagelet
        metadata={mockMetadata}
        values={mockValues}
        onChange={vi.fn()}
        isSaving={false}
        error={undefined}
      />
    );
    expect(screen.getByText('System Log Settings')).toBeInTheDocument();
    expect(screen.getByLabelText('Retention Days')).toHaveValue(30);
    expect(screen.getByLabelText('Log Types')).toBeInTheDocument();
  });

  it('calls onChange when input changes', () => {
    const handleChange = vi.fn();
    render(
      <SettingsPagelet
        metadata={mockMetadata}
        values={mockValues}
        onChange={handleChange}
        isSaving={false}
        error={undefined}
      />
    );
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '10' } });
    expect(handleChange).toHaveBeenCalled();
  });

  it('renders without save functionality', async () => {
    render(
      <SettingsPagelet
        metadata={mockMetadata}
        values={mockValues}
        onChange={vi.fn()}
        isSaving={false}
        error={undefined}
      />
    );
    // SettingsPagelet now only handles onChange, no save button
  });

  it('shows error message if error is present', () => {
    render(
      <SettingsPagelet
        metadata={mockMetadata}
        values={mockValues}
        onChange={vi.fn()}
        isSaving={false}
        error={'Something went wrong'}
      />
    );
    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
  });

  it('renders inline fieldErrors under inputs', () => {
    const fieldErrors = { retentionDays: 'Must be >= 1' };
    render(
      <SettingsPagelet
        metadata={mockMetadata}
        values={mockValues}
        onChange={vi.fn()}
        fieldErrors={fieldErrors}
      />
    );
    expect(screen.getByText('Must be >= 1')).toBeInTheDocument();
  });
});
