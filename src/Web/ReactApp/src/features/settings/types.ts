export type SettingsScopeId = 'user' | 'system' | 'admin';

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
  scopeId: SettingsScopeId;
  label: string;
  description: string;
  keywords: string[];
  subPages: SettingsSubPage[];
}

export interface SettingsScope {
  id: SettingsScopeId;
  label: string;
  description: string;
  keywords: string[];
  defaultCategoryId: string;
  adminOnly?: boolean;
}

/** @deprecated Use SettingsCategory instead */
export interface SettingsTab {
  id: string;
  label: string;
  icon?: React.ReactNode;
  keywords: string[];
}

export const SETTINGS_SCOPES: SettingsScope[] = [
  {
    id: 'user',
    label: 'User Settings',
    description: 'Personal preferences, security, notifications, and account-level tools.',
    keywords: ['user', 'profile', 'preferences', 'theme', 'locale', 'api keys', 'notifications', 'passkeys'],
    defaultCategoryId: 'profile',
  },
  {
    id: 'system',
    label: 'System Settings',
    description: 'Farm-wide configuration for printers, slicing, integrations, and policies.',
    keywords: ['system', 'farm', 'hardware', 'slicing', 'integrations', 'quotas', 'defaults'],
    defaultCategoryId: 'general',
    adminOnly: true,
  },
  {
    id: 'admin',
    label: 'Admin',
    description: 'Operational dashboards, user management, audit trails, and data maintenance.',
    keywords: ['admin', 'operations', 'status', 'workers', 'users', 'audit', 'data'],
    defaultCategoryId: 'operations',
    adminOnly: true,
  },
];

/** Settings categories with their sub-pages */
export const SETTINGS_CATEGORIES: SettingsCategory[] = [
  {
    id: 'profile',
    scopeId: 'user',
    label: 'Profile',
    description: 'Theme, locale, items per page, API keys, notifications, and passkeys.',
    keywords: ['profile', 'preferences', 'theme', 'appearance', 'locale', 'items per page', 'api', 'key', 'notification', 'passkey', 'security'],
    subPages: [
      { id: 'preferences', label: 'Preferences', description: 'Adjust theme, locale, and list density for your account.', keywords: ['theme', 'appearance', 'locale', 'items per page', 'preferences'] },
      { id: 'api-keys', label: 'API Keys', description: 'Create and revoke personal API credentials.', keywords: ['api', 'key', 'token', 'access'] },
      { id: 'notifications', label: 'Notifications', description: 'Choose how PrintFarmer notifies you about events.', keywords: ['notification', 'email', 'alert', 'push'] },
      { id: 'passkeys', label: 'Passkeys', description: 'Manage passwordless sign-in devices for your account.', keywords: ['passkey', 'security', 'webauthn', 'login'] },
    ],
  },
  {
    id: 'general',
    scopeId: 'system',
    label: 'General',
    description: 'Farm identity, timezone, appearance defaults, and core configuration.',
    keywords: ['general', 'farm', 'name', 'timezone', 'language', 'appearance', 'theme', 'system'],
    subPages: [],
  },
  {
    id: 'slicing',
    scopeId: 'system',
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
    scopeId: 'system',
    label: 'Hardware',
    description: 'Cameras, NFC, printer groups, locations, and device metadata.',
    keywords: ['printer', 'hardware', 'camera', 'nfc', 'location', 'custom field', 'device', 'webcam', 'group', 'binding'],
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
    id: 'integrations',
    scopeId: 'system',
    label: 'Integrations',
    description: 'Webhooks, automation endpoints, and external connections.',
    keywords: ['integration', 'api', 'key', 'external', 'webhook', 'automation', 'endpoint'],
    subPages: [],
  },
  {
    id: 'quotas',
    scopeId: 'system',
    label: 'Quotas',
    description: 'Usage limits, allowance policies, and farm-wide constraints.',
    keywords: ['quota', 'limit', 'allowance', 'budget', 'policy', 'data'],
    subPages: [],
  },
  {
    id: 'operations',
    scopeId: 'admin',
    label: 'Operations',
    description: 'System health, worker status, and operational monitoring.',
    keywords: ['system', 'status', 'health', 'workers', 'services', 'jobs', 'queue', 'monitoring'],
    subPages: [
      { id: 'status', label: 'Status', description: 'Inspect uptime, health, and infrastructure signals.', keywords: ['status', 'health', 'uptime', 'cpu', 'memory', 'disk', 'database', 'services'] },
      { id: 'workers', label: 'Workers', description: 'Monitor slicer workers, queues, and background jobs.', keywords: ['workers', 'slicer', 'jobs', 'queue', 'processing'] },
    ],
  },
  {
    id: 'users',
    scopeId: 'admin',
    label: 'Users',
    description: 'Accounts, roles, permissions, and authentication history.',
    keywords: ['user', 'role', 'permission', 'account', 'admin', 'login', 'audit', 'security'],
    subPages: [
      { id: 'accounts', label: 'User Accounts', description: 'Manage accounts, roles, and access levels.', keywords: ['user', 'account', 'role', 'permission', 'admin'] },
      { id: 'audit', label: 'Login Audit', description: 'Review authentication attempts and sign-in history.', keywords: ['login', 'audit', 'history', 'security', 'log'] },
    ],
  },
  {
    id: 'data',
    scopeId: 'admin',
    label: 'Data',
    description: 'Tags, exports, backups, cleanup workflows, and maintenance tasks.',
    keywords: ['data', 'backup', 'export', 'import', 'storage', 'tag', 'label', 'cleanup'],
    subPages: [
      { id: 'tags', label: 'Tags', description: 'Manage reusable labels across the farm.', keywords: ['tag', 'label', 'category'] },
      { id: 'management', label: 'Data Management', description: 'Run export, import, backup, and cleanup tasks.', keywords: ['backup', 'export', 'import', 'cleanup', 'storage'] },
    ],
  },
];

