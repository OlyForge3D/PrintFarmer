import '@testing-library/jest-dom';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';

const sections = vi.hoisted(() => [
  ['CostTracking', 'Operations'],
  ['SlicerSettings', 'Operations'],
  ['QueuePlanning', 'Operations'],
  ['ShiftPlan', 'Operations'],
  ['SpoolCoverage', 'Operations'],
  ['OperatorFeatures', 'Operations'],
  ['AutoTagging', 'Operations'],
  ['Obico', 'Monitoring'],
  ['HistorySeeding', 'Job Queue'],
  ['MaintenanceAlerts', 'Maintenance'],
  ['PrintStatisticsSync', 'Maintenance'],
  ['BackendTimeouts', 'Printers'],
  ['CameraSync', 'Printers'],
  ['NetworkDiscovery', 'Networking'],
  ['Slicer', 'Slicing'],
]);

vi.mock('@/services/settingsApi', () => ({
  fetchSettingsMetadata: vi.fn(async () => sections.map(([key, group]) => ({
    key, group, className: `${key}Settings`, displayName: `${key} section`,
    properties: [{
      name: 'testValue', type: 'string', attributes: [],
      display: { name: `${key} field`, inputType: 'Text' },
    }],
  }))),
  fetchSettingsGroups: vi.fn(async () => [...new Set(sections.map(([, group]) => group))]
    .map(key => ({ key, displayName: key }))),
  fetchSettingsUnified: vi.fn(async () => Object.fromEntries(
    sections.map(([key]) => [key, { testValue: 'unchanged', rowVersion: `${key}-v1` }]),
  )),
  saveSettingsValues: vi.fn(),
}));
vi.mock('@/hooks/useSlicer', () => ({ useSlicer: () => ({ isSlicerAvailable: true }) }));
vi.mock('@/common/hooks/usePageTour', () => ({ usePageTour: () => ({ startTour: vi.fn() }) }));
vi.mock('@/features/admin/settings/section-renderers', () => ({ getSectionRenderer: () => undefined }));

describe('settings section placement', () => {
  beforeEach(() => {
    localStorage.setItem('pf.settings.mode', 'everything');
  });

  it.each([
    ['costs', ['CostTracking']],
    ['slicing.defaults', ['SlicerSettings', 'Slicer']],
    ['planning', ['QueuePlanning', 'ShiftPlan', 'SpoolCoverage', 'HistorySeeding']],
    ['operator-features', ['OperatorFeatures']],
    ['data.auto-tagging', ['AutoTagging']],
    ['failure-detection', ['Obico']],
    ['maintenance.settings', ['MaintenanceAlerts', 'PrintStatisticsSync']],
    ['system', ['BackendTimeouts', 'CameraSync', 'NetworkDiscovery']],
  ])('renders only the sections owned by %s, even when metadata groups overlap', async (pageKey, expected) => {
    render(<MemoryRouter><SettingsPage pageKey={pageKey} /></MemoryRouter>);
    await screen.findByLabelText(`${expected[0]} field`);
    for (const [section] of sections) {
      if (expected.includes(section)) {
        expect(screen.getByLabelText(`${section} field`)).toHaveValue('unchanged');
      } else {
        expect(screen.queryByLabelText(`${section} field`)).not.toBeInTheDocument();
      }
    }
  });
});
