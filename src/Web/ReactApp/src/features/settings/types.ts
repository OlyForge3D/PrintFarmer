/** Sub-page within a settings category */
export interface SettingsSubPage {
  id: string;
  label: string;
  description: string;
  keywords: string[];
}

/** Settings category (sidebar item) */
export interface SettingsCategory {
  id: string;
  label: string;
  description: string;
  keywords: string[];
  subPages: SettingsSubPage[];
}

/** @deprecated Use SettingsCategory instead */
export interface SettingsTab {
  id: string;
  label: string;
  icon?: React.ReactNode;
  keywords: string[];
}

/** Settings categories with their sub-pages */
export const SETTINGS_CATEGORIES: SettingsCategory[] = [
  {
    id: 'general',
    label: 'General',
    description: 'Farm identity, timezone, appearance, and global defaults.',
    keywords: ['general', 'farm', 'name', 'timezone', 'language', 'appearance', 'theme', 'system'],
    subPages: [],
  },
  {
    id: 'slicing',
    label: 'Slicing',
    description: 'Bed types, slicer profiles, and print process defaults.',
    keywords: ['slicer', 'slice', 'profile', 'orcaslicer', 'prusaslicer', 'bed type', 'nozzle', 'process', 'print settings'],
    subPages: [
      { id: 'bed-types', label: 'Bed Types', description: 'Manage bed surfaces and plate presets.', keywords: ['bed', 'type', 'surface', 'plate'] },
      { id: 'profiles', label: 'Slicer Profiles', description: 'Review OrcaSlicer and PrusaSlicer profile libraries.', keywords: ['profile', 'slicer', 'orcaslicer', 'prusaslicer', 'process'] },
    ],
  },
  {
    id: 'hardware',
    label: 'Hardware',
    description: 'Cameras, NFC, printer groups, locations, and device metadata.',
    keywords: ['printer', 'hardware', 'nozzle', 'bed', 'camera', 'nfc', 'location', 'custom field', 'device', 'webcam', 'group', 'binding'],
    subPages: [
      { id: 'cameras', label: 'Cameras', description: 'Configure camera feeds and monitoring views.', keywords: ['camera', 'webcam', 'stream', 'video'] },
      { id: 'nfc', label: 'NFC Devices', description: 'Register and manage NFC readers and hardware.', keywords: ['nfc', 'tag', 'reader', 'rfid'] },
      { id: 'printer-groups', label: 'Printer Groups', description: 'Organize printers into shared operational groups.', keywords: ['printer', 'group', 'grouping', 'cluster'] },
      { id: 'nfc-bindings', label: 'NFC Bindings', description: 'Map NFC tags to printers, spools, and actions.', keywords: ['nfc', 'binding', 'bind', 'tag', 'assignment'] },
      { id: 'locations', label: 'Locations', description: 'Define farm rooms, zones, and placement areas.', keywords: ['location', 'room', 'area', 'zone'] },
      { id: 'custom-fields', label: 'Custom Fields', description: 'Extend hardware records with custom metadata.', keywords: ['custom', 'field', 'attribute', 'metadata'] },
    ],
  },
  {
    id: 'notifications',
    label: 'Notifications',
    description: 'Alert channels, delivery preferences, and push rules.',
    keywords: ['notification', 'email', 'alert', 'push', 'telegram', 'discord'],
    subPages: [],
  },
  {
    id: 'integrations',
    label: 'Integrations',
    description: 'Webhooks, automation endpoints, and external connections.',
    keywords: ['integration', 'api', 'key', 'external', 'webhook', 'automation', 'endpoint'],
    subPages: [],
  },
  {
    id: 'system',
    label: 'System',
    description: 'Farm health, worker status, and background services.',
    keywords: ['system', 'status', 'health', 'workers', 'services', 'cpu', 'memory', 'disk', 'database'],
    subPages: [
      { id: 'status', label: 'Status', description: 'Inspect uptime, health, and infrastructure signals.', keywords: ['status', 'health', 'uptime', 'cpu', 'memory', 'disk', 'database', 'services'] },
      { id: 'workers', label: 'Workers', description: 'Monitor slicer workers, queues, and background jobs.', keywords: ['workers', 'slicer', 'jobs', 'queue', 'processing'] },
    ],
  },
  {
    id: 'data',
    label: 'Data',
    description: 'Tags, quotas, backups, and cleanup workflows.',
    keywords: ['data', 'backup', 'export', 'import', 'storage', 'tag', 'label', 'quota', 'cleanup'],
    subPages: [
      { id: 'tags', label: 'Tags', description: 'Manage reusable labels across the farm.', keywords: ['tag', 'label', 'category'] },
      { id: 'quotas', label: 'Quotas', description: 'Adjust usage limits and allowance policies.', keywords: ['quota', 'limit', 'allowance', 'budget'] },
      { id: 'management', label: 'Data Management', description: 'Run export, import, backup, and cleanup tasks.', keywords: ['backup', 'export', 'import', 'cleanup', 'storage'] },
    ],
  },
  {
    id: 'users',
    label: 'Users',
    description: 'Accounts, API keys, permissions, and login history.',
    keywords: ['user', 'role', 'permission', 'account', 'admin', 'api key', 'profile', 'login', 'audit', 'security'],
    subPages: [
      { id: 'accounts', label: 'User Accounts', description: 'Manage accounts, roles, and access levels.', keywords: ['user', 'account', 'role', 'permission', 'admin'] },
      { id: 'api-keys', label: 'API Keys', description: 'Create and revoke personal API credentials.', keywords: ['api', 'key', 'token', 'access'] },
      { id: 'audit', label: 'Login Audit', description: 'Review authentication attempts and sign-in history.', keywords: ['login', 'audit', 'history', 'security', 'log'] },
    ],
  },
];

/** @deprecated Use SETTINGS_CATEGORIES instead */
export const SETTINGS_TABS: SettingsTab[] = SETTINGS_CATEGORIES.map((cat) => ({
  id: cat.id,
  label: cat.label,
  keywords: cat.keywords,
}));

export const DEFAULT_CATEGORY = 'general';

/** @deprecated Use DEFAULT_CATEGORY instead */
export const DEFAULT_TAB = 'general';

/** Get the default sub-page for a category (first one, or empty string if none) */
export function getDefaultSubPage(categoryId: string): string {
  const category = SETTINGS_CATEGORIES.find((c) => c.id === categoryId);
  return category?.subPages[0]?.id ?? '';
}
