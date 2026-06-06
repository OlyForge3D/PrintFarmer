import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Navigate, Route, Routes, useLocation } from 'react-router';

function LocationEcho({ testId }: { testId: string }) {
  const location = useLocation();
  return <div data-testid={testId}>{`${location.pathname}${location.search}`}</div>;
}

function SettingsRedirect({
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

function RedirectTest({ from }: { from: string }) {
  return render(
    <MemoryRouter initialEntries={[from]}>
      <Routes>
        <Route path="/settings" element={<LocationEcho testId="settings-location" />} />
        <Route path="/analytics" element={<LocationEcho testId="analytics-location" />} />
        <Route path="/printers" element={<LocationEcho testId="printers-location" />} />
        <Route path="/profile/api-keys" element={<LocationEcho testId="api-keys-location" />} />
        <Route path="/nfc-bindings" element={<LocationEcho testId="nfc-bindings-location" />} />
        <Route path="/printer-groups" element={<LocationEcho testId="printer-groups-location" />} />
        <Route path="/spools" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="/spools/:tabId" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="/cameras" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/cameras/:tabId" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/nfc-devices" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/locations" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/users" element={<Navigate to="/settings?scope=admin&tab=users&sub=accounts" replace />} />
        <Route path="/statistics" element={<Navigate to="/analytics?lens=production" replace />} />
        <Route path="/statistics/costs" element={<Navigate to="/analytics?lens=cost" replace />} />
        <Route path="/admin" element={<Navigate to="/settings?scope=admin" replace />} />
        <Route path="/admin/system" element={<Navigate to="/settings?scope=admin&tab=operations&sub=status" replace />} />
        <Route path="/admin/workers" element={<SettingsRedirect to="/settings?scope=admin&tab=operations&sub=workers" searchParamMap={{ tab: 'workerTab' }} />} />
        <Route path="/admin/users" element={<Navigate to="/settings?scope=admin&tab=users&sub=accounts" replace />} />
        <Route path="/admin/tags" element={<Navigate to="/settings?scope=admin&tab=data&sub=tags" replace />} />
        <Route path="/admin/data" element={<Navigate to="/settings?scope=admin&tab=data&sub=management" replace />} />
        <Route path="/admin/security/login-audit" element={<Navigate to="/settings?scope=admin&tab=users&sub=audit" replace />} />
        <Route path="/admin/printers" element={<Navigate to="/printers" replace />} />
        <Route path="/admin/file-health" element={<Navigate to="/settings?scope=admin&tab=operations&sub=status" replace />} />
        <Route path="/admin/monitoring" element={<Navigate to="/settings?scope=admin&tab=operations&sub=status" replace />} />
        <Route path="/admin/slicer-profiles" element={<Navigate to="/settings?scope=system&tab=slicing&sub=profiles" replace />} />
        <Route path="/admin/bed-types" element={<Navigate to="/settings?scope=system&tab=slicing&sub=bed-types" replace />} />
        <Route path="/admin/custom-fields" element={<Navigate to="/settings?scope=system&tab=hardware&sub=custom-fields" replace />} />
        <Route path="/admin/webhooks" element={<Navigate to="/settings?scope=system&tab=integrations" replace />} />
        <Route path="/admin/quotas" element={<Navigate to="/settings?scope=system&tab=quotas" replace />} />
        <Route path="/admin/cameras" element={<Navigate to="/settings?scope=system&tab=hardware&sub=cameras" replace />} />
        <Route path="/slice-jobs" element={<Navigate to="/settings?scope=admin&tab=operations&sub=workers&workerTab=jobs" replace />} />
        <Route path="/slicer-profiles" element={<Navigate to="/settings?scope=system&tab=slicing&sub=profiles" replace />} />
      </Routes>
    </MemoryRouter>
  );
}

describe('Settings nav migration redirects', () => {
  const settingsRedirectCases = [
    { from: '/spools', expected: '/settings?tab=filament', label: '/spools → filament' },
    { from: '/spools/active', expected: '/settings?tab=filament', label: '/spools/:tabId → filament' },
    { from: '/cameras', expected: '/settings?tab=hardware', label: '/cameras → hardware' },
    { from: '/cameras/manage', expected: '/settings?tab=hardware', label: '/cameras/:tabId → hardware' },
    { from: '/nfc-devices', expected: '/settings?tab=hardware', label: '/nfc-devices → hardware' },
    { from: '/locations', expected: '/settings?tab=hardware', label: '/locations → hardware' },
    { from: '/users', expected: '/settings?scope=admin&tab=users&sub=accounts', label: '/users → admin accounts' },
    { from: '/admin', expected: '/settings?scope=admin', label: '/admin → admin landing' },
    { from: '/admin/system', expected: '/settings?scope=admin&tab=operations&sub=status', label: '/admin/system → admin status' },
    { from: '/admin/workers', expected: '/settings?scope=admin&tab=operations&sub=workers', label: '/admin/workers → admin workers' },
    { from: '/admin/workers?tab=jobs', expected: '/settings?scope=admin&tab=operations&sub=workers&workerTab=jobs', label: '/admin/workers?tab=jobs → admin workers jobs tab' },
    { from: '/admin/users', expected: '/settings?scope=admin&tab=users&sub=accounts', label: '/admin/users → admin accounts' },
    { from: '/admin/tags', expected: '/settings?scope=admin&tab=data&sub=tags', label: '/admin/tags → admin tags' },
    { from: '/admin/data', expected: '/settings?scope=admin&tab=data&sub=management', label: '/admin/data → admin data management' },
    { from: '/admin/security/login-audit', expected: '/settings?scope=admin&tab=users&sub=audit', label: '/admin/security/login-audit → admin audit' },
    { from: '/admin/file-health', expected: '/settings?scope=admin&tab=operations&sub=status', label: '/admin/file-health → admin status' },
    { from: '/admin/monitoring', expected: '/settings?scope=admin&tab=operations&sub=status', label: '/admin/monitoring → admin status' },
    { from: '/admin/slicer-profiles', expected: '/settings?scope=system&tab=slicing&sub=profiles', label: '/admin/slicer-profiles → system slicing profiles' },
    { from: '/admin/bed-types', expected: '/settings?scope=system&tab=slicing&sub=bed-types', label: '/admin/bed-types → system bed types' },
    { from: '/admin/custom-fields', expected: '/settings?scope=system&tab=hardware&sub=custom-fields', label: '/admin/custom-fields → system custom fields' },
    { from: '/admin/webhooks', expected: '/settings?scope=system&tab=integrations', label: '/admin/webhooks → system integrations' },
    { from: '/admin/quotas', expected: '/settings?scope=system&tab=quotas', label: '/admin/quotas → system quotas' },
    { from: '/admin/cameras', expected: '/settings?scope=system&tab=hardware&sub=cameras', label: '/admin/cameras → system cameras' },
    { from: '/slice-jobs', expected: '/settings?scope=admin&tab=operations&sub=workers&workerTab=jobs', label: '/slice-jobs → admin workers jobs tab' },
    { from: '/slicer-profiles', expected: '/settings?scope=system&tab=slicing&sub=profiles', label: '/slicer-profiles → system slicing profiles' },
  ];

  settingsRedirectCases.forEach(({ from, expected, label }) => {
    it(`redirects ${label}`, () => {
      RedirectTest({ from });
      expect(screen.getByTestId('settings-location')).toHaveTextContent(expected);
    });
  });

  const analyticsRedirectCases = [
    { from: '/statistics', expected: '/analytics?lens=production', label: '/statistics → /analytics?lens=production' },
    { from: '/statistics/costs', expected: '/analytics?lens=cost', label: '/statistics/costs → /analytics?lens=cost' },
  ];

  analyticsRedirectCases.forEach(({ from, expected, label }) => {
    it(`redirects ${label}`, () => {
      RedirectTest({ from });
      expect(screen.getByTestId('analytics-location')).toHaveTextContent(expected);
    });
  });

  const survivingRouteCases = [
    { from: '/profile/api-keys', testId: 'api-keys-location', expected: '/profile/api-keys', label: '/profile/api-keys stays live' },
    { from: '/nfc-bindings', testId: 'nfc-bindings-location', expected: '/nfc-bindings', label: '/nfc-bindings stays live' },
    { from: '/printer-groups', testId: 'printer-groups-location', expected: '/printer-groups', label: '/printer-groups stays live' },
    { from: '/admin/printers', testId: 'printers-location', expected: '/printers', label: '/admin/printers moves to printers' },
  ];

  survivingRouteCases.forEach(({ from, testId, expected, label }) => {
    it(label, () => {
      RedirectTest({ from });
      expect(screen.getByTestId(testId)).toHaveTextContent(expected);
    });
  });
});
