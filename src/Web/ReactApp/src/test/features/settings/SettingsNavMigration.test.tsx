import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Navigate, Route, Routes, useLocation } from 'react-router';

function LocationEcho({ testId }: { testId: string }) {
  const location = useLocation();
  return <div data-testid={testId}>{`${location.pathname}${location.search}`}</div>;
}

function LegacySettingsRedirect({
  to,
  searchParamMap,
}: {
  to: string;
  searchParamMap?: Record<string, string>;
}) {
  const location = useLocation();
  const [pathname, search = ''] = to.split('?');
  const currentSearchParams = new URLSearchParams(location.search);
  const nextSearchParams = new URLSearchParams(search);

  Object.entries(searchParamMap ?? {}).forEach(([fromKey, toKey]) => {
    const value = currentSearchParams.get(fromKey);
    if (value) {
      nextSearchParams.set(toKey, value);
    }
  });

  const nextLocation = nextSearchParams.toString()
    ? `${pathname}?${nextSearchParams.toString()}`
    : pathname;

  return <Navigate to={nextLocation} replace />;
}

function SystemSettingsEchoOrLocationsRedirect() {
  const location = useLocation();
  const params = new URLSearchParams(location.search);
  if (params.get('tab') === 'hardware' && params.get('sub') === 'locations') {
    return <Navigate to="/locations/dashboard" replace />;
  }

  return <LocationEcho testId="settings-location" />;
}

