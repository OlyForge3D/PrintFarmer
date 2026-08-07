import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { apiClient } from '@/services/api';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasRole: (role: string) => role === 'farm_admin' }),
}));

vi.mock('@/common/hooks/usePasswordPolicy', () => ({
  usePasswordPolicy: () => ({ data: undefined }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getUsers: vi.fn(),
    getRoles: vi.fn(),
    checkUserAvailability: vi.fn(),
    createUser: vi.fn(),
    updateUser: vi.fn(),
    adminChangeUserPassword: vi.fn(),
    deleteUser: vi.fn(),
  },
}));

const testUser = {
  id: 'user-1',
  username: 'operator',
  email: 'operator@example.com',
  firstName: 'Print',
  lastName: 'Operator',
  isActive: true,
  emailConfirmed: true,
  createdAt: '2026-01-01T00:00:00Z',
  roles: ['farm_user'],
  permissions: ['printers'],
};

describe('UserManagementPage shared admin patterns', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getUsers).mockResolvedValue([]);
    vi.mocked(apiClient.getRoles).mockResolvedValue([
      {
        id: 'role-1',
        name: 'farm_user',
        displayName: 'Farm User',
        description: 'Standard user',
      },
    ]);
  });

  it('uses the shared loading state', () => {
    vi.mocked(apiClient.getUsers).mockImplementation(() => new Promise(() => {}));
    vi.mocked(apiClient.getRoles).mockImplementation(() => new Promise(() => {}));
    render(<UserManagementPage />);
    expect(screen.getByRole('status', { name: 'Loading users' })).toBeInTheDocument();
  });

  it('uses the shared empty state', async () => {
    render(<UserManagementPage />);
    expect(await screen.findByText('No users found')).toBeInTheDocument();
  });

  it('uses the shared error state and retries both users and roles', async () => {
    vi.mocked(apiClient.getUsers).mockRejectedValueOnce(new Error('users unavailable'));
    const user = userEvent.setup();
    render(<UserManagementPage />);

    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load users");
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(apiClient.getUsers).toHaveBeenCalledTimes(2);
    expect(apiClient.getRoles).toHaveBeenCalledTimes(2);
  });

  it('shows and discards the shared save bar for a dirty create form', async () => {
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByRole('button', { name: 'Add User' }));
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();

    await user.type(screen.getByPlaceholderText('Enter username'), 'operator');
    expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('saves a dirty profile and clears its pristine baseline', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    vi.mocked(apiClient.updateUser).mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Edit user'));
    const firstName = screen.getByPlaceholderText('First Name');
    await user.clear(firstName);
    await user.type(firstName, 'Farm');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(apiClient.updateUser).toHaveBeenCalledWith(
      'user-1',
      expect.objectContaining({ firstName: 'Farm' }),
    ));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
  });

  it('saves permissions through an independent dirty-state path', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    vi.mocked(apiClient.updateUser).mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Manage permissions'));
    await user.click(screen.getByRole('checkbox', { name: /Files/ }));
    await user.click(screen.getByRole('button', { name: 'Save permissions' }));

    await waitFor(() => expect(apiClient.updateUser).toHaveBeenCalledWith(
      'user-1',
      { accessibleAreas: ['printers', 'files'] },
    ));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('marks the password form pristine after a confirmed password change', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    vi.mocked(apiClient.adminChangeUserPassword).mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Change password'));
    await user.type(screen.getByPlaceholderText('Enter new password'), 'NewPassword1!');
    await user.type(screen.getByPlaceholderText('Confirm new password'), 'NewPassword1!');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Change Password' }));

    await waitFor(() => expect(apiClient.adminChangeUserPassword).toHaveBeenCalledWith(
      'user-1',
      'NewPassword1!',
      'NewPassword1!',
    ));
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
  });
});
