/**
 * `allowedGroups` passed to each `SettingsPage` sub-page inside
 * `SettingsShell.tsx`. Broken out into its own module so tests (and anyone
 * adding a new group) can cross-check it against `SETTINGS_GROUP_TO_LOCATION`
 * in `settings-navigation.ts` without pulling in the entire shell.
 *
 * Adding a settings group requires editing BOTH this map AND
 * `SETTINGS_GROUP_TO_LOCATION` — miss either and the group silently disappears
 * from the UI *and* the command palette. That is exactly how the `Job Queue`
 * group went missing.
 *
 * Only sub-pages that render a metadata-driven `SettingsPage` appear here;
 * sub-pages that mount a dedicated admin page (webhooks, tags, etc.) are not
 * groups in the metadata sense and are intentionally omitted.
 */
export const SUB_PAGE_ALLOWED_GROUPS: Record<string, readonly string[]> = {
  'general.farm': ['General'],
  'general.system': ['System', 'Networking', 'Catalog', 'Files', 'Printers'],
  'general.automation': ['Operations', 'Monitoring', 'Maintenance', 'Job Queue'],
  'integrations.connections': ['Integrations'],
  'slicing.defaults': ['Slicing'],
};
