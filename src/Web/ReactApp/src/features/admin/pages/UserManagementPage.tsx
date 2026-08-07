import React, { useState, useEffect, useEffectEvent } from 'react';
import { usePasswordPolicy } from '@/common/hooks/usePasswordPolicy';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import {
  AdminEmpty,
  AdminError,
  AdminLoading,
  AdminSaveBar,
  adminToast,
  useDirtyState,
} from '@/common/components/admin';
import {
  Plus,
  Shield,
  Users,
  UserCheck,
  UserX,
} from 'lucide-react';
import { apiClient } from '@/services/api';
import { DeleteIcon, SearchIcon, EditIcon, LockIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input, Select, FormField, Alert, Checkbox } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { User, Role } from '@/types/admin';

const APPLICATION_AREAS = [
  { id: 'printers', name: 'Printers', description: 'View and manage printer configurations' },
  { id: 'files', name: 'Files', description: 'Access harvested G-code files' },
  { id: 'harvest', name: 'Harvest', description: 'Use the harvester interface' },
  { id: 'jobs', name: 'Jobs', description: 'View and manage print jobs' },
  { id: 'catalog', name: 'Catalog', description: 'Access manufacturer and model catalog' },
  { id: 'settings', name: 'Settings', description: 'Modify account and application settings' },
  { id: 'spools', name: 'Spools', description: 'Manage filament spools inventory' },
] as const;

const EMPTY_NEW_USER = {
  username: '',
  email: '',
  password: '',
  firstName: '',
  lastName: '',
};

const EMPTY_PASSWORD_FORM = {
  newPassword: '',
  confirmNewPassword: '',
};

