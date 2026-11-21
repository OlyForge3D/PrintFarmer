import { useEffect, useState } from 'react';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useFarmIdentity } from '@/features/settings/hooks/useFarmIdentity';
import { formatFarmTime } from '@/features/settings/utils/farmTime';

/** Branding stays readable at narrow widths; the full identity is available on hover. */
export function FarmIdentityBrand() {
  const { isAuthenticated } = useAuth();
  const { data: identity, error } = useFarmIdentity(isAuthenticated);
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 30_000);
    return () => window.clearInterval(timer);
  }, []);

  const name = isAuthenticated && identity ? identity.farmName : 'PrintFarmer';
  const farmTime = identity ? formatFarmTime(now, identity.timeZone) : null;
  return (
    <div className="min-w-0">
      <div title={name} className="truncate font-bebas text-lg font-bold tracking-wide text-pf-text-primary">{name}</div>
      {isAuthenticated && error ? (
        <div role="status" className="truncate text-xs text-pf-error">Farm identity unavailable</div>
      ) : isAuthenticated && identity ? (
        farmTime === null ? (
          <div role="status" title={identity.timeZone} className="truncate text-xs text-pf-error">
            Farm timezone unsupported by this browser
          </div>
        ) : (
          <div title={`Farm time (${identity.timeZone})`} className="truncate text-xs text-pf-text-secondary">
            <span className="hidden lg:inline">Farm time: </span>
            <time aria-label={`Farm time: ${farmTime} (${identity.timeZone})`} dateTime={now.toISOString()}>{farmTime}</time>
            <span className="hidden lg:inline"> ({identity.timeZone})</span>
          </div>
        )
      ) : null}
    </div>
  );
}
