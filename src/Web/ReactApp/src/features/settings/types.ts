/** Sub-page within a settings category */
export interface SettingsSubPage {
  id: string;
  label: string;
  keywords: string[];
}

/** Settings category (sidebar item) */
export interface SettingsCategory {
  id: string;
  label: string;
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
    keywords: ['general', 'farm', 'name', 'timezone', 'language', 'appearance', 'theme', 'system'],
    subPages: [],
  },
  {
    id: 'slicing',
    label: 'Slicing',
    keywords: ['slicer', 'slice', 'profile', 'orcaslicer', 'prusaslicer', 'bed type', 'nozzle', 'process', 'print settings'],
    subPages: [
      { id: 'bed-types', label: 'Bed Types', keywords: ['bed', 'type', 'surface', 'plate'] },
      { id: 'profiles', label: 'Slicer Profiles', keywords: ['profile', 'slicer', 'orcaslicer', 'prusaslicer', 'process'] },
    ],
  },
  {
    id: 'hardware',
    label: 'Hardware',
    keywords: ['printer', 'hardware', 'nozzle', 'bed', 'camera', 'nfc', 'location', 'custom field', 'device', 'webcam'],
    subPages: [
      { id: 'cameras', label: 'Cameras', keywords: ['camera', 'webcam', 'stream', 'video'] },
      { id: 'nfc', label: 'NFC Devices', keywords: ['nfc', 'tag', 'reader', 'rfid'] },
      { id: 'locations', label: 'Locations', keywords: ['location', 'room', 'area', 'zone'] },
      { id: 'custom-fields', label: 'Custom Fields', keywords: ['custom', 'field', 'attribute', 'metadata'] },
    ],
  },
  {
    id: 'notifications',
    label: 'Notifications',
    keywords: ['notification', 'email', 'alert', 'push', 'telegram', 'discord'],
    subPages: [],
  },
  {
    id: 'integrations',
    label: 'Integrations',
    keywords: ['integration', 'api', 'key', 'external', 'webhook', 'automation', 'endpoint'],
    subPages: [],
  },
  {
    id: 'data',
    label: 'Data',
    keywords: ['data', 'backup', 'export', 'import', 'storage', 'tag', 'label', 'quota', 'cleanup'],
    subPages: [
      { id: 'tags', label: 'Tags', keywords: ['tag', 'label', 'category'] },
      { id: 'quotas', label: 'Quotas', keywords: ['quota', 'limit', 'allowance', 'budget'] },
      { id: 'management', label: 'Data Management', keywords: ['backup', 'export', 'import', 'cleanup', 'storage'] },
    ],
  },
  {
    id: 'users',
    label: 'Users',
    keywords: ['user', 'role', 'permission', 'account', 'admin', 'api key', 'profile', 'login', 'audit', 'security'],
    subPages: [
      { id: 'accounts', label: 'User Accounts', keywords: ['user', 'account', 'role', 'permission', 'admin'] },
      { id: 'api-keys', label: 'API Keys', keywords: ['api', 'key', 'token', 'access'] },
      { id: 'audit', label: 'Login Audit', keywords: ['login', 'audit', 'history', 'security', 'log'] },
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
