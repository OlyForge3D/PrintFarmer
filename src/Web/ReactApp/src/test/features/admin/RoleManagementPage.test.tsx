import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RoleManagementPage } from '@/features/admin/pages/RoleManagementPage';
import { apiClient } from '@/services/api';
import type {
  PermissionCatalog,
  RoleDetail,
  RolePermissions,
  RoleSummary,
  UpdateRolePermissionsResponse,
} from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getAdminRoles: vi.fn(),
    getAdminRole: vi.fn(),
    createAdminRole: vi.fn(),
    updateAdminRole: vi.fn(),
    deleteAdminRole: vi.fn(),
    getPermissionCatalog: vi.fn(),
    getRolePermissions: vi.fn(),
    updateRolePermissions: vi.fn(),
  },
}));

function apiError(statusCode: number, message: string, data?: unknown) {
  return { message, statusCode, data, isApiError: true } as unknown as Error & {
    statusCode: number;
    data?: unknown;
  };
}

const customRole: RoleSummary = {
  id: 'role-custom-1',
  name: 'shift_lead',
  displayName: 'Shift Lead',
  description: 'Runs a shift',
  isSystemRole: false,
  isActive: true,
  memberCount: 2,
  permissionCount: 3,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

const systemRole: RoleSummary = {
  id: 'role-admin',
  name: 'farm_admin',
  displayName: 'Farm Admin',
  description: 'Full access',
  isSystemRole: true,
  isActive: true,
  memberCount: 1,
  permissionCount: 10,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

const catalog: PermissionCatalog = {
  generatedAt: '2026-01-01T00:00:00Z',
  resources: [
    {
      resource: 'printers',
      displayName: 'Printers',
      permissions: [
        {
          resource: 'printers',
          action: 'view',
          permission: 'printers:view',
          actionDisplayName: 'View',
          impliedByAdmin: true,
          routes: [{ method: 'GET', template: '/api/printers' }],
        },
        {
          resource: 'printers',
          action: 'admin',
          permission: 'printers:admin',
          actionDisplayName: 'Admin',
          impliedByAdmin: false,
          routes: [{ method: 'DELETE', template: '/api/printers/{id}' }],
        },
      ],
    },
  ],
  orphanedCatalogEntries: [],
};

function buildRolePermissions(overrides: Partial<RolePermissions> = {}): RolePermissions {
  return {
    roleId: customRole.id,
    roleName: customRole.name,
    roleDisplayName: customRole.displayName,
    isSystemRole: false,
    isEditable: true,
    updatedAt: '2026-01-01T00:00:00Z',
    resources: [
      {
        resource: 'printers',
        displayName: 'Printers',
        permissions: [
          {
            resource: 'printers',
            action: 'view',
            permission: 'printers:view',
            actionDisplayName: 'View',
            impliedByAdmin: true,
            status: 'Granted',
          },
          {
            resource: 'printers',
            action: 'admin',
            permission: 'printers:admin',
            actionDisplayName: 'Admin',
            impliedByAdmin: false,
            status: 'Absent',
          },
        ],
      },
    ],
    ...overrides,
  };
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <RoleManagementPage />
    </QueryClientProvider>,
  );
}

describe('RoleManagementPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getAdminRoles).mockResolvedValue([customRole, systemRole]);
    vi.mocked(apiClient.getPermissionCatalog).mockResolvedValue(catalog);
    vi.mocked(apiClient.getRolePermissions).mockResolvedValue(buildRolePermissions());
  });

  it('shows the shared loading state before data resolves', () => {
    vi.mocked(apiClient.getAdminRoles).mockImplementation(() => new Promise(() => {}));
    vi.mocked(apiClient.getPermissionCatalog).mockImplementation(() => new Promise(() => {}));
    renderPage();
    expect(screen.getByRole('status', { name: 'Loading roles' })).toBeInTheDocument();
  });

  it('renders the role list once loaded', async () => {
    renderPage();
    expect(await screen.findByText('Shift Lead')).toBeInTheDocument();
    expect(screen.getByText('Farm Admin')).toBeInTheDocument();
    expect(screen.getByText('System')).toBeInTheDocument();
  });

  it('shows the shared error state and retries roles and the catalog', async () => {
    vi.mocked(apiClient.getAdminRoles).mockRejectedValueOnce(new Error('roles unavailable'));
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load roles");
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(apiClient.getAdminRoles).toHaveBeenCalledTimes(2);
    expect(apiClient.getPermissionCatalog).toHaveBeenCalledTimes(2);
  });

  it('creates a custom role with client-side name validation', async () => {
    const user = userEvent.setup();
    const created: RoleDetail = { ...customRole, id: 'role-new', name: 'new_role', permissions: [] };
    vi.mocked(apiClient.createAdminRole).mockResolvedValue(created);
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'New role' }));
    await user.type(await screen.findByLabelText(/^Name/), 'Not Valid!');
    await user.type(await screen.findByLabelText(/^Display name/), 'New Role');
    await user.click(screen.getByRole('button', { name: 'Create role' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/lowercase letters, numbers, and underscores/i);
    expect(apiClient.createAdminRole).not.toHaveBeenCalled();

    await user.clear(await screen.findByLabelText(/^Name/));
    await user.type(await screen.findByLabelText(/^Name/), 'new_role');
    await user.click(screen.getByRole('button', { name: 'Create role' }));

    await waitFor(() => expect(apiClient.createAdminRole).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'new_role', displayName: 'New Role' }),
    ));
  });

  it('rejects the reserved farm_ prefix client-side', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'New role' }));
    await user.type(await screen.findByLabelText(/^Name/), 'farm_custom');
    await user.type(await screen.findByLabelText(/^Display name/), 'Blocked');
    await user.click(screen.getByRole('button', { name: 'Create role' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/reserved for built-in system roles/i);
    expect(apiClient.createAdminRole).not.toHaveBeenCalled();
  });

  it('still validates the name pattern client-side when cloning from another role', async () => {
    const user = userEvent.setup();
    renderPage();

    await screen.findByText('Farm Admin');
    await user.click(screen.getByRole('button', { name: 'Clone Farm Admin' }));
    await user.type(await screen.findByLabelText(/^Name/), 'Not A Valid Slug!');
    await user.type(await screen.findByLabelText(/^Display name/), 'Cloned role');
    await user.click(screen.getByRole('button', { name: 'Create role' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/lowercase/i);
    expect(apiClient.createAdminRole).not.toHaveBeenCalled();
  });

  it('surfaces the server error message on create failure', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.createAdminRole).mockRejectedValue(apiError(400, 'Role name already exists.'));
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'New role' }));
    await user.type(await screen.findByLabelText(/^Name/), 'shift_lead_two');
    await user.type(await screen.findByLabelText(/^Display name/), 'Shift Lead Two');
    await user.click(screen.getByRole('button', { name: 'Create role' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Role name already exists.');
  });

  it('edits an existing custom role', async () => {
    const user = userEvent.setup();
    const updated: RoleDetail = { ...customRole, displayName: 'Shift Lead (updated)', permissions: [] };
    vi.mocked(apiClient.updateAdminRole).mockResolvedValue(updated);
    renderPage();

    await user.click(await screen.findByText('Shift Lead'));
    await user.click(screen.getByRole('button', { name: 'Edit Shift Lead' }));
    const displayNameInput = await screen.findByLabelText(/^Display name/);
    await user.clear(displayNameInput);
    await user.type(displayNameInput, 'Shift Lead (updated)');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(apiClient.updateAdminRole).toHaveBeenCalledWith(
      customRole.id,
      expect.objectContaining({ displayName: 'Shift Lead (updated)' }),
    ));
  });

  it('does not offer edit or delete actions for a system role', async () => {
    renderPage();
    await screen.findByText('Farm Admin');
    expect(screen.queryByRole('button', { name: 'Edit Farm Admin' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete Farm Admin' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clone Farm Admin' })).toBeInTheDocument();
  });

  it('deletes a role with no members directly', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.deleteAdminRole).mockResolvedValue(undefined);
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Delete Shift Lead' }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(apiClient.deleteAdminRole).toHaveBeenCalledWith(
      customRole.id,
      expect.objectContaining({}),
    ));
  });

  it('offers reassign-or-cascade when deleting a role that still has members', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.deleteAdminRole)
      .mockRejectedValueOnce(apiError(409, 'Role has members.', { error: 'Role has members.', memberCount: 2 }))
      .mockResolvedValueOnce(undefined);
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Delete Shift Lead' }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    expect(await screen.findByText('Role has members')).toBeInTheDocument();

    await user.click(screen.getByLabelText('Delete anyway and remove members from this role'));
    await user.click(screen.getByRole('button', { name: 'Delete role' }));

    await waitFor(() => expect(apiClient.deleteAdminRole).toHaveBeenLastCalledWith(
      customRole.id,
      expect.objectContaining({ cascade: true }),
    ));
  });

  it('toggles a permission, saves, and shows the sign-out confirmation copy', async () => {
    const user = userEvent.setup();
    const savedPermissions = buildRolePermissions({
      resources: [
        {
          resource: 'printers',
          displayName: 'Printers',
          permissions: [
            {
              resource: 'printers', action: 'view', permission: 'printers:view',
              actionDisplayName: 'View', impliedByAdmin: true, status: 'Granted',
            },
            {
              resource: 'printers', action: 'admin', permission: 'printers:admin',
              actionDisplayName: 'Admin', impliedByAdmin: false, status: 'Granted',
            },
          ],
        },
      ],
    });
    const response: UpdateRolePermissionsResponse = { role: savedPermissions, revokedSessionCount: 2 };
    vi.mocked(apiClient.updateRolePermissions).mockResolvedValue(response);

    renderPage();
    await user.click(await screen.findByText('Shift Lead'));

    const adminToggle = await screen.findByLabelText(/^Grant Admin/);
    await user.click(adminToggle);

    const saveBar = screen.getByTestId('admin-save-bar');
    expect(saveBar).toBeInTheDocument();
    await user.click(within(saveBar).getByRole('button', { name: 'Save changes' }));

    const confirmDialog = await screen.findByRole('dialog', { name: 'Save permission changes' });
    expect(within(confirmDialog).getByText(/currently has 2 member\(s\)/i)).toBeInTheDocument();
    await user.click(within(confirmDialog).getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(apiClient.updateRolePermissions).toHaveBeenCalledWith(
      customRole.id,
      expect.objectContaining({ permissions: expect.arrayContaining(['printers:view', 'printers:admin']) }),
    ));
  });

  it('shows a reload-and-retry banner on a 409 concurrency conflict', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.updateRolePermissions).mockRejectedValue(apiError(409, 'Stale update.'));
    renderPage();

    await user.click(await screen.findByText('Shift Lead'));
    await user.click(await screen.findByLabelText(/^Grant Admin/));

    const saveBar = screen.getByTestId('admin-save-bar');
    await user.click(within(saveBar).getByRole('button', { name: 'Save changes' }));
    const confirmDialog = await screen.findByRole('dialog', { name: 'Save permission changes' });
    await user.click(within(confirmDialog).getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Stale update.');
    await user.click(screen.getByRole('button', { name: 'Reload latest' }));

    await waitFor(() => expect(apiClient.getRolePermissions).toHaveBeenCalledTimes(2));
  });

  it('distinguishes a 409 lockout violation from a concurrency conflict on permission save', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.updateRolePermissions).mockRejectedValue(apiError(
      409,
      'This change would remove the last active role holding a required administrative permission.',
      { error: 'This change would remove the last active role holding a required administrative permission.', permissions: ['printers:admin'] },
    ));
    renderPage();

    await user.click(await screen.findByText('Shift Lead'));
    await user.click(await screen.findByLabelText(/^Grant Admin/));

    const saveBar = screen.getByTestId('admin-save-bar');
    await user.click(within(saveBar).getByRole('button', { name: 'Save changes' }));
    const confirmDialog = await screen.findByRole('dialog', { name: 'Save permission changes' });
    await user.click(within(confirmDialog).getByRole('button', { name: 'Save changes' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/last active role holding a required administrative permission/i);
    expect(alert).toHaveTextContent('printers:admin');
    expect(screen.queryByRole('button', { name: 'Reload latest' })).not.toBeInTheDocument();
  });

  it('confirms before discarding unsaved permission edits when switching roles', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.getRolePermissions).mockImplementation((roleId: string) =>
      Promise.resolve(
        roleId === systemRole.id
          ? buildRolePermissions({
              roleId: systemRole.id,
              roleName: systemRole.name,
              roleDisplayName: systemRole.displayName,
              isSystemRole: true,
              isEditable: false,
            })
          : buildRolePermissions(),
      ));
    renderPage();

    await user.click(await screen.findByText('Shift Lead'));
    await user.click(await screen.findByLabelText(/^Grant Admin/));

    await user.click(await screen.findByText('Farm Admin'));

    const confirmDialog = await screen.findByRole('dialog', { name: 'Discard unsaved permission changes?' });
    expect(within(confirmDialog).getByText('You have unsaved permission changes for this role. Switching roles will discard them.')).toBeInTheDocument();

    // Cancelling keeps the original role selected and the toggle still checked.
    await user.click(within(confirmDialog).getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByLabelText(/^Grant Admin/)).toBeChecked();

    await user.click(await screen.findByText('Farm Admin'));
    const confirmDialog2 = await screen.findByRole('dialog', { name: 'Discard unsaved permission changes?' });
    await user.click(within(confirmDialog2).getByRole('button', { name: 'Discard changes' }));

    await screen.findByText(/implicit total access/i);
  });

  it('renders the farm_admin permission pane as fully read-only', async () => {
    vi.mocked(apiClient.getRolePermissions).mockResolvedValue(
      buildRolePermissions({
        roleId: systemRole.id,
        roleName: systemRole.name,
        roleDisplayName: systemRole.displayName,
        isSystemRole: true,
        isEditable: false,
      }),
    );
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByText('Farm Admin'));

    expect(await screen.findByText(/implicit total access/i)).toBeInTheDocument();
    const toggle = screen.getByLabelText(/^Grant View/) as HTMLInputElement;
    expect(toggle).toBeDisabled();
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
  });

  it('warns that saving will clear any explicitly denied permissions', async () => {
    vi.mocked(apiClient.getRolePermissions).mockResolvedValue(
      buildRolePermissions({
        resources: [
          {
            resource: 'printers',
            displayName: 'Printers',
            permissions: [
              {
                resource: 'printers', action: 'view', permission: 'printers:view',
                actionDisplayName: 'View', impliedByAdmin: true, status: 'Denied',
              },
              {
                resource: 'printers', action: 'admin', permission: 'printers:admin',
                actionDisplayName: 'Admin', impliedByAdmin: false, status: 'Absent',
              },
            ],
          },
        ],
      }),
    );
    renderPage();

    await userEvent.setup().click(await screen.findByText('Shift Lead'));

    expect(await screen.findByText(/explicitly denied for this role/i)).toBeInTheDocument();
  });
});
