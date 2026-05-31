export interface SettingsTab {
  id: string;
  label: string;
  icon?: React.ReactNode;
  keywords: string[];
}

export const SETTINGS_TABS: SettingsTab[] = [
  { id: 'general', label: 'General', keywords: ['general', 'farm', 'name', 'timezone', 'language'] },
  { id: 'filament', label: 'Filament', keywords: ['filament', 'spool', 'material', 'spoolman'] },
  { id: 'slicing', label: 'Slicing', keywords: ['slicer', 'slice', 'profile', 'orcaslicer', 'prusaslicer'] },
  { id: 'hardware', label: 'Hardware', keywords: ['printer', 'hardware', 'nozzle', 'bed', 'camera'] },
  { id: 'notifications', label: 'Notifications', keywords: ['notification', 'email', 'alert', 'webhook'] },
  { id: 'integrations', label: 'Integrations', keywords: ['integration', 'api', 'key', 'external', 'webhook'] },
  { id: 'data', label: 'Data', keywords: ['data', 'backup', 'export', 'import', 'storage'] },
  { id: 'users', label: 'Users', keywords: ['user', 'role', 'permission', 'account', 'admin'] },
];

export const DEFAULT_TAB = 'general';
