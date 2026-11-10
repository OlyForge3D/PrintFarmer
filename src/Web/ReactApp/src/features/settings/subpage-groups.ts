import type { SettingsScopeId } from './types';

export interface SettingsSectionLocation {
  scopeId: SettingsScopeId;
  categoryId: string;
  subPageId?: string;
}

// Persisted section keys and metadata groups stay unchanged. These are UI homes.
export const SETTINGS_GROUP_TO_LOCATION: Record<string, SettingsSectionLocation> = {
  FarmIdentity: { scopeId: 'system', categoryId: 'identity', subPageId: 'details' },
  System: { scopeId: 'system', categoryId: 'system' },
  Networking: { scopeId: 'system', categoryId: 'system' },
  Catalog: { scopeId: 'system', categoryId: 'system' },
  Files: { scopeId: 'system', categoryId: 'system' },
  Printers: { scopeId: 'system', categoryId: 'system' },
  Operations: { scopeId: 'system', categoryId: 'planning' },
  Monitoring: { scopeId: 'system', categoryId: 'failure-detection' },
  Maintenance: { scopeId: 'system', categoryId: 'maintenance', subPageId: 'settings' },
  'Job Queue': { scopeId: 'system', categoryId: 'planning' },
  Integrations: { scopeId: 'system', categoryId: 'integrations', subPageId: 'connections' },
  Slicing: { scopeId: 'system', categoryId: 'slicing', subPageId: 'defaults' },
};

export const SETTINGS_SECTION_TO_LOCATION: Record<string, SettingsSectionLocation> = {
  CostTracking: { scopeId: 'system', categoryId: 'costs' },
  Obico: { scopeId: 'system', categoryId: 'failure-detection' },
  SlicerSettings: { scopeId: 'system', categoryId: 'slicing', subPageId: 'defaults' },
  OperatorFeatures: { scopeId: 'system', categoryId: 'operator-features' },
  AutoTagging: { scopeId: 'system', categoryId: 'data', subPageId: 'auto-tagging' },
  QueuePlanning: { scopeId: 'system', categoryId: 'planning' },
  ShiftPlan: { scopeId: 'system', categoryId: 'planning' },
  SpoolCoverage: { scopeId: 'system', categoryId: 'planning' },
  HistorySeeding: { scopeId: 'system', categoryId: 'planning' },
  MaintenanceAlerts: { scopeId: 'system', categoryId: 'maintenance', subPageId: 'settings' },
  PrintStatisticsSync: { scopeId: 'system', categoryId: 'maintenance', subPageId: 'settings' },
};

export function getSettingsSectionLocation(key: string, group: string): SettingsSectionLocation | undefined {
  return SETTINGS_SECTION_TO_LOCATION[key] ?? SETTINGS_GROUP_TO_LOCATION[group];
}

export function getSettingsSectionPage(key: string, group: string): string | undefined {
  const location = getSettingsSectionLocation(key, group);
  return location && [location.categoryId, location.subPageId].filter(Boolean).join('.');
}
