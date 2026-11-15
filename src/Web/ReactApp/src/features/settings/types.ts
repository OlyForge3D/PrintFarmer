export type { PrinterControlMode, UserSettingsResponse, UpdateUserSettingsRequest } from '@/features/settings/types/index';
export type { FarmSettingsResponse, UpdateFarmSettingsRequest } from '@/features/settings/types/index';

export type SettingsScopeId = 'user' | 'system';

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
    label: 'Farm & Admin Settings',
    description: 'Farm-wide configuration, accounts, access, integrations, and policies.',
    keywords: ['system', 'farm', 'hardware', 'slicing', 'integrations', 'quotas', 'defaults'],
    defaultCategoryId: 'system',
    adminOnly: true,
  },
];

/** Settings categories with their sub-pages */
export const SETTINGS_CATEGORIES: SettingsCategory[] = [
  {
    id: 'identity',
    scopeId: 'system',
    label: 'Farm Identity',
    description: 'Farm name and timezone for the farm-local clock.',
    keywords: ['farm', 'identity', 'name', 'timezone', 'clock'],
    subPages: [
      { id: 'details', label: 'Farm Identity', description: 'Customize the farm name and farm-local clock timezone without changing schedules.', keywords: ['farm', 'name', 'timezone', 'identity', 'clock'] },
    ],
  },
  {
    id: 'profile',
    scopeId: 'user',
    label: 'Profile',
    description: 'Account preferences, appearance, API keys, notifications, and passkeys.',
    keywords: ['profile', 'account', 'personal settings'],
    subPages: [
      { id: 'preferences', label: 'Preferences', description: 'Choose Guided or Expert printer controls, locale, list density, and Printables username.', keywords: ['guided', 'expert', 'printer', 'motion', 'guidance', 'hints', 'control mode', 'locale', 'items per page', 'printables', 'username', 'preferences'] },
      { id: 'appearance', label: 'Appearance', description: 'Choose a color theme and preview the dashboard in real time.', keywords: ['theme', 'appearance', 'color', 'dark', 'light', 'preview'] },
      { id: 'api-keys', label: 'API Keys', description: 'Create and revoke personal API credentials.', keywords: ['api', 'key', 'token', 'access'] },
      { id: 'notifications', label: 'Notifications', description: 'Choose how PrintFarmer notifies you about events.', keywords: ['notification', 'email', 'alert', 'push'] },
      { id: 'passkeys', label: 'Passkeys', description: 'Manage passwordless sign-in devices for your account.', keywords: ['passkey', 'security', 'webauthn', 'login'] },
    ],
  },
  {
    id: 'system',
    scopeId: 'system',
    label: 'System',
    description: 'Database, logging, printer connectivity, networking, and files.',
    keywords: ['system', 'database', 'logging', 'networking', 'files', 'discovery', 'camera', 'connectivity'],
    subPages: [],
  },
  {
    id: 'failure-detection',
    scopeId: 'system',
    label: 'Failure Detection',
    description: 'Obico configuration, server pool, and PrintGuard runtime status.',
    keywords: ['failure', 'detection', 'obico', 'printguard', 'monitoring'],
    subPages: [],
  },
  {
    id: 'costs',
    scopeId: 'system',
    label: 'Costs & Energy',
    description: 'Farm electricity tariff, machine rates, and material cost defaults.',
    keywords: ['cost', 'energy', 'electricity', 'tariff', 'wattage', 'material', 'price'],
    subPages: [],
  },
  {
    id: 'planning',
    scopeId: 'system',
    label: 'Planning',
    description: 'Queue work windows, shifts, filament coverage, and history seeding.',
    keywords: ['planning', 'queue', 'shift', 'spool', 'coverage', 'history'],
    subPages: [],
  },
  {
    id: 'operator-features',
    scopeId: 'system',
    label: 'Operator Features',
    description: 'Farm-wide availability of operator workflows.',
    keywords: ['operator', 'feature', 'availability', 'attention', 'capability'],
    subPages: [],
  },
  {
    id: 'slicing',
    scopeId: 'system',
    label: 'Slicing',
    description: 'Bed types, slicer profiles, and print process defaults.',
    keywords: ['slicer', 'slice', 'profile', 'orcaslicer', 'prusaslicer', 'bed type', 'nozzle', 'process', 'print settings'],
    subPages: [
      { id: 'defaults', label: 'Defaults', description: 'Configure slicer defaults and print process behavior.', keywords: ['slicer', 'default', 'process', 'print settings', 'nozzle', 'speed'] },
      { id: 'bed-types', label: 'Bed Types', description: 'Manage bed surfaces and plate presets.', keywords: ['bed', 'type', 'surface', 'plate'] },
      { id: 'profiles', label: 'Slicer Profiles', description: 'Review OrcaSlicer and PrusaSlicer profile libraries.', keywords: ['profile', 'slicer', 'orcaslicer', 'prusaslicer', 'process'] },
    ],
  },
  {
    id: 'hardware',
    scopeId: 'system',
    label: 'Hardware',
    description: 'NFC readers and bindings, printer groups, locations, and power monitors.',
    keywords: ['printer', 'printers', 'hardware', 'nfc', 'custom field', 'device', 'group', 'binding'],
    subPages: [
      { id: 'nfc', label: 'NFC', description: 'Manage NFC readers and spool-tag bindings.', keywords: ['nfc', 'tag', 'reader', 'rfid', 'binding', 'spool', 'tray'] },
      { id: 'printer-groups', label: 'Printer Groups', description: 'Organize printers into shared operational groups.', keywords: ['printer', 'printers', 'hardware', 'group', 'grouping', 'cluster'] },
    ],
  },
  {
    id: 'integrations',
    scopeId: 'system',
    label: 'Integrations',
    description: 'Webhooks, automation endpoints, and external connections.',
    keywords: ['integration', 'api', 'key', 'external', 'webhook', 'automation', 'endpoint'],
    subPages: [
      { id: 'connections', label: 'External Services', description: 'Configure Spoolman, Home Assistant, Telegram, and slicer auth.', keywords: ['spoolman', 'home assistant', 'telegram', 'slicer', 'octoprint'] },
      { id: 'webhooks', label: 'Webhooks', description: 'Manage outgoing webhook endpoints.', keywords: ['webhook', 'api', 'endpoints'] },
    ],
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
    id: 'users',
    scopeId: 'system',
    label: 'Users',
    description: 'Accounts, roles, and permissions.',
    keywords: ['user', 'role', 'permission', 'account', 'admin', 'security'],
    subPages: [
      { id: 'accounts', label: 'User Accounts', description: 'Manage accounts, roles, and access levels.', keywords: ['user', 'account', 'role', 'permission', 'admin'] },
      { id: 'roles', label: 'Roles & Permissions', description: 'Create custom roles and manage their permission grants.', keywords: ['role', 'permission', 'matrix', 'grant', 'deny', 'access control'] },
    ],
  },
  {
    id: 'data',
    scopeId: 'system',
    label: 'Data',
    description: 'Tags, automatic tagging rules, and custom record fields.',
    keywords: ['data', 'tag', 'label', 'organization'],
    subPages: [
      { id: 'tags', label: 'Tags', description: 'Manage reusable labels across the farm.', keywords: ['tag', 'label', 'category'] },
      { id: 'auto-tagging', label: 'Auto-Tagging', description: 'Configure automatic tag rules.', keywords: ['auto', 'tag', 'rule', 'material'] },
      { id: 'custom-fields', label: 'Custom Fields', description: 'Extend printer and user records with metadata.', keywords: ['custom', 'field', 'attribute', 'metadata'] },
    ],
  },
];

export const USER_SETTINGS_CATEGORIES = SETTINGS_CATEGORIES.filter((category) => category.scopeId === 'user');

const settingsCategoryLookup = new Map(SETTINGS_CATEGORIES.map((category) => [category.id, category]));
const settingsScopeLookup = new Map(SETTINGS_SCOPES.map((scope) => [scope.id, scope]));

export const SETTINGS_CATEGORIES_BY_SCOPE: Record<SettingsScopeId, SettingsCategory[]> = {
  user: USER_SETTINGS_CATEGORIES,
  system: SETTINGS_CATEGORIES.filter((category) => category.scopeId === 'system'),
};

export const DEFAULT_SCOPE: SettingsScopeId = 'user';
export const DEFAULT_CATEGORY = getDefaultCategoryForScope(DEFAULT_SCOPE);

export function isSettingsScope(value: string | null | undefined): value is SettingsScopeId {
  return value === 'user' || value === 'system';
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
