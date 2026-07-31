import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

import {
  isPropertyRequired,
  validateSection,
  deriveSettingsIssues,
  countIssuesBySection,
} from '@/features/admin/settings/settingsAttention';

/**
 * Attention state on the settings page (#1012).
 *
 * The load-bearing requirement is that attention is *earned*: every flagged item
 * corresponds to a condition the system can detect from backend metadata or a
 * server response. There is no curated list of "important" fields anywhere in
 * this feature, and these tests exist to keep it that way — a fixture whose
 * metadata declares nothing must produce no attention items, no matter how
 * interesting its field names look.
 */

type Prop = {
  name: string;
  type: string;
  attributes: string[];
  display?: Record<string, unknown>;
};

type Section = {
  key: string;
  className: string;
  displayName: string;
  group: string;
  order: number;
  properties: Prop[];
};

// ─────────────────────────────────────────────────────────────────────────────
// Pure derivation

const requiredByAttribute: Prop = {
  name: 'apiKey',
  type: 'string',
  attributes: ['RequiredAttribute'],
  display: { name: 'API Key' },
};

const requiredByDisplay: Prop = {
  name: 'subnets',
  type: 'array',
  attributes: [],
  display: { name: 'Subnets', required: true },
};

const conditionallyRequired: Prop = {
  name: 'subnets',
  type: 'array',
  attributes: [],
  display: { name: 'Subnets', required: true, requiredWhen: 'enabled' },
};

const gate: Prop = {
  name: 'enabled',
  type: 'boolean',
  attributes: [],
  display: { name: 'Enabled', inputType: 'Boolean' },
};

describe('isPropertyRequired', () => {
  it('honours [Required] from the backend attribute list', () => {
    expect(isPropertyRequired(requiredByAttribute, {})).toBe(true);
  });

  it('honours SettingDisplay(Required = true)', () => {
    expect(isPropertyRequired(requiredByDisplay, {})).toBe(true);
  });

  it('treats an unannotated property as optional', () => {
    expect(isPropertyRequired(gate, {})).toBe(false);
  });

  it('applies a RequiredWhen gate only while the gating field is true', () => {
    expect(isPropertyRequired(conditionallyRequired, { enabled: true })).toBe(true);
    expect(isPropertyRequired(conditionallyRequired, { enabled: false })).toBe(false);
  });

  it('reads the gate the same way the checkbox does', () => {
    // SettingsPagelet renders a Boolean control with `checked={Boolean(value)}`.
    // If the gate demanded a literal `true`, a truthy-but-not-`true` value would
    // draw the checkbox ON while the gate read OFF, and the page would disagree
    // with itself about one field.
    expect(isPropertyRequired(conditionallyRequired, { enabled: 1 as never })).toBe(true);
    expect(isPropertyRequired(conditionallyRequired, { enabled: 0 as never })).toBe(false);
    expect(isPropertyRequired(conditionallyRequired, { enabled: undefined })).toBe(false);
  });

  it('does not require a field whose gate cannot be resolved', () => {
    // A stale annotation naming a property that no longer exists must not
    // produce an error the user has no way to clear.
    const stale: Prop = {
      ...conditionallyRequired,
      display: { name: 'Subnets', required: true, requiredWhen: 'noSuchField' },
    };
    expect(isPropertyRequired(stale, { enabled: true })).toBe(false);
  });
});

describe('validateSection', () => {
  const section = {
    key: 'NetworkDiscovery',
    className: 'NetworkDiscoverySettings',
    displayName: 'Network Discovery',
    properties: [gate, conditionallyRequired],
  } as never;

  it('flags a conditionally-required field that is empty while enabled', () => {
    expect(validateSection(section, { enabled: true, subnets: [] })).toEqual({
      subnets: 'This field is required.',
    });
  });

  it('stays silent when the gate is off', () => {
    expect(validateSection(section, { enabled: false, subnets: [] })).toEqual({});
  });

  it('stays silent once the field has a value', () => {
    expect(validateSection(section, { enabled: true, subnets: ['10.0.0.0/24'] })).toEqual({});
  });

  it('flags a value outside its declared range', () => {
    const ranged = {
      key: 'S',
      className: 'S',
      properties: [
        {
          name: 'timeout',
          type: 'int',
          attributes: [],
          display: { name: 'Timeout', inputType: 'Number', minValue: 50, maxValue: 60000 },
        },
      ],
    } as never;
    expect(validateSection(ranged, { timeout: 10 })).toEqual({ timeout: 'Minimum is 50' });
    expect(validateSection(ranged, { timeout: 99999 })).toEqual({ timeout: 'Maximum is 60000' });
    expect(validateSection(ranged, { timeout: 200 })).toEqual({});
  });
});

