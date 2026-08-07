import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MemoryRouter, Navigate, Outlet, Route, Routes, useLocation } from 'react-router';

function LocationEcho({ testId }: { testId: string }) {
  const location = useLocation();
  return <div data-testid={testId}>{`${location.pathname}${location.search}`}</div>;
}

function renderCanonicalRoute(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/settings" element={<LocationEcho testId="settings-location" />} />
        <Route path="/admin/settings" element={<LocationEcho testId="settings-location" />} />
        <Route path="/admin/manage" element={<LocationEcho testId="settings-location" />} />
        <Route path="/locations" element={<Outlet />}>
          <Route index element={<Navigate to="dashboard" replace />} />
          <Route path="dashboard" element={<LocationEcho testId="locations-location" />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

describe('canonical settings navigation', () => {
  it.each([
    '/settings',
    '/admin/settings?tab=slicing&sub=profiles',
    '/admin/manage?tab=operations&sub=workers&workerTab=jobs',
  ])('keeps the canonical destination %s', (destination) => {
    renderCanonicalRoute(destination);
    expect(screen.getByTestId('settings-location')).toHaveTextContent(destination);
  });

  it('uses the locations index route for the dashboard default', () => {
    renderCanonicalRoute('/locations');
    expect(screen.getByTestId('locations-location')).toHaveTextContent('/locations/dashboard');
  });
});
