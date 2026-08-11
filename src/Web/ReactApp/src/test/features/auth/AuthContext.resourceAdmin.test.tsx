/**
 * Covers the resource:admin implication added by AuthContext.hasPermission for issue #1447:
 * a user holding only "{resource}:admin" must satisfy `hasPermission(resource, action)` for
 * every action on that resource, without leaking the grant to a different resource, and
 * without introducing any broader action hierarchy.
 */
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { UserDto } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getCurrentUser: vi.fn(),
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';

function PermissionProbe({ resource, action }: { resource: string; action: string }) {
  const { hasPermission, isLoading } = useAuth();
  if (isLoading) {
    return <span data-testid="loading">loading</span>;
  }
  return <span data-testid="probe-result">{String(hasPermission(resource, action))}</span>;
}

async function renderWithUser(permissions: string[], resource: string, action: string) {
  const user: UserDto = {
    id: 'user-1',
    username: 'alice',
    email: 'alice@example.com',
    roles: ['farm_user'],
    permissions,
  } as UserDto;
  vi.mocked(apiClient.getCurrentUser).mockResolvedValue(user);
  localStorage.setItem('auth-token', 'test-token');

  render(
    <AuthProvider>
      <PermissionProbe resource={resource} action={action} />
    </AuthProvider>,
  );

  await waitFor(() => expect(screen.queryByTestId('loading')).toBeNull());
  return screen.getByTestId('probe-result').textContent;
}

describe('AuthContext.hasPermission — resource:admin implication (#1447)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('a "{resource}:admin" grant satisfies a finer-grained action check on the same resource', async () => {
    const result = await renderWithUser(['calibration:admin'], 'calibration', 'read');
    expect(result).toBe('true');
  });

  it('a "{resource}:read" grant does not satisfy an admin check on the same resource', async () => {
    const result = await renderWithUser(['calibration:read'], 'calibration', 'admin');
    expect(result).toBe('false');
  });

  it('an admin grant on one resource does not leak to another resource', async () => {
    const result = await renderWithUser(['printers:admin'], 'queue', 'read');
    expect(result).toBe('false');
  });

  it('exact resource:action matches still succeed', async () => {
    const result = await renderWithUser(['calibration:read'], 'calibration', 'read');
    expect(result).toBe('true');
  });

  it('an unrelated permission on the same resource does not satisfy the check', async () => {
    const result = await renderWithUser(['calibration:update'], 'calibration', 'read');
    expect(result).toBe('false');
  });
});