describe('deriveSettingsIssues', () => {
  const section = {
    key: 'NetworkDiscovery',
    className: 'NetworkDiscoverySettings',
    displayName: 'Network Discovery',
    properties: [gate, conditionallyRequired],
  } as never;

  it('produces nothing for a well-configured section', () => {
    const issues = deriveSettingsIssues(
      [section],
      { NetworkDiscovery: { enabled: true, subnets: ['10.0.0.0/24'] } },
    );
    expect(issues).toEqual([]);
  });

  it('labels an issue with the section and field a user would recognise', () => {
    const issues = deriveSettingsIssues(
      [section],
      { NetworkDiscovery: { enabled: true, subnets: [] } },
    );
    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({
      sectionKey: 'NetworkDiscovery',
      sectionLabel: 'Network Discovery',
      field: 'subnets',
      fieldLabel: 'Subnets',
      severity: 'Error',
    });
  });

  it('writes banner copy that names the switch which made the field required', () => {
    // The banner sits away from the field, so "This field is required." is not
    // enough — it has to say which section, which field, and what turned the
    // requirement on. The inline `message` stays terse for the field row.
    const issues = deriveSettingsIssues(
      [section],
      { NetworkDiscovery: { enabled: true, subnets: [] } },
    );
    expect(issues[0].title).toBe('Network Discovery is on but Subnets is not set');
    expect(issues[0].detail).toBe('Subnets is required while Enabled is enabled.');
    expect(issues[0].message).toBe('This field is required.');
  });

  it('treats a list of blank entries as empty', () => {
    // The multi-value control leaves a blank row behind when a user clears one.
    // `['']` is not a value the server will take, so the page must not call the
    // section healthy — verified against the live page, where blanking both
    // subnets left discovery enabled with nothing to scan and no warning.
    const issues = deriveSettingsIssues(
      [section],
      { NetworkDiscovery: { enabled: true, subnets: ['', '  '] } },
    );
    expect(issues).toHaveLength(1);
    expect(issues[0].field).toBe('subnets');
  });

  it('surfaces a server-reported section error with no field to focus', () => {
    const issues = deriveSettingsIssues(
      [section],
      { NetworkDiscovery: { enabled: false } },
      { NetworkDiscovery: 'At least one valid subnet is required.' },
    );
    expect(issues).toHaveLength(1);
    expect(issues[0].field).toBe('');
    expect(issues[0].message).toBe('At least one valid subnet is required.');
  });

  it('counts issues per section', () => {
    const issues = deriveSettingsIssues(
      [section],
      { NetworkDiscovery: { enabled: true, subnets: [] } },
      { NetworkDiscovery: 'Server said no.' },
    );
    expect(countIssuesBySection(issues)).toEqual({ NetworkDiscovery: 2 });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Page integration

let sections: Section[] = [];
let sectionValues: Record<string, Record<string, unknown>> = {};

vi.mock('@/services/settingsApi', () => ({
  fetchSettingsMetadata: vi.fn(() => Promise.resolve(sections)),
  fetchSettingsGroups: vi.fn(() =>
    Promise.resolve(
      Array.from(new Set(sections.map((s) => s.group))).map((key, i) => ({
        key,
        displayName: key,
        order: i + 1,
      })),
    ),
  ),
  fetchSettingsUnified: vi.fn(() => Promise.resolve(sectionValues)),
  saveSettingsValues: vi.fn(),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));
vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));
vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));
vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div'),
}));

import { SettingsPage } from '@/features/admin/pages/SettingsPage';

/** A healthy band that declares nothing and should never be flagged. */
function storageBand(): Section {
  return {
    key: 'Storage',
    className: 'StorageSettings',
    displayName: 'Storage',
    group: 'Storage',
    order: 1,
    properties: [
      {
        name: 'retentionDays',
        type: 'int',
        attributes: [],
        display: { name: 'Retention Days', inputType: 'Number' },
      },
    ],
  };
}

/** A band whose subnet list is required only while discovery is enabled. */
function discoveryBand(): Section {
  return {
    key: 'NetworkDiscovery',
    className: 'NetworkDiscoverySettings',
    displayName: 'Network Discovery',
    group: 'Networking',
    order: 2,
    properties: [
      { name: 'enableDiscovery', type: 'boolean', attributes: [], display: { name: 'Enable Discovery', inputType: 'Boolean' } },
      {
        name: 'discoverySubnets',
        type: 'array',
        attributes: [],
        display: { name: 'Discovery Subnets', required: true, requiredWhen: 'enableDiscovery' },
      },
    ],
  };
}

async function renderPage(
  fixture: Section[],
  values: Record<string, Record<string, unknown>>,
) {
  sections = fixture;
  sectionValues = values;
  render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByTestId('settings-band-flow')).toBeInTheDocument();
  });
}