const settingsCategoryLookup = new Map(SETTINGS_CATEGORIES.map((category) => [category.id, category]));
const settingsScopeLookup = new Map(SETTINGS_SCOPES.map((scope) => [scope.id, scope]));

export const SETTINGS_CATEGORIES_BY_SCOPE: Record<SettingsScopeId, SettingsCategory[]> = {
  user: SETTINGS_CATEGORIES.filter((category) => category.scopeId === 'user'),
  system: SETTINGS_CATEGORIES.filter((category) => category.scopeId === 'system'),
  admin: SETTINGS_CATEGORIES.filter((category) => category.scopeId === 'admin'),
};

/** @deprecated Use SETTINGS_CATEGORIES instead */
export const SETTINGS_TABS: SettingsTab[] = SETTINGS_CATEGORIES.map((category) => ({
  id: category.id,
  label: category.label,
  keywords: category.keywords,
}));

export const DEFAULT_SCOPE: SettingsScopeId = 'user';
export const DEFAULT_CATEGORY = getDefaultCategoryForScope(DEFAULT_SCOPE);

/** @deprecated Use DEFAULT_CATEGORY instead */
export const DEFAULT_TAB = DEFAULT_CATEGORY;

export function isSettingsScope(value: string | null | undefined): value is SettingsScopeId {
  return value === 'user' || value === 'system' || value === 'admin';
}

export function getSettingsScope(scopeId: SettingsScopeId): SettingsScope | undefined {
  return settingsScopeLookup.get(scopeId);
}

export function getSettingsCategory(categoryId: string): SettingsCategory | undefined {
  return settingsCategoryLookup.get(categoryId);
}

export function getSettingsCategoriesForScope(scopeId: SettingsScopeId): SettingsCategory[] {
  return SETTINGS_CATEGORIES_BY_SCOPE[scopeId];
}

export function getDefaultCategoryForScope(scopeId: SettingsScopeId): string {
  return getSettingsScope(scopeId)?.defaultCategoryId ?? getSettingsScope(DEFAULT_SCOPE)?.defaultCategoryId ?? 'profile';
}

export function getSettingsScopeForCategory(categoryId: string): SettingsScopeId {
  return getSettingsCategory(categoryId)?.scopeId ?? DEFAULT_SCOPE;
}

/** Get the default sub-page for a category (first one, or empty string if none) */
export function getDefaultSubPage(categoryId: string): string {
  return getSettingsCategory(categoryId)?.subPages[0]?.id ?? '';
}
