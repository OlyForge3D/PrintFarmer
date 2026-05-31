export interface SettingsTab {
  id: string;
  label: string;
  icon?: React.ReactNode;
  keywords: string[];
}

export const SETTINGS_TABS: SettingsTab[] = [
  { id: 'general', label: 'General', keywords: ['general', 'farm', 'name', 'timezone', 'language', 'appearance', 'theme', 'system'] },
  { id: 'filament', label: 'Filament', keywords: ['filament', 'spool', 'material', 'spoolman', 'inventory', 'pla', 'abs', 'petg'] },
  { id: 'slicing', label: 'Slicing', keywords: ['slicer', 'slice', 'profile', 'orcaslicer', 'prusaslicer', 'bed type', 'nozzle', 'process', 'print settings'] },
  { id: 'hardware', label: 'Hardware', keywords: ['printer', 'hardware', 'nozzle', 'bed', 'camera', 'nfc', 'location', 'custom field', 'device', 'webcam'] },
  { id: 'notifications', label: 'Notifications', keywords: ['notification', 'email', 'alert', 'push', 'telegram', 'discord'] },
  { id: 'integrations', label: 'Integrations', keywords: ['integration', 'api', 'key', 'external', 'webhook', 'automation', 'endpoint'] },
  { id: 'data', label: 'Data', keywords: ['data', 'backup', 'export', 'import', 'storage', 'tag', 'label', 'quota', 'cleanup'] },
  { id: 'users', label: 'Users', keywords: ['user', 'role', 'permission', 'account', 'admin', 'api key', 'profile', 'login', 'audit', 'security'] },
];

export const DEFAULT_TAB = 'general';
