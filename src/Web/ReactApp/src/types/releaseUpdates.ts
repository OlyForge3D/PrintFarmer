/**
 * Wire shape of `GET /api/admin/release-updates` (issue #3281). Mirrors
 * `ApplicationReleaseUpdateStatusDto` (camelCase JSON, string enums).
 */
export type ApplicationReleaseUpdateStatus =
  | 'UpToDate'
  | 'UpdateAvailable'
  | 'NotChecked'
  | 'CheckFailed'
  | 'Disabled'
  | 'UnknownInstalledVersion';

export type ApplicationReleaseChannel = 'Stable' | 'Insider';

export interface ApplicationReleaseUpdateStatusDto {
  status: ApplicationReleaseUpdateStatus;
  updateAvailable: boolean;
  installedVersion: string | null;
  channel: ApplicationReleaseChannel | null;
  latestVersion: string | null;
  latestTag: string | null;
  latestReleaseName: string | null;
  latestPublishedAt: string | null;
  releaseUrl: string | null;
  lastCheckedAt: string | null;
  lastSuccessfulCheckAt: string | null;
  isStale: boolean;
  error: string | null;
  checkIntervalSeconds: number;
  upgradeDocsUrl: string;
}
