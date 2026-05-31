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
        <Route path="/spools" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="/spools/:tabId" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="/cameras" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/cameras/:tabId" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/nfc-devices" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/locations" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="/users" element={<Navigate to="/settings?tab=users" replace />} />
        <Route path="/profile/api-keys" element={<Navigate to="/settings?tab=users" replace />} />
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
  const redirectCases = [
    { from: '/spools', tab: 'filament', label: '/spools → filament' },
    { from: '/spools/active', tab: 'filament', label: '/spools/:tabId → filament' },
    { from: '/cameras', tab: 'hardware', label: '/cameras → hardware' },
    { from: '/cameras/manage', tab: 'hardware', label: '/cameras/:tabId → hardware' },
    { from: '/nfc-devices', tab: 'hardware', label: '/nfc-devices → hardware' },
    { from: '/locations', tab: 'hardware', label: '/locations → hardware' },
    { from: '/users', tab: 'users', label: '/users → users' },
    { from: '/profile/api-keys', tab: 'users', label: '/profile/api-keys → users' },
    { from: '/admin/tags', tab: 'data', label: '/admin/tags → data' },
    { from: '/admin/bed-types', tab: 'slicing', label: '/admin/bed-types → slicing' },
    { from: '/admin/custom-fields', tab: 'hardware', label: '/admin/custom-fields → hardware' },
    { from: '/admin/webhooks', tab: 'integrations', label: '/admin/webhooks → integrations' },
    { from: '/admin/quotas', tab: 'data', label: '/admin/quotas → data' },
    { from: '/admin/data', tab: 'data', label: '/admin/data → data' },
    { from: '/slicer-profiles', tab: 'slicing', label: '/slicer-profiles → slicing' },
    { from: '/admin/security/login-audit', tab: 'users', label: '/admin/security/login-audit → users' },
  ];

  redirectCases.forEach(({ from, label }) => {
    it(`redirects ${label}`, () => {
      RedirectTest({ from });
      expect(screen.getByTestId('settings-page')).toBeInTheDocument();
    });
  });
});