export function UserManagementPage({ embedded = false }: EmbeddablePageProps) {
  const { hasRole } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [searchTerm, setSearchTerm] = useState('');
  const editForm = useDirtyState<{ user: User | null }>({ user: null });
  const permissionForm = useDirtyState({ permissions: [] as string[] });
  const selectedUser = editForm.values.user;
  const [showCreateModal, setShowCreateModal] = useState(false);
  const [showEditModal, setShowEditModal] = useState(false);
  const [showPermissionsModal, setShowPermissionsModal] = useState(false);
  const [isSavingUser, setIsSavingUser] = useState(false);
  const [isSavingPermissions, setIsSavingPermissions] = useState(false);
  const [userToDelete, setUserToDelete] = useState<User | null>(null);
  const [userToChangePassword, setUserToChangePassword] = useState<User | null>(null);
  const [showChangePasswordModal, setShowChangePasswordModal] = useState(false);
  const [showChangePasswordConfirm, setShowChangePasswordConfirm] = useState(false);
  const [changePasswordError, setChangePasswordError] = useState<string | null>(null);
  const [isChangingPassword, setIsChangingPassword] = useState(false);
  const passwordForm = useDirtyState(EMPTY_PASSWORD_FORM);
  const passwordChangeForm = passwordForm.values;
  const { data: passwordPolicy } = usePasswordPolicy();
  const createForm = useDirtyState({
    user: EMPTY_NEW_USER,
    roleId: '',
    permissions: [] as string[],
  });
  const newUser = createForm.values.user;
  const selectedRoleId = createForm.values.roleId;
  const selectedPermissions = createForm.values.permissions;
  type AvailabilityStatus = 'idle' | 'checking' | 'available' | 'taken' | 'error';
  const [usernameStatus, setUsernameStatus] = useState<AvailabilityStatus>('idle');
  const [emailStatus, setEmailStatus] = useState<AvailabilityStatus>('idle');
  const [, setAvailabilityMessage] = useState('');
  const [createErrors, setCreateErrors] = useState<Record<string, string>>({});
  const [isCreating, setIsCreating] = useState(false);
  const DEBOUNCE_MS = 450;

  // Helper: Check if a role is admin role
  const isAdminRole = (roleName: string | undefined) => roleName === 'farm_admin';

  const passwordMeetsPolicyValue = (password: string) => {
    if (!passwordPolicy) return true; // don't block while loading
    const p = password;
    if (p.length < passwordPolicy.minLength) return false;
    if (passwordPolicy.requireUppercase && !/[A-Z]/.test(p)) return false;
    if (passwordPolicy.requireLowercase && !/[a-z]/.test(p)) return false;
    if (passwordPolicy.requireDigit && !/[0-9]/.test(p)) return false;
    if (passwordPolicy.requireSymbol && !/[^A-Za-z0-9]/.test(p)) return false;
    return true;
  };

  const passwordMeetsPolicy = () => passwordMeetsPolicyValue(newUser.password);

  // Batched debounced availability checks (single request for username + email)
  useEffect(() => {
    if (!showCreateModal) return;

    const username = newUser.username.trim();
    const email = newUser.email.trim();

    // If both empty, reset statuses
    if (!username && !email) {
      setUsernameStatus('idle');
      setEmailStatus('idle');
      return;
    }

    // Mark only the fields that have a value as checking
    if (username) setUsernameStatus('checking'); else setUsernameStatus('idle');
    if (email) setEmailStatus('checking'); else setEmailStatus('idle');

    const ctrl = new AbortController();
    const handle = setTimeout(async () => {
      try {
        const data = await apiClient.checkUserAvailability(username, email);

        if (username) {
          const uTaken = data.usernameExists === true;
          setUsernameStatus(uTaken ? 'taken' : 'available');
        }
        if (email) {
          const eTaken = data.emailExists === true;
          setEmailStatus(eTaken ? 'taken' : 'available');
        }
      } catch {
        if (username) setUsernameStatus('error');
        if (email) setEmailStatus('error');
        setAvailabilityMessage('Could not verify availability');
      }
    }, DEBOUNCE_MS);

    return () => {
      clearTimeout(handle);
      ctrl.abort();
    };
  }, [newUser.username, newUser.email, showCreateModal]);

  const validateForm = () => {
    const errs: CreateUserFormState['errors'] = {};
    if (!newUser.username.trim()) errs.username = 'Username is required';
    if (!newUser.email.trim()) errs.email = 'Email is required';
    else if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(newUser.email.trim())) errs.email = 'Invalid email format';
    if (!newUser.password) errs.password = 'Password is required';
    else if (!passwordMeetsPolicy()) errs.password = 'Password does not meet policy';
    return errs;
  };

  const createUser = async () => {
    if (isCreating) return;
    const fieldErrs = validateForm();
    if (Object.keys(fieldErrs).length > 0) {
      setCreateErrors(fieldErrs);
      return;
    }
    setCreateErrors({});
    setIsCreating(true);

    try {
      await apiClient.createUser({
        username: newUser.username.trim(),
        email: newUser.email.trim(),
        password: newUser.password,
        firstName: newUser.firstName.trim() || undefined,
        lastName: newUser.lastName.trim() || undefined,
        roleIds: selectedRoleId ? [selectedRoleId] : [],
        accessibleAreas: selectedPermissions
      });

      // We could optimistically insert but reloading ensures roles & computed fields
      await loadUsers();
      adminToast.success('User created');
      createForm.markPristine({
        user: EMPTY_NEW_USER,
        roleId: '',
        permissions: [],
      });
      setShowCreateModal(false);
    } catch (err) {
      const error = err as { response?: { data?: Record<string, unknown> } };
      let errorMessage = 'Failed to create user';

      // Handle apiClient errors
      if (error.response?.data) {
        const data = error.response.data as Record<string, unknown>;
        errorMessage = (data.error || data.message || data.title || errorMessage) as string;
        }

        adminToast.error(errorMessage);
    } finally {
      setIsCreating(false);
    }
  };

  const openCreateUser = () => {
    const farmUserRole = roles.find(r => r.name === 'farm_user');
    createForm.markPristine({
      user: EMPTY_NEW_USER,
      roleId: farmUserRole ? farmUserRole.id : '',
      permissions: [],
    });
    setShowCreateModal(true);
  };

  const openEditUser = (user: User) => {
    editForm.markPristine({ user });
    setShowEditModal(true);
  };

  const openPermissions = (user: User) => {
    editForm.markPristine({ user });
    permissionForm.markPristine({ permissions: user.permissions });
    setShowPermissionsModal(true);
  };

  const updateSelectedUser = (update: (user: User) => User) => {
    if (selectedUser) {
      editForm.setValue('user', update(selectedUser));
    }
  };

  const saveSelectedUser = async () => {
    if (!selectedUser || isSavingUser) return;
    setIsSavingUser(true);
    try {
      await apiClient.updateUser(selectedUser.id, {
        firstName: selectedUser.firstName,
        lastName: selectedUser.lastName,
        email: selectedUser.email,
        isActive: selectedUser.isActive,
        roles: selectedUser.roles,
        permissions: selectedUser.permissions,
      });
      editForm.markPristine({ user: selectedUser });
      adminToast.success('User updated successfully');
      setUsers(users => users.map(user => user.id === selectedUser.id ? { ...user, ...selectedUser } : user));
      setShowEditModal(false);
    } catch (err) {
      const error = err as { response?: { data?: Record<string, unknown> } };
      const message = (error.response?.data as Record<string, unknown> | undefined)?.message as string || 'Failed to update user';
      adminToast.error(message);
    } finally {
      setIsSavingUser(false);
    }
  };

  const savePermissions = async () => {
    if (!selectedUser || isSavingPermissions) return;
    setIsSavingPermissions(true);
    try {
      const permissions = permissionForm.values.permissions;
      await apiClient.updateUser(selectedUser.id, {
        accessibleAreas: permissions,
      });
      permissionForm.markPristine({ permissions });
      const nextUser = { ...selectedUser, permissions };
      const nextOriginalUser = editForm.original.user
        ? { ...editForm.original.user, permissions }
        : nextUser;
      editForm.markPristine({ user: nextOriginalUser });
      editForm.replaceValues({ user: nextUser });
      adminToast.success('Permissions updated');
      setShowPermissionsModal(false);
      await loadUsers();
    } catch (err) {
      const error = err as { response?: { data?: Record<string, unknown> } };
      const message = (error.response?.data as Record<string, unknown> | undefined)?.message as string || 'Failed to update permissions';
      adminToast.error(message);
    } finally {
      setIsSavingPermissions(false);
    }
  };

  const loadUsers = async () => {
    try {
      setLoadError(null);
      const data = await apiClient.getUsers();
      setUsers((data as unknown) as User[]);
    } catch (error) {
      setLoadError(error);
    } finally {
      setLoading(false);
    }
  };

  const loadData = async () => {
    try {
      setLoadError(null);
      const [usersData, rolesData] = await Promise.all([
        apiClient.getUsers(),
        apiClient.getRoles(),
      ]);
      setUsers((usersData as unknown) as User[]);
      setRoles((rolesData as unknown) as Role[]);
    } catch (error) {
      setLoadError(error);
    } finally {
      setLoading(false);
    }
  };

  // Extract keyboard handler with useEffectEvent to access latest state without retriggers
  const handleKeyDown = useEffectEvent((e: KeyboardEvent) => {
    if (e.key === 'k' && !['input', 'textarea'].includes((e.target as HTMLElement).tagName.toLowerCase())) {
      e.preventDefault();
      openCreateUser();
    }
  });

  // Load users and roles on mount
  useEffect(() => {
    void loadData();
  }, []);

  // Keyboard shortcut: 'k' to create new user
  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
   
  }, []);

  const filteredUsers = users.filter(user =>
    user.username.toLowerCase().includes(searchTerm.toLowerCase()) ||
    user.email.toLowerCase().includes(searchTerm.toLowerCase()) ||
    (user.firstName && user.firstName.toLowerCase().includes(searchTerm.toLowerCase())) ||
    (user.lastName && user.lastName.toLowerCase().includes(searchTerm.toLowerCase()))
  );

  const formatDate = (dateString: string) => {
    return new Date(dateString).toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  };

  const getRoleBadgeColor = (roleName: string) => {
    switch (roleName) {
      case 'farm_admin':
        return 'bg-pf-error/10 text-pf-error border-pf-error/30';
      case 'farm_user':
        return 'bg-pf-accent-bg/15 text-pf-accent border-pf-accent/30';
      default:
        return 'bg-pf-bg-1 text-pf-text-primary border-pf-border';
    }
  };

  // Early access check AFTER hooks to avoid conditional hook usage
  if (!hasRole('farm_admin')) {
    return (
      <PageTemplate
        title="User Management"
        subtitle="Manage user accounts, roles, and permissions for PrintFarmer."
        icon={Users}
        embedded={embedded}
      >
        <AdminError
          title="Access denied"
          description="You need administrator privileges to access user management."
        />
      </PageTemplate>
    );
  }

  if (loading) {
    return (
      <PageTemplate
        title="User Management"
        subtitle="Manage user accounts, roles, and permissions for PrintFarmer."
        icon={Users}
        embedded={embedded}
      >
        <AdminLoading variant="table" rows={6} cols={6} label="Loading users" />
      </PageTemplate>
    );
  }

  if (loadError) {
    return (
      <PageTemplate
        title="User Management"
        subtitle="Manage user accounts, roles, and permissions for PrintFarmer."
        icon={Users}
        embedded={embedded}
      >
        <AdminError
          title="Couldn't load users"
          description="Try loading user management data again."
          error={loadError}
          onRetry={() => void loadData()}
        />
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="User Management"
      subtitle="Manage user accounts, roles, and permissions for PrintFarmer."
      icon={Users}
      actions={
        <Button
          variant="primary"
          onClick={openCreateUser}
          className="flex items-center gap-2"
        >
          <Plus className="h-4 w-4" />
          <span>Add User</span>
        </Button>
      }
      embedded={embedded}
    >
      {/* Controls */}
      <div className="mb-6 flex flex-col sm:flex-row gap-4 justify-between">
        <div className="relative flex-1 max-w-xs">
          <Input
            type="text"
            placeholder="Search users..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="pl-10"
          />
          <SearchIcon className="h-4 w-4 absolute left-3 top-1/2 transform -translate-y-1/2 text-pf-text-tertiary" />
        </div>
      </div>
      {/* Users Table */}
      <div className="card">
        <div className="overflow-x-auto">
          <table>
            <thead>
              <tr>
                <th>User</th>
                <th>Roles</th>
                <th>Status</th>
                <th>Last Login</th>
                <th>Created</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {filteredUsers.map((user) => (
                <tr key={user.id} className="hover:bg-pf-bg-2">
                  <td className="px-6 py-4">
                    <div>
                      <div className="text-sm font-medium text-pf-text-primary">
                        {user.firstName && user.lastName
                          ? `${user.firstName} ${user.lastName}`
                          : user.username}
                      </div>
                      <div className="text-sm text-pf-text-secondary">
                        {user.username} • {user.email}
                      </div>
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex flex-wrap gap-1">
                      {user.roles.map((role) => (
                        <span
                          key={role}
                          className={`inline-flex px-2 py-1 text-xs rounded-xs border ${getRoleBadgeColor(role)}`}
                        >
                          {roles.find(r => r.name === role)?.displayName || role}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex items-center">
                      {user.isActive ? (
                        <>
                          <UserCheck className="h-4 w-4 text-pf-success mr-2" />
                          <span className="text-sm text-pf-success">Active</span>
                        </>
                      ) : (
                        <>
                          <UserX className="h-4 w-4 text-pf-error mr-2" />
                          <span className="text-sm text-pf-error">Inactive</span>
                        </>
                      )}
                    </div>
                  </td>
                  <td className="px-6 py-4 text-sm text-pf-text-secondary">
                    {user.lastLogin ? formatDate(user.lastLogin) : 'Never'}
                  </td>
                  <td className="px-6 py-4 text-sm text-pf-text-secondary">
                    {formatDate(user.createdAt)}
                  </td>
                  <td className="px-6 py-4 text-right">
                    <div className="flex items-center justify-end space-x-2">
                      <Button
                        type="button"
                        variant="subtle"
                        size="sm"
                        onClick={() => {
                          setUserToChangePassword(user);
                          passwordForm.markPristine(EMPTY_PASSWORD_FORM);
                          setChangePasswordError(null);
                          setShowChangePasswordModal(true);
                        }}
                        className="!p-2 !h-auto"
                        title="Change password"
                      >
                        <LockIcon className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="subtle"
                        size="sm"
                        onClick={() => openPermissions(user)}
                        className="!p-2 !h-auto"
                        title="Manage permissions"
                      >
                        <Shield className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="subtle"
                        size="sm"
                        onClick={() => openEditUser(user)}
                        className="!p-2 !h-auto"
                        title="Edit user"
                      >
                        <EditIcon className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="subtle"
                        size="sm"
                        onClick={() => setUserToDelete(user)}
                        className="!p-2 !h-auto hover:text-pf-error-text"
                        title="Delete user"
                      >
                        <DeleteIcon className="h-4 w-4" />
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {filteredUsers.length === 0 && (
          <AdminEmpty
            icon={<Users className="h-12 w-12" />}
            title={searchTerm ? 'No matching users' : 'No users found'}
            description={searchTerm
              ? 'Try a different username, email, or name.'
              : 'Create a user account to grant access to PrintFarmer.'}
            size="compact"
          />
        )}
      </div>

      {/* User count and modals section */}
      <div>
        {/* User count */}
        <div className="mt-4 text-sm text-pf-text-secondary">
          Showing {filteredUsers.length} of {users.length} users
        </div>

        {showCreateModal && (
          <Modal
            isOpen={showCreateModal}
            onClose={() => {
              createForm.reset();
              setShowCreateModal(false);
            }}
            title="Create New User"
            size="lg"
          >
            <div className="space-y-4">
              {createErrors.general && (
                <Alert type="error">{createErrors.general}</Alert>
              )}
              <FormField 
                label="Username" 
                error={createErrors.username}
                required
              >
                <Input
                  type="text"
                  value={newUser.username}
                  onChange={(e) => {
                    const v = e.target.value;
                    createForm.setValue('user', { ...newUser, username: v });
                    setUsernameStatus('idle');
                  }}
                  placeholder="Enter username"
                />
                {!createErrors.username && newUser.username && (
                  <div className="mt-2 text-xs flex items-center gap-1" aria-live="polite" aria-atomic="true">
                    {usernameStatus === 'checking' && (
                      <svg className="animate-spin h-3 w-3 text-pf-text-tertiary" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v3a5 5 0 00-5 5H4z" />
                      </svg>
                    )}
                    {usernameStatus === 'available' && <span className="text-pf-success">✓ Available</span>}
                    {usernameStatus === 'taken' && <span className="text-pf-error">✗ Already taken</span>}
                    {usernameStatus === 'error' && <span className="text-pf-warning">✗ Check failed</span>}
                  </div>
                )}
              </FormField>
              
              <FormField 
                label="Email" 
                error={createErrors.email}
                required
              >
                <Input
                  type="email"
                  value={newUser.email}
                  onChange={(e) => {
                    const v = e.target.value;
                    createForm.setValue('user', { ...newUser, email: v });
                    setEmailStatus('idle');
                  }}
                  placeholder="Enter email address"
                />
                {!createErrors.email && newUser.email && (
                  <div className="mt-2 text-xs flex items-center gap-1" aria-live="polite" aria-atomic="true">
                    {emailStatus === 'checking' && (
                      <svg className="animate-spin h-3 w-3 text-pf-text-tertiary" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v3a5 5 0 00-5 5H4z" />
                      </svg>
                    )}
                    {emailStatus === 'available' && <span className="text-pf-success">✓ Available</span>}
                    {emailStatus === 'taken' && <span className="text-pf-error">✗ Already taken</span>}
                    {emailStatus === 'error' && <span className="text-pf-warning">✗ Check failed</span>}
                  </div>
                )}
              </FormField>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <FormField label="First Name" required={false}>
                  <Input
                    type="text"
                    value={newUser.firstName}
                    onChange={(e) => createForm.setValue('user', { ...newUser, firstName: e.target.value })}
                    placeholder="Optional"
                  />
                </FormField>
                <FormField label="Last Name" required={false}>
                  <Input
                    type="text"
                    value={newUser.lastName}
                    onChange={(e) => createForm.setValue('user', { ...newUser, lastName: e.target.value })}
                    placeholder="Optional"
                  />
                </FormField>
              </div>

              <FormField 
                label="Password" 
                error={createErrors.password}
                required
              >
                <Input
                  type="password"
                  value={newUser.password}
                  onChange={(e) => createForm.setValue('user', { ...newUser, password: e.target.value })}
                  placeholder="Enter password"
                />
                {passwordPolicy && (
                  <ul className="mt-3 space-y-1 text-xs">
                    <li className={newUser.password.length >= passwordPolicy.minLength ? 'text-pf-success' : 'text-pf-text-secondary'}>
                      ✓ Min length: {passwordPolicy.minLength}
                    </li>
                    {passwordPolicy.requireUppercase && (
                      <li className={/[A-Z]/.test(newUser.password) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                        ✓ At least one uppercase letter
                      </li>
                    )}
                    {passwordPolicy.requireLowercase && (
                      <li className={/[a-z]/.test(newUser.password) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                        ✓ At least one lowercase letter
                      </li>
                    )}
                    {passwordPolicy.requireDigit && (
                      <li className={/[0-9]/.test(newUser.password) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                        ✓ At least one digit
                      </li>
                    )}
                    {passwordPolicy.requireSymbol && (
                      <li className={/[^A-Za-z0-9]/.test(newUser.password) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                        ✓ At least one symbol
                      </li>
                    )}
                  </ul>
                )}
              </FormField>

              <FormField 
                label="Role" 
                required
              >
                <Select
                  value={selectedRoleId}
                  onChange={(e) => {
                    createForm.setValue('roleId', e.target.value);
                    const selectedRole = roles.find(r => r.id === e.target.value);
                    if (selectedRole?.name === 'farm_admin') {
                      createForm.setValue('permissions', APPLICATION_AREAS.map(a => a.id));
                    } else if (selectedRole?.name === 'farm_user') {
                      createForm.setValue('permissions', ['printers', 'files', 'jobs', 'spools']);
                    } else {
                      createForm.setValue('permissions', []);
                    }
                  }}
                >
                  <option value="">Select a role...</option>
                  {roles.map(role => (
                    <option key={role.id} value={role.id}>
                      {role.displayName}
                    </option>
                  ))}
                </Select>
                {selectedRoleId && (
                  <p className="mt-2 text-xs text-pf-text-secondary">
                    {roles.find(r => r.id === selectedRoleId)?.description}
                  </p>
                )}
              </FormField>

              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-3">
                  Application Access
                </label>
                <p className="text-xs text-pf-text-secondary mb-3">
                  Select which areas of the application this user can access:
                </p>
                <div className="space-y-3">
                  {APPLICATION_AREAS.map(area => {
                    const isAdmin = isAdminRole(roles.find(r => r.id === selectedRoleId)?.name);
                    const isDisabled = isAdmin;

                    return (
                      <label key={area.id} className="flex items-start gap-3 p-2 hover:bg-pf-bg-2 rounded-sm cursor-pointer transition">
                        <Checkbox
                          checked={selectedPermissions.includes(area.id)}
                          onChange={() => {
                            if (!isDisabled) {
                              createForm.setValue(
                                'permissions',
                                selectedPermissions.includes(area.id)
                                  ? selectedPermissions.filter(id => id !== area.id)
                                  : [...selectedPermissions, area.id],
                              );
                            }
                          }}
                          disabled={isDisabled}
                        />
                        <div className="flex-1">
                          <div className="text-sm font-medium text-pf-text-primary">{area.name}</div>
                          <div className="text-xs text-pf-text-secondary">{area.description}</div>
                          {isDisabled && <div className="text-xs text-pf-warning mt-1">Included with admin role</div>}
                        </div>
                      </label>
                    );
                  })}
                </div>
              </div>
            </div>

            <AdminSaveBar
              isDirty={createForm.isDirty}
              changeCount={createForm.changedCount}
              changedLabels={createForm.changedKeys.map(key => ({
                user: 'User details',
                roleId: 'Role',
                permissions: 'Application access',
              })[key])}
              onDiscard={() => {
                createForm.reset();
                setShowCreateModal(false);
              }}
              onSave={createUser}
              isSaving={isCreating}
              error={usernameStatus === 'taken' || emailStatus === 'taken'
                ? 'Choose an available username and email before creating the user.'
                : null}
              saveLabel="Create user"
              discardLabel="Cancel"
              className="-mx-6 -mb-6 mt-6"
            />
          </Modal>
        )}
        {showEditModal && selectedUser && (
          <Modal
            isOpen={showEditModal}
            onClose={() => {
              if (isSavingUser) return;
              editForm.reset();
              setShowEditModal(false);
            }}
            title={`Edit User: ${selectedUser.username}`}
            size="lg"
          >
            <form className="space-y-4" onSubmit={(event) => event.preventDefault()}>
              <FormField label="First Name">
                <Input
                  type="text"
                  value={selectedUser.firstName || ''}
                  onChange={e => updateSelectedUser(user => ({ ...user, firstName: e.target.value }))}
                  placeholder="First Name"
                  disabled={isSavingUser}
                />
              </FormField>

              <FormField label="Last Name">
                <Input
                  type="text"
                  value={selectedUser.lastName || ''}
                  onChange={e => updateSelectedUser(user => ({ ...user, lastName: e.target.value }))}
                  placeholder="Last Name"
                  disabled={isSavingUser}
                />
              </FormField>

              <FormField label="Email" required>
                <Input
                  type="email"
                  value={selectedUser.email}
                  onChange={e => updateSelectedUser(user => ({ ...user, email: e.target.value }))}
                  placeholder="Email"
                  disabled={isSavingUser}
                />
              </FormField>

              <FormField label="Active">
                <Checkbox
                  checked={selectedUser.isActive}
                  onChange={e => updateSelectedUser(user => ({ ...user, isActive: e.target.checked }))}
                  label="User account is active"
                  disabled={isSavingUser}
                />
              </FormField>

              <FormField label="Role" required>
                <Select
                  value={selectedUser.roles[0] || ''}
                  disabled={isSavingUser}
                  onChange={(e) => {
                    updateSelectedUser(user => {
                      const newRole = e.target.value;
                      let newPermissions = user.permissions;
                      if (newRole === 'farm_admin') {
                        newPermissions = APPLICATION_AREAS.map(a => a.id);
                      } else if (newRole === 'farm_user' && !newPermissions.includes('printers')) {
                        newPermissions = ['printers', 'files', 'jobs', 'spools'];
                      }
                      return {
                        ...user,
                        roles: newRole ? [newRole] : [],
                        permissions: newPermissions
                      };
                    });
                  }}
                >
                  <option value="">Select a role...</option>
                  {roles.map(role => (
                    <option key={role.id} value={role.name}>
                      {role.displayName}
                    </option>
                  ))}
                </Select>
                {selectedUser.roles[0] && (
                  <p className="mt-2 text-xs text-pf-text-secondary">
                    {roles.find(r => r.name === selectedUser.roles[0])?.description}
                  </p>
                )}
              </FormField>

              <div>
                <div className="flex items-center justify-between mb-3">
                  <label className="text-sm font-medium text-pf-text-primary">Application Access</label>
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    disabled={isSavingUser}
                    onClick={() => {
                      permissionForm.markPristine({ permissions: selectedUser.permissions });
                      setShowPermissionsModal(true);
                    }}
                  >
                    Edit Permissions →
                  </Button>
                </div>
                <div className="flex flex-wrap gap-2 p-3 bg-pf-bg-0 rounded-sm border border-pf-border">
                  {selectedUser.permissions.length > 0 ? (
                    selectedUser.permissions.map(p => {
                      const area = APPLICATION_AREAS.find(a => a.id === p);
                      return (
                        <span key={p} className="inline-block bg-pf-accent/20 text-pf-accent px-2 py-1 rounded-sm text-xs font-medium" title={area?.description}>
                          {area?.name || p}
                        </span>
                      );
                    })
                  ) : (
                    <span className="text-pf-text-tertiary text-xs">No accessible areas configured</span>
                  )}
                </div>
              </div>
            </form>

            <AdminSaveBar
              isDirty={editForm.isDirty}
              changeCount={editForm.changedCount}
              changedLabels={['User details']}
              onDiscard={() => {
                editForm.reset();
                setShowEditModal(false);
              }}
              onSave={saveSelectedUser}
              isSaving={isSavingUser}
              saveLabel="Save changes"
              discardLabel="Cancel"
              className="-mx-6 -mb-6 mt-6"
            />
          </Modal>
        )}

        {/* Permissions Modal */}
        {showPermissionsModal && selectedUser && (
          <Modal
            isOpen={showPermissionsModal}
            onClose={() => {
              if (isSavingPermissions) return;
              permissionForm.reset();
              setShowPermissionsModal(false);
            }}
            title={`Manage Application Access for ${selectedUser.username}`}
            size="lg"
          >
            <div className="space-y-4">
              <p className="text-sm text-pf-text-secondary">
                Select which areas of the application this user can access:
              </p>
              <div className="space-y-3 bg-pf-bg-0 p-4 rounded-sm border border-pf-border">
                {APPLICATION_AREAS.map(area => {
                  const userRole = selectedUser.roles[0];
                  const isAdmin = userRole === 'farm_admin';
                  const isDisabled = isAdmin;
                  const hasAccess = permissionForm.values.permissions.includes(area.id);

                  return (
                    <label key={area.id} className="flex items-start gap-3 p-2 hover:bg-pf-bg-1 rounded-sm cursor-pointer transition">
                      <Checkbox
                        checked={hasAccess}
                        onChange={() => {
                          if (!isDisabled) {
                            const updatedPermissions = hasAccess
                              ? permissionForm.values.permissions.filter(id => id !== area.id)
                              : [...permissionForm.values.permissions, area.id];
                            permissionForm.setValue('permissions', updatedPermissions);
                          }
                        }}
                        disabled={isDisabled || isSavingPermissions}
                      />
                      <div className="flex-1">
                        <div className="text-sm font-medium text-pf-text-primary">{area.name}</div>
                        <div className="text-xs text-pf-text-secondary">{area.description}</div>
                        {isDisabled && <div className="text-xs text-pf-warning mt-1">Included with admin role</div>}
                      </div>
                    </label>
                  );
                })}
              </div>
            </div>

            <AdminSaveBar
              isDirty={permissionForm.isDirty}
              changeCount={permissionForm.changedCount}
              changedLabels={['Application access']}
              onDiscard={() => {
                permissionForm.reset();
                setShowPermissionsModal(false);
              }}
              onSave={savePermissions}
              isSaving={isSavingPermissions}
              saveLabel="Save permissions"
              discardLabel="Cancel"
              className="-mx-6 -mb-6 mt-6"
            />
          </Modal>
        )}

        {showChangePasswordModal && userToChangePassword && (
          <Modal
            isOpen={showChangePasswordModal}
            onClose={() => {
              if (isChangingPassword) return;
              setShowChangePasswordModal(false);
              setUserToChangePassword(null);
              setShowChangePasswordConfirm(false);
              passwordForm.reset();
              setChangePasswordError(null);
            }}
            title={`Change Password: ${userToChangePassword.username}`}
            size="md"
          >
            <div className="space-y-4">
              {changePasswordError && <Alert type="error">{changePasswordError}</Alert>}

              <Alert type="warning" title="Admin Action">
                This will immediately replace the user's password and revoke existing sessions.
              </Alert>

              <FormField label="New Password" required>
                <Input
                  type="password"
                  value={passwordChangeForm.newPassword}
                  onChange={(e) => {
                    passwordForm.setValue('newPassword', e.target.value);
                    setChangePasswordError(null);
                  }}
                  placeholder="Enter new password"
                  disabled={isChangingPassword}
                />
              </FormField>

              <FormField label="Confirm New Password" required>
                <Input
                  type="password"
                  value={passwordChangeForm.confirmNewPassword}
                  onChange={(e) => {
                    passwordForm.setValue('confirmNewPassword', e.target.value);
                    setChangePasswordError(null);
                  }}
                  placeholder="Confirm new password"
                  disabled={isChangingPassword}
                />
              </FormField>

              {passwordPolicy && (
                <ul className="space-y-1 text-xs text-pf-text-secondary">
                  <li className={passwordChangeForm.newPassword.length >= passwordPolicy.minLength ? 'text-pf-success' : 'text-pf-text-secondary'}>
                    ✓ Min length: {passwordPolicy.minLength}
                  </li>
                  {passwordPolicy.requireUppercase && (
                    <li className={/[A-Z]/.test(passwordChangeForm.newPassword) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                      ✓ At least one uppercase letter
                    </li>
                  )}
                  {passwordPolicy.requireLowercase && (
                    <li className={/[a-z]/.test(passwordChangeForm.newPassword) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                      ✓ At least one lowercase letter
                    </li>
                  )}
                  {passwordPolicy.requireDigit && (
                    <li className={/[0-9]/.test(passwordChangeForm.newPassword) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                      ✓ At least one digit
                    </li>
                  )}
                  {passwordPolicy.requireSymbol && (
                    <li className={/[^A-Za-z0-9]/.test(passwordChangeForm.newPassword) ? 'text-pf-success' : 'text-pf-text-secondary'}>
                      ✓ At least one symbol
                    </li>
                  )}
                </ul>
              )}
            </div>

            <AdminSaveBar
              isDirty={passwordForm.isDirty}
              changeCount={passwordForm.changedCount}
              changedLabels={passwordForm.changedKeys.map(key => ({
                newPassword: 'New password',
                confirmNewPassword: 'Password confirmation',
              })[key])}
              onDiscard={() => {
                passwordForm.reset();
                setShowChangePasswordModal(false);
                setUserToChangePassword(null);
                setChangePasswordError(null);
              }}
              onSave={() => {
                if (passwordChangeForm.newPassword !== passwordChangeForm.confirmNewPassword) {
                  setChangePasswordError('Password confirmation does not match.');
                  return;
                }
                if (!passwordMeetsPolicyValue(passwordChangeForm.newPassword)) {
                  setChangePasswordError('Password does not meet policy requirements.');
                  return;
                }
                setShowChangePasswordConfirm(true);
              }}
              isSaving={isChangingPassword}
              error={changePasswordError}
              saveLabel="Continue"
              discardLabel="Cancel"
              className="-mx-6 -mb-6 mt-6"
            />
          </Modal>
        )}

        <ConfirmationModal
          isOpen={showChangePasswordConfirm}
          title="Confirm Password Change?"
          message={`Are you sure you want to change the password for "${userToChangePassword?.username}"?`}
          confirmButtonText={isChangingPassword ? 'Changing...' : 'Change Password'}
          cancelButtonText="Cancel"
          isDangerous
          onConfirm={async () => {
            if (!userToChangePassword || isChangingPassword) return;
            setIsChangingPassword(true);
            try {
              await apiClient.adminChangeUserPassword(
                userToChangePassword.id,
                passwordChangeForm.newPassword,
                passwordChangeForm.confirmNewPassword
              );
              adminToast.success(`Password changed for "${userToChangePassword.username}"`);
              setShowChangePasswordConfirm(false);
              setShowChangePasswordModal(false);
              setUserToChangePassword(null);
              passwordForm.markPristine(EMPTY_PASSWORD_FORM);
              setChangePasswordError(null);
            } catch (err) {
              const error = err as { response?: { data?: Record<string, unknown> } };
              const message = (error.response?.data as Record<string, unknown> | undefined)?.error as string
                || (error.response?.data as Record<string, unknown> | undefined)?.message as string
                || 'Failed to change user password';
              setChangePasswordError(message);
              setShowChangePasswordConfirm(false);
              adminToast.error(message);
            } finally {
              setIsChangingPassword(false);
            }
          }}
          onCancel={() => {
            if (isChangingPassword) return;
            setShowChangePasswordConfirm(false);
          }}
        />

        {/* Delete User Confirmation Modal */}
        <ConfirmationModal
          isOpen={!!userToDelete}
          title="Delete User?"
          message={`Are you sure you want to delete user "${userToDelete?.username}"? This action cannot be undone.`}
          confirmButtonText="Delete User"
          cancelButtonText="Cancel"
          isDangerous
          onConfirm={async () => {
            if (!userToDelete) return;
            try {
              await apiClient.deleteUser(userToDelete.id);
              adminToast.success(`User "${userToDelete.username}" deleted`);
              setUserToDelete(null);
              loadUsers();
            } catch (err) {
              const error = err as { response?: { data?: Record<string, unknown> } };
              const message = (error.response?.data as Record<string, unknown> | undefined)?.message as string || 'Failed to delete user';
              adminToast.error(message);
              setUserToDelete(null);
            }
          }}
          onCancel={() => setUserToDelete(null)}
        />
      </div>
    </PageTemplate>
  );
}