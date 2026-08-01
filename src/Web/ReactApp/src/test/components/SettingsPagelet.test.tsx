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

/**
 * #1012 — "Within a card: required fields first, then the declared order."
 *
 * Ordering keys off the *unconditional* requirement only. A `RequiredWhen`
 * field flips as the user toggles its gate, and reordering on that would make
 * rows physically move under the pointer mid-edit.
 */
describe('SettingsPagelet — required fields lead (#1012)', () => {
  function prop(name: string, display: Record<string, unknown>) {
    return { name, displayName: name, type: 'string', attributes: [], ...display };
  }

  function sectionWith(properties: unknown[]) {
    return {
      key: 'Ordering',
      className: 'OrderingSettings',
      displayName: 'Ordering',
      description: '',
      properties,
    } as never;
  }

  function renderedLabels(container: HTMLElement): string[] {
    return Array.from(container.querySelectorAll('label')).map((l) =>
      (l.textContent ?? '').replace('*', '').trim(),
    );
  }

  it('floats a required field above optional ones declared before it', () => {
    const { container } = render(
      <SettingsPagelet
        metadata={sectionWith([
          prop('alpha', { display: { name: 'Alpha', inputType: 'Text' } }),
          prop('beta', { display: { name: 'Beta', inputType: 'Text' } }),
          prop('gamma', { display: { name: 'Gamma', inputType: 'Text', required: true } }),
        ])}
        values={{ alpha: '', beta: '', gamma: '' }}
        onChange={vi.fn()}
      />,
    );
    expect(renderedLabels(container)).toEqual(['Gamma', 'Alpha', 'Beta']);
  });

  it('keeps the declared order among the non-required tail', () => {
    const { container } = render(
      <SettingsPagelet
        metadata={sectionWith([
          prop('zulu', { display: { name: 'Zulu', inputType: 'Text' } }),
          prop('alpha', { display: { name: 'Alpha', inputType: 'Text' } }),
        ])}
        values={{ zulu: '', alpha: '' }}
        onChange={vi.fn()}
      />,
    );
    // Declared order, not alphabetical — the sort must be stable, not a re-sort.
    expect(renderedLabels(container)).toEqual(['Zulu', 'Alpha']);
  });

  it('does not reorder a conditionally-required field when its gate flips', () => {
    const properties = [
      prop('enabled', { display: { name: 'Enabled', inputType: 'Checkbox' } }),
      prop('alpha', { display: { name: 'Alpha', inputType: 'Text' } }),
      prop('gated', {
        display: { name: 'Gated', inputType: 'Text', required: true, requiredWhen: 'enabled' },
      }),
    ];
    const off = render(
      <SettingsPagelet
        metadata={sectionWith(properties)}
        values={{ enabled: false, alpha: '', gated: '' }}
        onChange={vi.fn()}
      />,
    );
    const orderWhenOff = renderedLabels(off.container);
    off.unmount();

    const on = render(
      <SettingsPagelet
        metadata={sectionWith(properties)}
        values={{ enabled: true, alpha: '', gated: '' }}
        onChange={vi.fn()}
      />,
    );
    // Same positions either way: only the required *marker* changes, not layout.
    expect(renderedLabels(on.container)).toEqual(orderWhenOff);
  });
});

/**
 * `aria-required` is not a supported attribute on `role="group"` — ARIA allows
 * it on textbox, combobox, listbox, radiogroup and the checkbox/radio family,
 * and assistive tech drops it anywhere else. The requirement has to reach the
 * user through something that is actually announced.
 */
describe('SettingsPagelet — array requirement uses valid ARIA (Hicks #7)', () => {
  const arraySection = {
    key: 'NetworkDiscovery',
    className: 'NetworkDiscoverySettings',
    displayName: 'Network Discovery',
    description: '',
    properties: [
      {
        name: 'discoverySubnets',
        displayName: 'Discovery Subnets',
        type: 'string[]',
        attributes: [],
        display: { name: 'Discovery Subnets', inputType: 'Array', isMulti: true, required: true },
      },
    ],
  } as never;

  it('never puts aria-required on the group', () => {
    const { container } = render(
      <SettingsPagelet
        metadata={arraySection}
        values={{ discoverySubnets: ['10.0.0.0/24'] }}
        onChange={vi.fn()}
      />,
    );
    const group = container.querySelector('[role="group"]');
    expect(group).not.toBeNull();
    expect(group).not.toHaveAttribute('aria-required');
  });

  it('describes the group with a hint a screen reader will read', () => {
    const { container } = render(
      <SettingsPagelet
        metadata={arraySection}
        values={{ discoverySubnets: ['10.0.0.0/24'] }}
        onChange={vi.fn()}
      />,
    );
    const group = container.querySelector('[role="group"]')!;
    const describedBy = group.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    // The referenced node must exist, or the attribute is decoration.
    const hint = container.querySelector(`#${CSS.escape(describedBy!)}`);
    expect(hint?.textContent).toMatch(/required/i);
  });
});
