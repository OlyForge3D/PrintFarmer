/**
 * Legacy path -> canonical destination map.
 *
 * Every entry in this file corresponds to a `<Route ... element={<Navigate .../>}>`
 * (or equivalent) declaration in `App.tsx`. This file is the source of truth for:
 *
 * 1. **Documentation (#940 — Ash)**: the "if you had this URL bookmarked, it now
 *    goes here" table in the release notes and migration guide.
 * 2. **Regression tests (#939 — Kane)**: the integration suite iterates this list
 *    to confirm every entry still resolves to its stated new path.
 * 3. **Ripley's own coverage** (this issue): the tests co-located with this
 *    registry pin each redirect at the component level so we can be sure the
 *    map matches what `App.tsx` actually does.
 *
 * ⚠️ **Do not delete entries when you rename a route.** Add a NEW entry pointing
 * the old path to the new path so external bookmarks keep working. Removing a
 * mapping is an automatic rejection.
 *
 * ⚠️ **Query-string preservation:** most redirects use `<Navigate>` which drops
 * incoming search params entirely. Where that behaviour is intentional it is
 * documented in `notes`. For redirects that must preserve or remap parameters
 * (currently `/admin/workers` and `/admin/system`), the target string is a
 * template and the redirect implementation in `App.tsx` handles the rewrite.
 */

export interface LegacyRedirect {
  /** Full legacy path (without host, without leading protocol). */
  from: string;
  /**
   * Canonical destination path, including query string when required.
   *
   * The value is compared verbatim to `location.pathname + location.search`
   * during redirect tests. Use `?tab=x&sub=y` to target a specific SettingsShell
   * sub-page.
   */
  to: string;
  /** Human-readable purpose of the legacy path (surfaces in docs and test names). */
  description: string;
  /**
   * Optional freeform notes — dropped search-params, parameter remaps, or the
   * PR that introduced the redirect.
   */
  notes?: string;
}

/**
 * Canonical list of legacy admin/settings/hardware paths and where they now go.
 *
 * Groups match the sections in `App.tsx` to make cross-referencing easy.
 */
export const LEGACY_REDIRECTS: readonly LegacyRedirect[] = [
  // ── /admin/* legacy paths ─────────────────────────────────────────────
  // Note: `/admin` itself is no longer a redirect — since #936 it renders the
  // Admin Control Center hub. See `App.tsx` and `AdminControlCenterPage.tsx`.
  {
    from: '/admin/printers',
    to: '/printers',
    description: 'Duplicate of the top-level /printers destination.',
  },
  {
    from: '/admin/file-health',
    to: '/admin/manage?tab=operations&sub=status',
    description: 'File health used to be a dedicated page — folded into System Status.',
  },
  {
    from: '/admin/slicer-profiles',
    to: '/admin/settings?tab=slicing&sub=profiles',
    description: 'Slicer profile library moved into System Settings > Slicing.',
  },
  {
    from: '/admin/tags',
    to: '/admin/manage?tab=data&sub=tags',
    description: 'Tag administration moved into Admin Console > Data.',
  },
  {
    from: '/admin/bed-types',
    to: '/admin/settings?tab=slicing&sub=bed-types',
    description: 'Bed types moved into System Settings > Slicing.',
  },
  {
    from: '/admin/custom-fields',
    to: '/admin/settings?tab=hardware&sub=custom-fields',
    description: 'Custom fields moved into System Settings > Hardware.',
  },
  {
    from: '/admin/webhooks',
    to: '/admin/settings?tab=integrations',
    description: 'Webhooks moved into System Settings > Integrations.',
    notes: 'Lands on the Integrations tab; SettingsShell may auto-select the Webhooks sub-page.',
  },
  {
    from: '/admin/quotas',
    to: '/admin/settings?tab=quotas',
    description: 'Quota administration moved into System Settings > Quotas.',
  },
  {
    from: '/admin/data',
    to: '/admin/manage?tab=data&sub=management',
    description: 'Data management moved into Admin Console > Data.',
  },
  {
    from: '/admin/monitoring',
    to: '/admin/manage?tab=operations&sub=status',
    description: 'Legacy monitoring page — folded into System Status.',
  },
  {
    from: '/admin/cameras',
    to: '/admin/settings?tab=hardware&sub=cameras',
    description: 'Camera administration moved into System Settings > Hardware.',
  },
  {
    from: '/admin/security/login-audit',
    to: '/admin/manage?tab=users&sub=audit',
    description: 'Login audit moved into Admin Console > Users.',
  },
  {
    from: '/admin/settings-legacy',
    to: '/admin/settings?tab=general',
    description: 'Legacy alias for the pre-SettingsShell admin settings page.',
  },

  // ── /admin/workers and /admin/system: parametric rewrites ─────────────
  {
    from: '/admin/workers',
    to: '/admin/manage?tab=operations&sub=workers',
    description: 'Worker management moved into Admin Console > Operations.',
    notes: 'Implemented via LegacySettingsRedirect: any incoming `tab` search param is remapped to `workerTab` so deep-links to a specific worker tab keep working.',
  },
  {
    from: '/admin/system',
    to: '/admin/manage?tab=operations&sub=status',
    description: 'System administration was multi-tab — folded into Admin Console > Operations.',
    notes: 'Implemented via LegacySystemTabRedirect: legacy `?tab=services|logs|connections|monitoring` values each map to a specific Operations sub-page.',
  },

  // ── Top-level legacy paths ────────────────────────────────────────────
  {
    from: '/users',
    to: '/admin/manage?tab=users&sub=accounts',
    description: 'Top-level Users route moved under the admin namespace.',
  },
  {
    from: '/settings/system',
    to: '/admin/settings?tab=general',
    description: 'Legacy top-level System Settings shortcut.',
  },
  {
    from: '/preferences',
    to: '/settings',
    description: 'Legacy preferences shortcut — now the user Settings page.',
  },
  {
    from: '/locations',
    to: '/locations/dashboard',
    description: 'Bare /locations lands on the Locations dashboard.',
  },
  {
    from: '/cameras',
    to: '/admin/settings?tab=hardware&sub=cameras',
    description: 'Legacy top-level Cameras page.',
  },
  {
    from: '/nfc-devices',
    to: '/admin/settings?tab=hardware&sub=nfc',
    description: 'Legacy top-level NFC Devices page.',
  },
  {
    from: '/statistics',
    to: '/analytics?lens=production',
    description: 'Legacy Statistics route — now the Analytics production lens.',
  },
  {
    from: '/statistics/costs',
    to: '/analytics?lens=cost',
    description: 'Legacy statistics cost view — now the Analytics cost lens.',
  },
  {
    from: '/slicer-profiles',
    to: '/admin/settings?tab=slicing&sub=profiles',
    description: 'Legacy top-level Slicer Profiles page.',
  },
  {
    from: '/slicer/import-official',
    to: '/profiles/import',
    description: 'Legacy import-official shortcut — now the shared profile import wizard.',
  },
  {
    from: '/slice-jobs',
    to: '/admin/manage?tab=operations&sub=workers&workerTab=jobs',
    description: 'Legacy Slice Jobs list — now the Jobs tab under Workers.',
  },
  {
    from: '/files/projects',
    to: '/projects',
    description: 'Legacy nested Projects path.',
  },
];

/**
 * Find a legacy redirect by its original path.
 *
 * Returns `undefined` when the path is not a known legacy alias.
 */
export function getLegacyRedirect(from: string): LegacyRedirect | undefined {
  return LEGACY_REDIRECTS.find((redirect) => redirect.from === from);
}