const bandCaptions = () =>
  Array.from(
    screen.getByTestId('settings-band-flow').querySelectorAll('h3'),
  ).map((h) => h.textContent?.trim());

describe('settings attention band', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
  });

  it('renders nothing when every section is configured', async () => {
    await renderPage([storageBand(), discoveryBand()], {
      Storage: { retentionDays: 30 },
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: ['10.0.0.0/24'] },
    });
    expect(screen.queryByTestId('settings-attention-list')).not.toBeInTheDocument();
  });

  it('leaves band order untouched when nothing is flagged', async () => {
    await renderPage([storageBand(), discoveryBand()], {
      Storage: { retentionDays: 30 },
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: ['10.0.0.0/24'] },
    });
    expect(bandCaptions()).toEqual(['Storage', 'Networking']);
  });

  it('flags a conditionally-required field left empty', async () => {
    await renderPage([storageBand(), discoveryBand()], {
      Storage: { retentionDays: 30 },
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: [] },
    });
    const items = await screen.findAllByTestId('settings-attention-item');
    expect(items).toHaveLength(1);
    expect(items[0]).toHaveAttribute('data-attention-section', 'NetworkDiscovery');
    expect(items[0]).toHaveAttribute('data-attention-field', 'discoverySubnets');
    expect(items[0].textContent).toContain('Network Discovery');
    expect(items[0].textContent).toContain('Discovery Subnets');
  });

  it('does not flag the same field when its gate is off', async () => {
    await renderPage([storageBand(), discoveryBand()], {
      Storage: { retentionDays: 30 },
      NetworkDiscovery: { enableDiscovery: false, discoverySubnets: [] },
    });
    expect(screen.queryByTestId('settings-attention-list')).not.toBeInTheDocument();
  });

  it('floats a flagged band above the declared order', async () => {
    await renderPage([storageBand(), discoveryBand()], {
      Storage: { retentionDays: 30 },
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: [] },
    });
    expect(bandCaptions()).toEqual(['Networking', 'Storage']);
  });

  it('badges the flagged band and its card, and leaves the healthy ones bare', async () => {
    await renderPage([storageBand(), discoveryBand()], {
      Storage: { retentionDays: 30 },
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: [] },
    });
    const flow = screen.getByTestId('settings-band-flow');

    // The band badge lags the card badge by one render: a card knows its own
    // issues immediately, whereas the band count round-trips up through the
    // save registry — the same publish-up path the save bar uses. Waiting here
    // rather than asserting synchronously keeps the test honest about that.
    await waitFor(() => {
      expect(flow.textContent).toContain('1 issue');
    });

    const flagged = flow.querySelector('[data-section-key="NetworkDiscovery"]');
    const healthy = flow.querySelector('[data-section-key="Storage"]');
    expect(flagged).toHaveAttribute('data-section-issues', '1');
    expect(healthy).not.toHaveAttribute('data-section-issues');
    expect(flagged?.textContent).toContain('Action needed');
    expect(healthy?.textContent).not.toContain('Action needed');
  });

  it('clears the item once the user supplies a value', async () => {
    const user = userEvent.setup();
    await renderPage([discoveryBand()], {
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: [] },
    });
    expect(await screen.findAllByTestId('settings-attention-item')).toHaveLength(1);

    // Turning the gate off is the other way to satisfy the requirement, and it
    // exercises the live-derivation path without depending on the array editor.
    await user.click(screen.getByLabelText('Enable Discovery'));

    await waitFor(() => {
      expect(screen.queryByTestId('settings-attention-item')).not.toBeInTheDocument();
    });
  });

  it('offers a Fix action that reveals the offending field', async () => {
    const user = userEvent.setup();
    await renderPage([discoveryBand()], {
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: [] },
    });
    const item = (await screen.findAllByTestId('settings-attention-item'))[0];
    const fix = item.querySelector('button');
    expect(fix).toBeTruthy();

    const row = document.querySelector(
      '[data-setting-property="NetworkDiscovery.discoverySubnets"]',
    );
    expect(row).toBeTruthy();

    await user.click(fix!);
    expect(row).toHaveClass('pf-setting-focus');
  });

  it('renders through the shared AttentionRow, not a settings-only copy', async () => {
    await renderPage([discoveryBand()], {
      NetworkDiscovery: { enableDiscovery: true, discoverySubnets: [] },
    });
    const item = (await screen.findAllByTestId('settings-attention-item'))[0];
    // These come from AttentionRow's own markup: the severity badge and the
    // screen-reader prefix. If the settings page ever forks its own row, the
    // fork will not reproduce both by accident.
    expect(item.textContent).toContain('Error');
    expect(item.querySelector('.sr-only')?.textContent).toBe('Error: ');
    expect(item.className).toContain('border-pf-error/40');
  });
});
