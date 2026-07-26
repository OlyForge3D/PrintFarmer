import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { SettingsPagelet } from '@/common/components/SettingsPagelet';

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

  it('emits section-qualified control ids so two sections sharing a property name do not collide', () => {
    // `Enabled` is declared on 13 backend settings classes, and Obico, Telegram,
    // HomeAssistant and Go2RTC all render on the connections page. A bare
    // `prop.name` id produced duplicate DOM ids and pointed both labels at
    // whichever control rendered first.
    const shared = (key: string) => ({
      key,
      className: key,
      displayName: key,
      properties: [
        { name: 'enabled', displayName: 'Enabled', type: 'bool', attributes: [] },
      ],
    });

    const { container } = render(
      <>
        <SettingsPagelet
          metadata={shared('ObicoSettings')}
          values={{ enabled: true }}
          onChange={vi.fn()}
        />
        <SettingsPagelet
          metadata={shared('TelegramSettings')}
          values={{ enabled: false }}
          onChange={vi.fn()}
        />
      </>,
    );

    const ids = Array.from(container.querySelectorAll('[id]')).map((el) => el.id);
    expect(ids).toContain('ObicoSettings.enabled');
    expect(ids).toContain('TelegramSettings.enabled');
    expect(new Set(ids).size).toBe(ids.length);

    const labels = Array.from(container.querySelectorAll('label[for]')).map((el) =>
      el.getAttribute('for'),
    );
    expect(new Set(labels).size).toBe(labels.length);
  });
});
