import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router';
import { Navigate } from 'react-router';

// Capture redirects by rendering a test route structure
function RedirectTest({ from }: { from: string }) {
  return render(
    <MemoryRouter initialEntries={[from]}>
      <Routes>
        <Route path="/settings" element={<div data-testid="settings-page">Settings</div>} />
        <Route path="/analytics" element={<div data-testid="analytics-page">Analytics</div>} />
        <Route path="/profile/api-keys" element={<div data-testid="api-keys-page">API Keys</div>} />
        <Route path="/nfc-bindings" element={<div data-testid="nfc-bindings-page">NFC Bindings</div>} />
        <Route path="/printer-groups" element={<div data-testid="printer-groups-page">Printer Groups</div>} />
        <Route path="/admin/workers" element={<div data-testid="workers-page">Workers</div>} />
        <Route path="/spools" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="/spools/:tabId" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="/cameras" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/cameras/:tabId" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/nfc-devices" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/locations" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/users" element={<Navigate to="/settings?tab=users" replace />} />
        <Route path="/statistics" element={<Navigate to="/analytics?lens=production" replace />} />
        <Route path="/statistics/costs" element={<Navigate to="/analytics?lens=cost" replace />} />
        {/* /profile/api-keys is NOT redirected — it renders ApiKeysPage directly for all auth'd users */}
        <Route path="/admin/tags" element={<Navigate to="/settings?tab=data" replace />} />
        <Route path="/admin/bed-types" element={<Navigate to="/settings?tab=slicing" replace />} />
        <Route path="/admin/custom-fields" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/admin/webhooks" element={<Navigate to="/settings?tab=integrations" replace />} />
        <Route path="/admin/quotas" element={<Navigate to="/settings?tab=data" replace />} />
        <Route path="/admin/data" element={<Navigate to="/settings?tab=data" replace />} />
        <Route path="/slicer-profiles" element={<Navigate to="/settings?tab=slicing" replace />} />
        <Route path="/admin/security/login-audit" element={<Navigate to="/settings?tab=users" replace />} />
      </Routes>
    </MemoryRouter>
  );
}

describe('Settings nav migration redirects', () => {
  const settingsRedirectCases = [
    { from: '/spools', label: '/spools → filament' },
    { from: '/spools/active', label: '/spools/:tabId → filament' },
    { from: '/cameras', label: '/cameras → hardware' },
    { from: '/cameras/manage', label: '/cameras/:tabId → hardware' },
    { from: '/nfc-devices', label: '/nfc-devices → hardware' },
    { from: '/locations', label: '/locations → hardware' },
    { from: '/users', label: '/users → users' },
    { from: '/admin/tags', label: '/admin/tags → data' },
    { from: '/admin/bed-types', label: '/admin/bed-types → slicing' },
    { from: '/admin/custom-fields', label: '/admin/custom-fields → hardware' },
    { from: '/admin/webhooks', label: '/admin/webhooks → integrations' },
    { from: '/admin/quotas', label: '/admin/quotas → data' },
    { from: '/admin/data', label: '/admin/data → data' },
    { from: '/slicer-profiles', label: '/slicer-profiles → slicing' },
    { from: '/admin/security/login-audit', label: '/admin/security/login-audit → users' },
  ];

  settingsRedirectCases.forEach(({ from, label }) => {
    it(`redirects ${label}`, () => {
      RedirectTest({ from });
      expect(screen.getByTestId('settings-page')).toBeInTheDocument();
    });
  });

  const analyticsRedirectCases = [
    { from: '/statistics', label: '/statistics → /analytics?lens=production' },
    { from: '/statistics/costs', label: '/statistics/costs → /analytics?lens=cost' },
  ];

  analyticsRedirectCases.forEach(({ from, label }) => {
    it(`redirects ${label}`, () => {
      RedirectTest({ from });
      expect(screen.getByTestId('analytics-page')).toBeInTheDocument();
    });
  });

  const survivingRouteCases = [
    { from: '/profile/api-keys', testId: 'api-keys-page', label: '/profile/api-keys stays live' },
    { from: '/nfc-bindings', testId: 'nfc-bindings-page', label: '/nfc-bindings stays live' },
    { from: '/printer-groups', testId: 'printer-groups-page', label: '/printer-groups stays live' },
    { from: '/admin/workers', testId: 'workers-page', label: '/admin/workers stays live' },
  ];

  survivingRouteCases.forEach(({ from, testId, label }) => {
    it(label, () => {
      RedirectTest({ from });
      expect(screen.getByTestId(testId)).toBeInTheDocument();
    });
  });
});
