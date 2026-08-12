import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { apiClient } from '@/services/api';

let hasUsersAdmin = true;

vi.mock('sonner', () => ({
  toast: { info: vi.fn(), error: vi.fn(), success: vi.fn(), warning: vi.fn() },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  // #1457: UserManagementPage now gates on hasPermission('users', 'admin').
  useAuth: () => ({
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: (resource: string, action: string) => hasUsersAdmin && resource === 'users' && action === 'admin',
  }),
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

const farmUserRole = {
  id: 'role-1',
  name: 'farm_user',
  displayName: 'Farm User',
  description: 'Standard user',
  isSystemRole: true,
  isActive: true,
  permissions: [
    { resource: 'printers', action: 'view', granted: true },
  ],
};

const farmAdminRole = {
  id: 'role-2',
  name: 'farm_admin',
  displayName: 'Farm Admin',
  description: 'Administrator',
  isSystemRole: true,
  isActive: true,
  permissions: [
    { resource: 'roles', action: 'admin', granted: true },
    { resource: 'users', action: 'admin', granted: true },
  ],
};

function createDeferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

describe('UserManagementPage shared admin patterns', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hasUsersAdmin = true;
    vi.mocked(apiClient.getUsers).mockResolvedValue([]);
    vi.mocked(apiClient.getRoles).mockResolvedValue([farmUserRole]);
  });

  it('denies access and shows AdminError when the user lacks users:admin', () => {
    hasUsersAdmin = false;
    render(<UserManagementPage />);
    expect(screen.getByText('Access denied')).toBeInTheDocument();
    expect(screen.getByText('You need administrator privileges to access user management.')).toBeInTheDocument();
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

  it('does not submit a username known to be unavailable', async () => {
    vi.mocked(apiClient.checkUserAvailability).mockResolvedValue({
      usernameExists: true,
      emailExists: false,
    });
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByRole('button', { name: 'Add User' }));
    await user.type(screen.getByPlaceholderText('Enter username'), 'operator');
    await user.type(screen.getByPlaceholderText('Enter email address'), 'operator@example.com');
    await user.type(screen.getByPlaceholderText('Enter password'), 'NewPassword1!');
    expect(await screen.findByText('✗ Already taken')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Create user' }));
    expect(apiClient.createUser).not.toHaveBeenCalled();
  });

  it('saves a dirty profile and clears its pristine baseline', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    const update = createDeferred();
    vi.mocked(apiClient.updateUser).mockReturnValue(update.promise);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Edit user'));
    const firstName = screen.getByPlaceholderText('First Name');
    await user.clear(firstName);
    await user.type(firstName, 'Farm');
    const saveButton = screen.getByRole('button', { name: 'Save changes' });
    await user.click(saveButton);
    await user.click(saveButton);

    expect(apiClient.updateUser).toHaveBeenCalledTimes(1);
    expect(apiClient.updateUser).toHaveBeenCalledWith(
      'user-1',
      expect.objectContaining({ firstName: 'Farm' }),
    );
    update.resolve();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
  });

  it('assigns multiple roles via checkboxes and saves them as roleIds (not role names)', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    vi.mocked(apiClient.getRoles).mockResolvedValue([farmUserRole, farmAdminRole]);
    const update = createDeferred();
    vi.mocked(apiClient.updateUser).mockReturnValue(update.promise);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Edit user'));
    // The user already holds Farm User; add Farm Admin so they hold two roles at once.
    await user.click(screen.getByRole('checkbox', { name: /Farm Admin/ }));
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(apiClient.updateUser).toHaveBeenCalledWith(
      'user-1',
      expect.objectContaining({ roleIds: expect.arrayContaining(['role-1', 'role-2']) }),
    );
    const [, payload] = vi.mocked(apiClient.updateUser).mock.calls[0];
    expect((payload as { roleIds: string[] }).roleIds).toHaveLength(2);
    expect(payload).not.toHaveProperty('roles');
    update.resolve();
  });

  it('states that role changes revoke sessions before the user saves', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Edit user'));
    expect(screen.getByText(/immediately revokes this user's active sessions/i)).toBeInTheDocument();
  });

  it('refuses to save and does not call the API when a held role cannot be resolved to an id', async () => {
    const { toast } = await import('sonner');
    const userWithStaleRole = { ...testUser, roles: ['farm_user', 'retired_role'] };
    vi.mocked(apiClient.getUsers).mockResolvedValue([userWithStaleRole]);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Edit user'));
    const firstName = screen.getByPlaceholderText('First Name');
    await user.type(firstName, 'X');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(apiClient.updateUser).not.toHaveBeenCalled();
    expect(vi.mocked(toast.error).mock.calls[0][0]).toContain('retired_role');
  });

  it('shows the specific server reason when a role change is refused (e.g. last-admin lockout)', async () => {
    const { toast } = await import('sonner');
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    vi.mocked(apiClient.updateUser).mockRejectedValue({
      response: { data: { error: 'Cannot remove the last administrative role from this account.', code: 'LastAdminRole' } },
    });
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Edit user'));
    const firstName = screen.getByPlaceholderText('First Name');
    await user.type(firstName, 'X');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(vi.mocked(toast.error).mock.calls[0][0]).toBe(
      'Cannot remove the last administrative role from this account.',
    );
  });

  it('saves permissions through an independent dirty-state path', async () => {
    vi.mocked(apiClient.getUsers).mockResolvedValue([testUser]);
    const update = createDeferred();
    vi.mocked(apiClient.updateUser).mockReturnValue(update.promise);
    const user = userEvent.setup();
    render(<UserManagementPage />);

    await user.click(await screen.findByTitle('Manage permissions'));
    await user.click(screen.getByRole('checkbox', { name: /Files/ }));
    const saveButton = screen.getByRole('button', { name: 'Save permissions' });
    await user.click(saveButton);
    await user.click(saveButton);

    expect(apiClient.updateUser).toHaveBeenCalledTimes(1);
    expect(apiClient.updateUser).toHaveBeenCalledWith(
      'user-1',
      { accessibleAreas: ['printers', 'files'] },
    );
    update.resolve();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
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