function renderRedirect(from: string) {
  return render(
    <MemoryRouter initialEntries={[from]}>
      <Routes>
        <Route path="/settings" element={<LocationEcho testId="settings-location" />} />
        <Route path="/admin/settings" element={<SystemSettingsEchoOrLocationsRedirect />} />
        <Route path="/admin/manage" element={<LocationEcho testId="settings-location" />} />
        <Route path="/analytics" element={<LocationEcho testId="analytics-location" />} />
        <Route path="/locations/dashboard" element={<LocationEcho testId="locations-location" />} />
        <Route path="/profile/api-keys" element={<LocationEcho testId="api-keys-location" />} />
        <Route path="/nfc-bindings" element={<LocationEcho testId="nfc-bindings-location" />} />
        <Route path="/printer-groups" element={<LocationEcho testId="printer-groups-location" />} />
        <Route path="/printers" element={<LocationEcho testId="printers-location" />} />
        <Route path="/admin/printers" element={<Navigate to="/printers" replace />} />
        <Route path="/preferences" element={<Navigate to="/settings" replace />} />
        <Route path="/cameras" element={<Navigate to="/admin/settings?tab=hardware&sub=cameras" replace />} />
        <Route path="/cameras/:tabId" element={<Navigate to="/admin/settings?tab=hardware&sub=cameras" replace />} />
        <Route path="/nfc-devices" element={<Navigate to="/admin/settings?tab=hardware&sub=nfc" replace />} />
        <Route path="/locations" element={<Navigate to="/locations/dashboard" replace />} />
        <Route path="/users" element={<Navigate to="/admin/manage?tab=users&sub=accounts" replace />} />
        <Route path="/statistics" element={<Navigate to="/analytics?lens=production" replace />} />
        <Route path="/statistics/costs" element={<Navigate to="/analytics?lens=cost" replace />} />
        <Route path="/admin" element={<Navigate to="/admin/settings" replace />} />
        <Route path="/admin/system" element={<Navigate to="/admin/manage?tab=operations&sub=status" replace />} />
        <Route path="/admin/settings-legacy" element={<Navigate to="/admin/settings?tab=general" replace />} />
        <Route path="/admin/workers" element={<LegacySettingsRedirect to="/admin/manage?tab=operations&sub=workers" searchParamMap={{ tab: 'workerTab' }} />} />
        <Route path="/admin/users" element={<Navigate to="/admin/manage?tab=users&sub=accounts" replace />} />
        <Route path="/admin/tags" element={<Navigate to="/admin/manage?tab=data&sub=tags" replace />} />
        <Route path="/admin/data" element={<Navigate to="/admin/manage?tab=data&sub=management" replace />} />
        <Route path="/admin/security/login-audit" element={<Navigate to="/admin/manage?tab=users&sub=audit" replace />} />
        <Route path="/admin/file-health" element={<Navigate to="/admin/manage?tab=operations&sub=status" replace />} />
        <Route path="/admin/monitoring" element={<Navigate to="/admin/manage?tab=operations&sub=status" replace />} />
        <Route path="/admin/slicer-profiles" element={<Navigate to="/admin/settings?tab=slicing&sub=profiles" replace />} />
        <Route path="/admin/bed-types" element={<Navigate to="/admin/settings?tab=slicing&sub=bed-types" replace />} />
        <Route path="/admin/custom-fields" element={<Navigate to="/admin/settings?tab=hardware&sub=custom-fields" replace />} />
        <Route path="/admin/webhooks" element={<Navigate to="/admin/settings?tab=integrations" replace />} />
        <Route path="/admin/quotas" element={<Navigate to="/admin/settings?tab=quotas" replace />} />
        <Route path="/admin/cameras" element={<Navigate to="/admin/settings?tab=hardware&sub=cameras" replace />} />
        <Route path="/slice-jobs" element={<Navigate to="/admin/manage?tab=operations&sub=workers&workerTab=jobs" replace />} />
        <Route path="/slicer-profiles" element={<Navigate to="/admin/settings?tab=slicing&sub=profiles" replace />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('Settings nav migration redirects', () => {
  const settingsRedirectCases = [
    { from: '/preferences', expected: '/settings', label: '/preferences → /settings' },
    { from: '/cameras', expected: '/admin/settings?tab=hardware&sub=cameras', label: '/cameras → system cameras' },
    { from: '/cameras/manage', expected: '/admin/settings?tab=hardware&sub=cameras', label: '/cameras/:tabId → system cameras' },
    { from: '/nfc-devices', expected: '/admin/settings?tab=hardware&sub=nfc', label: '/nfc-devices → system nfc devices' },
    { from: '/users', expected: '/admin/manage?tab=users&sub=accounts', label: '/users → admin accounts' },
    { from: '/admin', expected: '/admin/settings', label: '/admin → admin landing' },
    { from: '/admin/system', expected: '/admin/manage?tab=operations&sub=status', label: '/admin/system → admin status' },
    { from: '/admin/settings-legacy', expected: '/admin/settings?tab=general', label: '/admin/settings-legacy → system general' },
    { from: '/admin/workers', expected: '/admin/manage?tab=operations&sub=workers', label: '/admin/workers → admin workers' },
    { from: '/admin/workers?tab=jobs', expected: '/admin/manage?tab=operations&sub=workers&workerTab=jobs', label: '/admin/workers?tab=jobs → admin workers jobs tab' },
    { from: '/admin/users', expected: '/admin/manage?tab=users&sub=accounts', label: '/admin/users → admin accounts' },
    { from: '/admin/tags', expected: '/admin/manage?tab=data&sub=tags', label: '/admin/tags → admin tags' },
    { from: '/admin/data', expected: '/admin/manage?tab=data&sub=management', label: '/admin/data → admin data management' },
    { from: '/admin/security/login-audit', expected: '/admin/manage?tab=users&sub=audit', label: '/admin/security/login-audit → admin audit' },
    { from: '/admin/file-health', expected: '/admin/manage?tab=operations&sub=status', label: '/admin/file-health → admin status' },
    { from: '/admin/monitoring', expected: '/admin/manage?tab=operations&sub=status', label: '/admin/monitoring → admin status' },
    { from: '/admin/slicer-profiles', expected: '/admin/settings?tab=slicing&sub=profiles', label: '/admin/slicer-profiles → system slicing profiles' },
    { from: '/admin/bed-types', expected: '/admin/settings?tab=slicing&sub=bed-types', label: '/admin/bed-types → system bed types' },
    { from: '/admin/custom-fields', expected: '/admin/settings?tab=hardware&sub=custom-fields', label: '/admin/custom-fields → system custom fields' },
    { from: '/admin/webhooks', expected: '/admin/settings?tab=integrations', label: '/admin/webhooks → system integrations' },
    { from: '/admin/quotas', expected: '/admin/settings?tab=quotas', label: '/admin/quotas → system quotas' },
    { from: '/admin/cameras', expected: '/admin/settings?tab=hardware&sub=cameras', label: '/admin/cameras → system cameras' },
    { from: '/slice-jobs', expected: '/admin/manage?tab=operations&sub=workers&workerTab=jobs', label: '/slice-jobs → admin workers jobs tab' },
    { from: '/slicer-profiles', expected: '/admin/settings?tab=slicing&sub=profiles', label: '/slicer-profiles → system slicing profiles' },
  ];

  settingsRedirectCases.forEach(({ from, expected, label }) => {
    it(`redirects ${label}`, () => {
      renderRedirect(from);
      expect(screen.getByTestId('settings-location')).toHaveTextContent(expected);
    });
  });

  const analyticsRedirectCases = [
    { from: '/statistics', expected: '/analytics?lens=production', label: '/statistics → /analytics?lens=production' },
    { from: '/statistics/costs', expected: '/analytics?lens=cost', label: '/statistics/costs → /analytics?lens=cost' },
  ];

  const locationsRedirectCases = [
    { from: '/locations', expected: '/locations/dashboard', label: '/locations → unified location dashboard' },
    { from: '/admin/settings?tab=hardware&sub=locations', expected: '/locations/dashboard', label: 'legacy settings locations deep link → unified location dashboard' },
  ];

  locationsRedirectCases.forEach(({ from, expected, label }) => {
    it(`redirects ${label}`, () => {
      renderRedirect(from);
      expect(screen.getByTestId('locations-location')).toHaveTextContent(expected);
    });
  });

  analyticsRedirectCases.forEach(({ from, expected, label }) => {
    it(`redirects ${label}`, () => {
      renderRedirect(from);
      expect(screen.getByTestId('analytics-location')).toHaveTextContent(expected);
    });
  });

  const survivingRouteCases = [
    { from: '/profile/api-keys', testId: 'api-keys-location', expected: '/profile/api-keys', label: '/profile/api-keys stays live' },
    { from: '/nfc-bindings', testId: 'nfc-bindings-location', expected: '/nfc-bindings', label: '/nfc-bindings stays live' },
    { from: '/printer-groups', testId: 'printer-groups-location', expected: '/printer-groups', label: '/printer-groups stays live' },
    { from: '/admin/printers', testId: 'printers-location', expected: '/printers', label: '/admin/printers → /printers' },
  ];

  survivingRouteCases.forEach(({ from, testId, expected, label }) => {
    it(label, () => {
      renderRedirect(from);
      expect(screen.getByTestId(testId)).toHaveTextContent(expected);
    });
  });
});
