import { useState } from 'react';
import { Alert } from '@/common/components/ui';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useReleaseUpdateStatus } from '@/features/admin/hooks/useReleaseUpdateStatus';

const TRUSTED_LINK_PREFIX = 'https://github.com/OlyForge3D/PrintFarmer/';
const DISMISS_STORAGE_KEY = 'pf.releaseUpdateBanner.dismissedTag';

function safeLink(url: string | null | undefined): string | null {
  return url && url.startsWith(TRUSTED_LINK_PREFIX) ? url : null;
}

function readDismissedTag(): string | null {
  try {
    return sessionStorage.getItem(DISMISS_STORAGE_KEY);
  } catch {
    return null;
  }
}

function formatTimestamp(value: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toLocaleString();
}

/**
 * Farm-admin alert for a newer PrintFarmer release on the installed channel (issue #3281).
 * Non-admins never request the status. Hidden once the installed version catches up.
 */
export function ReleaseUpdateBanner() {
  const { hasRole } = useAuth();
  const isAdmin = hasRole('farm_admin');
  const { data } = useReleaseUpdateStatus({ enabled: isAdmin });
  const [dismissedTag, setDismissedTag] = useState<string | null>(readDismissedTag);

  if (!isAdmin || !data?.updateAvailable || !data.latestVersion || !data.installedVersion) {
    return null;
  }

  if (data.latestTag && dismissedTag === data.latestTag) {
    return null;
  }

  const releaseUrl = safeLink(data.releaseUrl);
  const docsUrl = safeLink(data.upgradeDocsUrl);
  const lastSuccess = formatTimestamp(data.lastSuccessfulCheckAt);
  const checkFailed = data.status === 'CheckFailed';

  const handleDismiss = () => {
    const tag = data.latestTag;
    setDismissedTag(tag);
    try {
      if (tag) sessionStorage.setItem(DISMISS_STORAGE_KEY, tag);
    } catch {
      // Storage unavailable: dismissal lasts for this render tree only.
    }
  };

  return (
    <section aria-label="PrintFarmer update available" className="px-1 pt-1 lg:px-2 lg:pt-2">
      <Alert type="info" title={`PrintFarmer ${data.latestVersion} is available`} onClose={handleDismiss}>
        <p>
          This server runs <strong>{data.installedVersion}</strong>
          {data.channel ? ` (${data.channel.toLowerCase()} channel)` : ''}. Back up your database before upgrading,
          then for an installer-based deployment run{' '}
          <code className="rounded-sm bg-pf-bg-0/60 px-1 font-mono text-xs">
            ./install.sh --upgrade --version {data.latestVersion}
          </code>
          .
        </p>
        <p className="mt-1 flex flex-wrap gap-x-4 gap-y-1">
          {releaseUrl && (
            <a href={releaseUrl} target="_blank" rel="noopener noreferrer" className="underline font-medium">
              Release notes for {data.latestTag}
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          )}
          {docsUrl && (
            <a href={docsUrl} target="_blank" rel="noopener noreferrer" className="underline font-medium">
              Upgrade guide
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          )}
        </p>
        {(checkFailed || data.isStale) && (
          <p className="mt-1 text-pf-text-secondary">
            {checkFailed ? 'The latest release check failed' : 'Release information may be out of date'}
            {lastSuccess ? `; last successful check ${lastSuccess}.` : '.'}
          </p>
        )}
      </Alert>
    </section>
  );
}
