import React, { useState, useEffect } from 'react';
import { usePasswordPolicy } from '@/hooks/usePasswordPolicy';
import { toast } from 'sonner';
import { PageTemplate } from '@/components/PageTemplate';
import { 
  Users, 
  Plus, 
  Edit, 
  Trash2, 
  Shield, 
  Search,
  UserX,
  UserCheck
} from 'lucide-react';
import { TableSkeleton } from '@/components/skeletons/TableSkeleton';
import { useAuth } from '@/contexts/AuthHooks';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

interface User {
  id: string;
  username: string;
  email: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
  emailConfirmed: boolean;
  lastLogin?: string;
  createdAt: string;
  roles: string[];
  permissions: string[];
}

interface Role {
  id: string;
  name: string;
  displayName: string;
  description?: string;
  isSystemRole: boolean;
  isActive: boolean;
}

export function UserManagementPage() {
  const { hasRole } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState('');
  const [selectedUser, setSelectedUser] = useState<User | null>(null);
  const [showCreateModal, setShowCreateModal] = useState(false);
  const [showEditModal, setShowEditModal] = useState(false);
  const [showPermissionsModal, setShowPermissionsModal] = useState(false);
  const { data: passwordPolicy } = usePasswordPolicy();
  const [newUser, setNewUser] = useState({ username: '', email: '', password: '', firstName: '', lastName: '' });
  const [creating, setCreating] = useState(false);
  const [selectedRoleId, setSelectedRoleId] = useState<string>('');
  const [applicationAreas, setApplicationAreas] = useState<Array<{id: string; name: string; description: string}>>([]);
  const [selectedPermissions, setSelectedPermissions] = useState<string[]>([]);
  const [createErrors, setCreateErrors] = useState<{username?: string; email?: string; password?: string; general?: string; roles?: string}>({});
  type AvailabilityStatus = 'idle' | 'checking' | 'available' | 'taken' | 'error';
  const [usernameStatus, setUsernameStatus] = useState<AvailabilityStatus>('idle');
  const [emailStatus, setEmailStatus] = useState<AvailabilityStatus>('idle');
  const [, setAvailabilityMessage] = useState('');
  const DEBOUNCE_MS = 450;

  // Helper: Check if a role is admin role
  const isAdminRole = (roleName: string | undefined) => roleName === 'farm_admin';

  // Helper: Get primary role for display (admin > user > other)
  const getPrimaryRole = (roleNames: string[]): string => {
    if (roleNames.includes('farm_admin')) return 'farm_admin';
    if (roleNames.includes('farm_user')) return 'farm_user';
    return roleNames[0] || '';
  };

  const passwordMeetsPolicy = () => {
    if (!passwordPolicy) return true; // don't block while loading
    const p = newUser.password;
    if (p.length < passwordPolicy.minLength) return false;
    if (passwordPolicy.requireUppercase && !/[A-Z]/.test(p)) return false;
    if (passwordPolicy.requireLowercase && !/[a-z]/.test(p)) return false;
    if (passwordPolicy.requireDigit && !/[0-9]/.test(p)) return false;
    if (passwordPolicy.requireSymbol && !/[^A-Za-z0-9]/.test(p)) return false;
    return true;
  };

  // Batched debounced availability checks (single request for username + email)
  useEffect(() => {
    if (!showCreateModal) return;

    const username = newUser.username.trim();
    const email = newUser.email.trim();

    // If both empty, reset statuses
    if (!username && !email) {
      if (usernameStatus !== 'idle') setUsernameStatus('idle');
      if (emailStatus !== 'idle') setEmailStatus('idle');
      return;
    }

    // Mark only the fields that have a value as checking
    if (username) setUsernameStatus('checking'); else setUsernameStatus('idle');
    if (email) setEmailStatus('checking'); else setEmailStatus('idle');

    const ctrl = new AbortController();
    const handle = setTimeout(async () => {
      try {
        const params = new URLSearchParams();
        if (username) params.append('username', username);
        if (email) params.append('email', email);
        const res = await fetch(`${getApiBaseUrl()}/users/availability?${params.toString()}`, { signal: ctrl.signal, headers: getAuthHeaders() });
        if (!res.ok) throw new Error('availability failed');
        const data: { usernameExists?: boolean; emailExists?: boolean } = await res.json();

        if (username) {
          const uTaken = data.usernameExists === true;
            setUsernameStatus(uTaken ? 'taken' : 'available');
          if (!uTaken && createErrors.username === 'Username already taken') {
            setCreateErrors(errs => ({ ...errs, username: undefined }));
          }
          // Set message only if email absent (avoid overwriting with mixed)
          if (!email) {
            setAvailabilityMessage(uTaken ? 'Username is already taken' : 'Username is available');
          }
        }
        if (email) {
          const eTaken = data.emailExists === true;
          setEmailStatus(eTaken ? 'taken' : 'available');
          if (!eTaken && createErrors.email === 'Email already taken') {
            setCreateErrors(errs => ({ ...errs, email: undefined }));
          }
          // If both present, prefer more specific combined message only when needed
          if (username) {
            if (data.usernameExists && data.emailExists) setAvailabilityMessage('Username and email are already taken');
            else if (data.usernameExists) setAvailabilityMessage('Username is already taken');
            else if (data.emailExists) setAvailabilityMessage('Email is already taken');
            else setAvailabilityMessage('Username and email are available');
          } else {
            setAvailabilityMessage(eTaken ? 'Email is already taken' : 'Email is available');
          }
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
  }, [newUser.username, newUser.email, showCreateModal, createErrors.email, createErrors.username, emailStatus, usernameStatus]);

  const validateForm = () => {
    const errs: typeof createErrors = {};
    if (!newUser.username.trim()) errs.username = 'Username is required';
    if (!newUser.email.trim()) errs.email = 'Email is required';
    else if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(newUser.email.trim())) errs.email = 'Invalid email format';
    if (!newUser.password) errs.password = 'Password is required';
    else if (!passwordMeetsPolicy()) errs.password = 'Password does not meet policy';
    return errs;
  };

  const createUser = async () => {
    if (creating) return;
    const fieldErrs = validateForm();
    if (Object.keys(fieldErrs).length > 0) {
      setCreateErrors(fieldErrs);
      return;
    }

    try {
      setCreating(true);
      setCreateErrors({});

      const response = await fetch(`${getApiBaseUrl()}/users`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders()
        },
        body: JSON.stringify({
          username: newUser.username.trim(),
          email: newUser.email.trim(),
          password: newUser.password,
          firstName: newUser.firstName.trim() || undefined,
          lastName: newUser.lastName.trim() || undefined,
          roleIds: selectedRoleId ? [selectedRoleId] : [],
          accessibleAreas: selectedPermissions
        })
      });

      if (!response.ok) {
        let errorMessage = 'Failed to create user';
        let json: { message?: string; error?: string; title?: string; errors?: Record<string, string[]> } | null = null;
        try {
          // API may return JSON or plain text
            const contentType = response.headers.get('Content-Type') || '';
            if (contentType.includes('application/json')) {
              json = await response.json();
              if (json) {
                errorMessage = json.error || json.message || json.title || errorMessage;
              }
            } else {
              errorMessage = (await response.text()) || errorMessage;
            }
        } catch (e) {
          console.warn('Failed to parse error response:', e);
        }
        
        // Extract field errors if any
        if (json?.errors) {
          const fieldErrors: Record<string, string> = {};
          for (const key in json.errors) {
            const msgArray = json.errors[key];
            if (Array.isArray(msgArray) && msgArray.length > 0) {
              fieldErrors[key] = msgArray[0];
            }
          }
          setCreateErrors(fieldErrors);
        }

        toast.error(errorMessage);
        return;
      }

      // We could optimistically insert but reloading ensures roles & computed fields
      await loadUsers();
      toast.success('User created');
      setShowCreateModal(false);
      setNewUser({ username: '', email: '', password: '', firstName: '', lastName: '' });
      setSelectedRoleId('');
      setSelectedPermissions([]);
    } catch (err) {
      console.error('Error creating user', err);
      toast.error('Unexpected error creating user');
    } finally {
      setCreating(false);
    }
  };

  useEffect(() => {
    loadUsers();
    loadRoles();
    loadApplicationAreas();
  }, []);

  const loadApplicationAreas = async () => {
    try {
      // Start with common application areas. In future, these could come from API
      setApplicationAreas([
        { id: 'printers', name: 'Printers', description: 'View and manage printer configurations' },
        { id: 'files', name: 'Files', description: 'Access harvested G-code files' },
        { id: 'harvest', name: 'Harvest', description: 'Use the harvester interface' },
        { id: 'jobs', name: 'Jobs', description: 'View and manage print jobs' },
        { id: 'catalog', name: 'Catalog', description: 'Access manufacturer and model catalog' },
        { id: 'settings', name: 'Settings', description: 'Modify account and application settings' },
        { id: 'spools', name: 'Spools', description: 'Manage filament spools inventory' }
      ]);
    } catch (error) {
      console.error('Error loading application areas:', error);
    }
  };

  const loadUsers = async () => {
    try {
      const response = await fetch(`${getApiBaseUrl()}/users`, {
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders()
        }
      });
      
      if (response.ok) {
        const data = await response.json();
        setUsers(data);
      } else {
        console.error('Failed to load users');
      }
    } catch (error) {
      console.error('Error loading users:', error);
    } finally {
      setLoading(false);
    }
  };

  const loadRoles = async () => {
    try {
      const response = await fetch(`${getApiBaseUrl()}/users/roles`, {
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders()
        }
      });
      
      if (response.ok) {
        const data = await response.json();
        setRoles(data);
      } else {
        console.error('Failed to load roles');
      }
    } catch (error) {
      console.error('Error loading roles:', error);
    }
  };

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
        return 'bg-red-100 text-red-800 border-red-200';
      case 'farm_user':
        return 'bg-blue-100 text-blue-800 border-blue-200';
      default:
        return 'bg-gray-100 text-gray-800 border-gray-200';
    }
  };

  // Early access check AFTER hooks to avoid conditional hook usage
  if (!hasRole('farm_admin')) {
    return (
      <div className="flex items-center justify-center min-h-screen" aria-live="polite" aria-label="Access denied message">
        <div className="text-center" role="alert">
          <Shield className="h-16 w-16 mx-auto text-red-500 mb-4" aria-hidden="true" />
          <h2 className="text-xl font-semibold text-pf-text-primary mb-2">
            Access Denied
          </h2>
          <p className="text-pf-text-secondary">
            You need administrator privileges to access user management.
          </p>
        </div>
      </div>
    );
  }

  if (loading) {
    return (
      <PageTemplate
        title="User Management"
        subtitle="Manage user accounts, roles, and permissions for PrintFarmer."
        icon={Users}
        maxWidth="max-w-7xl"
      >
        <TableSkeleton rows={6} cols={6} />
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="User Management"
      subtitle="Manage user accounts, roles, and permissions for PrintFarmer."
      icon={Users}
      maxWidth="max-w-7xl"
      actions={
        <button
          onClick={() => {
            const farmUserRole = roles.find(r => r.name === 'farm_user');
            setSelectedRoleId(farmUserRole ? farmUserRole.id : '');
            setSelectedPermissions([]);
            setCreateErrors({});
            setNewUser({ username: '', email: '', password: '', firstName: '', lastName: '' });
            setShowCreateModal(true);
          }}
          className="btn-base btn-md btn-primary"
        >
          <Plus className="h-4 w-4 mr-2" />
          Add User
        </button>
      }
    >
      {/* Controls */}
      <div className="mb-6 flex flex-col sm:flex-row gap-4 justify-between">
        <div className="relative">
          <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 h-4 w-4 text-pf-text-tertiary" />
          <input
            type="text"
            placeholder="Search users..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="pl-10 pr-4 input-base"
          />
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
                          className={`inline-flex px-2 py-1 text-xs rounded-full border ${getRoleBadgeColor(role)}`}
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
                          <UserCheck className="h-4 w-4 text-green-500 mr-2" />
                          <span className="text-sm text-green-700">Active</span>
                        </>
                      ) : (
                        <>
                          <UserX className="h-4 w-4 text-red-500 mr-2" />
                          <span className="text-sm text-red-700">Inactive</span>
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
                      <button
                        onClick={() => {
                          setSelectedUser(user);
                          setShowPermissionsModal(true);
                        }}
                        className="p-2 text-pf-text-secondary hover:text-pf-accent rounded-md hover:bg-pf-bg-2"
                        title="Manage permissions"
                      >
                        <Shield className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => {
                          setSelectedUser(user);
                          setShowEditModal(true);
                        }}
                        className="p-2 text-pf-text-secondary hover:text-pf-accent rounded-md hover:bg-pf-bg-2"
                        title="Edit user"
                      >
                        <Edit className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => {
                            if (confirm(`Are you sure you want to delete user "${user.username}"?`)) {
                            // TODO: Implement delete user
                            if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.userManagementPage) {
                              if (typeof window !== 'undefined' && (window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.userManagementPage) {
                                console.log('Delete user:', user.id);
                              }
                            }
                          }
                        }}
                        className="p-2 text-pf-text-secondary hover:text-red-500 rounded-md hover:bg-pf-bg-2"
                        title="Delete user"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {filteredUsers.length === 0 && (
          <div className="text-center py-8">
            <Users className="h-12 w-12 mx-auto text-pf-text-tertiary mb-4" />
            <p className="text-pf-text-secondary">
              {searchTerm ? 'No users found matching your search.' : 'No users found.'}
            </p>
          </div>
        )}
      </div>

      {/* User count and modals section */}
      <div>
        {/* User count */}
        <div className="mt-4 text-sm text-pf-text-secondary">
          Showing {filteredUsers.length} of {users.length} users
        </div>

        {/* TODO: Modals for create/edit users */}
        {showCreateModal && (
        <div className="modal-overlay">
          <div className="modal modal-lg">
            <div className="modal-header">
              <h3 className="modal-header-title">Create New User</h3>
            </div>
            <div className="modal-body">
            {createErrors.general && (
              <div className="alert-base alert-error mb-4" role="alert">
                {createErrors.general}
              </div>
            )}
            <div className="gap-md flex-col" role="group" aria-label="Create user form">
              <div>
                <label htmlFor="create-username" className="block text-sm font-medium mb-1">Username</label>
                <input
                  id="create-username"
                  type="text"
                  value={newUser.username}
                  onChange={(e) => {
                    const v = e.target.value;
                    setNewUser(u => ({ ...u, username: v }));
                    setCreateErrors(errs => ({ ...errs, username: undefined }));
                    setUsernameStatus('idle');
                  }}
                  className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
                />
                {createErrors.username && <p className="text-xs text-red-500 mt-1" role="alert">{createErrors.username}</p>}
                {!createErrors.username && newUser.username && (
                  <p className="text-xs mt-1 flex items-center gap-1" aria-live="polite" aria-atomic="true">
                    {usernameStatus === 'checking' && (
                      <svg className="animate-spin h-3 w-3 text-pf-text-tertiary" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v3a5 5 0 00-5 5H4z" />
                      </svg>
                    )}
                    {usernameStatus === 'available' && <span className="text-green-600">Username available</span>}
                    {usernameStatus === 'taken' && <span className="text-red-500">Username already taken</span>}
                    {usernameStatus === 'error' && <span className="text-orange-500">Username check failed</span>}
                  </p>
                )}
              </div>
              <div>
                <label htmlFor="create-email" className="block text-sm font-medium mb-1">Email</label>
                <input
                  id="create-email"
                  type="email"
                  value={newUser.email}
                  onChange={(e) => {
                    const v = e.target.value;
                    setNewUser(u => ({ ...u, email: v }));
                    setCreateErrors(errs => ({ ...errs, email: undefined }));
                    setEmailStatus('idle');
                  }}
                  className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
                />
                {createErrors.email && <p className="text-xs text-red-500 mt-1" role="alert">{createErrors.email}</p>}
                {!createErrors.email && newUser.email && (
                  <p className="text-xs mt-1 flex items-center gap-1" aria-live="polite" aria-atomic="true">
                    {emailStatus === 'checking' && (
                      <svg className="animate-spin h-3 w-3 text-pf-text-tertiary" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v3a5 5 0 00-5 5H4z" />
                      </svg>
                    )}
                    {emailStatus === 'available' && <span className="text-green-600">Email available</span>}
                    {emailStatus === 'taken' && <span className="text-red-500">Email already taken</span>}
                    {emailStatus === 'error' && <span className="text-orange-500">Email check failed</span>}
                  </p>
                )}
              </div>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                  <label htmlFor="create-first-name" className="block text-sm font-medium mb-1">First Name <span className="text-xs text-pf-text-tertiary">(optional)</span></label>
                  <input
                    id="create-first-name"
                    type="text"
                    value={newUser.firstName}
                    onChange={(e) => setNewUser(u => ({ ...u, firstName: e.target.value }))}
                    className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
                  />
                </div>
                <div>
                  <label htmlFor="create-last-name" className="block text-sm font-medium mb-1">Last Name <span className="text-xs text-pf-text-tertiary">(optional)</span></label>
                  <input
                    id="create-last-name"
                    type="text"
                    value={newUser.lastName}
                    onChange={(e) => setNewUser(u => ({ ...u, lastName: e.target.value }))}
                    className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
                  />
                </div>
              </div>
              <div>
                <label htmlFor="create-password" className="block text-sm font-medium mb-1">Password</label>
                <input
                  id="create-password"
                  type="password"
                  value={newUser.password}
                  onChange={(e) => setNewUser(u => ({ ...u, password: e.target.value }))}
                  className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
                />
                {createErrors.password && <p className="text-xs text-red-500 mt-1" role="alert">{createErrors.password}</p>}
                {passwordPolicy && (
                  <ul className="mt-2 text-xs space-y-1 text-pf-text-secondary">
                    <li className={newUser.password.length >= passwordPolicy.minLength ? 'text-green-500' : ''}>Min length: {passwordPolicy.minLength}</li>
                    {passwordPolicy.requireUppercase && <li className={/[A-Z]/.test(newUser.password) ? 'text-green-500' : ''}>At least one uppercase letter</li>}
                    {passwordPolicy.requireLowercase && <li className={/[a-z]/.test(newUser.password) ? 'text-green-500' : ''}>At least one lowercase letter</li>}
                    {passwordPolicy.requireDigit && <li className={/[0-9]/.test(newUser.password) ? 'text-green-500' : ''}>At least one digit</li>}
                    {passwordPolicy.requireSymbol && <li className={/[^A-Za-z0-9]/.test(newUser.password) ? 'text-green-500' : ''}>At least one symbol</li>}
                  </ul>
                )}
              </div>
              <div>
                <label htmlFor="create-role" className="block text-sm font-medium mb-1">Role</label>
                <select
                  id="create-role"
                  value={selectedRoleId}
                  onChange={(e) => {
                    setSelectedRoleId(e.target.value);
                    // Auto-select appropriate permissions based on role
                    const selectedRole = roles.find(r => r.id === e.target.value);
                    if (selectedRole?.name === 'farm_admin') {
                      // Admins get access to everything
                      setSelectedPermissions(applicationAreas.map(a => a.id));
                    } else if (selectedRole?.name === 'farm_user') {
                      // Regular users get basic access
                      setSelectedPermissions(['printers', 'files', 'jobs', 'spools']);
                    } else {
                      setSelectedPermissions([]);
                    }
                  }}
                  className="w-full input-base"
                >
                  <option value="">Select a role...</option>
                  {roles.map(role => (
                    <option key={role.id} value={role.id}>
                      {role.displayName}
                    </option>
                  ))}
                </select>
                <p className="text-xs text-pf-text-tertiary mt-1">
                  {selectedRoleId ? roles.find(r => r.id === selectedRoleId)?.description || '' : 'Choose a role to assign permissions'}
                </p>
              </div>
              <div>
                <label className="block text-sm font-medium mb-2">Application Access</label>
                <p className="text-xs text-pf-text-secondary mb-3">Select which areas of the application this user can access:</p>
                <div className="space-y-2">
                  {applicationAreas.map(area => {
                    const isAdmin = isAdminRole(roles.find(r => r.id === selectedRoleId)?.name);
                    const isDisabled = isAdmin; // Admins always have full access
                    
                    return (
                      <label key={area.id} className="flex items-start gap-2 p-2 hover:bg-pf-bg-0 rounded cursor-pointer transition">
                        <input
                          type="checkbox"
                          checked={selectedPermissions.includes(area.id)}
                          onChange={() => {
                            if (!isDisabled) {
                              setSelectedPermissions(prev => 
                                prev.includes(area.id) 
                                  ? prev.filter(id => id !== area.id)
                                  : [...prev, area.id]
                              );
                            }
                          }}
                          disabled={isDisabled}
                          className="mt-0.5 cursor-pointer"
                        />
                        <div className="flex-1">
                          <div className="text-sm font-medium text-pf-text-primary">{area.name}</div>
                          <div className="text-xs text-pf-text-secondary">{area.description}</div>
                          {isDisabled && <div className="text-xs text-pf-warning-text mt-0.5">Included with admin role</div>}
                        </div>
                      </label>
                    );
                  })}
                </div>
              </div>
            </div>
            <div className="modal-footer">
              <button
                onClick={() => setShowCreateModal(false)}
                className="btn-base btn-md btn-secondary"
              >Cancel</button>
                <button
                  onClick={createUser}
                  disabled={creating || !newUser.username || !newUser.email || !passwordMeetsPolicy() || usernameStatus === 'taken' || emailStatus === 'taken'}
                  className="btn-base btn-md btn-primary"
                >{creating && (
                  <svg className="animate-spin h-4 w-4 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"></path>
                  </svg>
                )}Create</button>
            </div>
            </div>
          </div>
        </div>
      )}
      {showEditModal && selectedUser && (
        <div className="modal-overlay">
          <div className="modal modal-lg">
            <div className="modal-header">
              <h3 className="modal-header-title">Edit User: {selectedUser.username}</h3>
            </div>
            <div className="modal-body">
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                try {
                  const response = await fetch(`${getApiBaseUrl()}/users/${selectedUser.id}`, {
                    method: 'PUT',
                    headers: {
                      'Content-Type': 'application/json',
                      ...getAuthHeaders()
                    },
                    body: JSON.stringify({
                      firstName: selectedUser.firstName,
                      lastName: selectedUser.lastName,
                      email: selectedUser.email,
                      isActive: selectedUser.isActive,
                      roles: selectedUser.roles,
                      permissions: selectedUser.permissions
                    })
                  });
                  if (response.ok) {
                    toast.success('User updated successfully');
                    setUsers(users => users.map(u => u.id === selectedUser.id ? { ...u, ...selectedUser } : u));
                  } else {
                    const err = await response.json().catch(() => ({}));
                    toast.error(err.message || 'Failed to update user');
                  }
                } catch {
                  toast.error('Error updating user');
                } finally {
                  setShowEditModal(false);
                  setSelectedUser(null);
                }
              }}
              className="space-y-4"
            >
              <div>
                <label className="block text-sm font-medium mb-1">First Name</label>
                <input
                  type="text"
                  value={selectedUser.firstName || ''}
                  onChange={e => setSelectedUser(u => u ? { ...u, firstName: e.target.value } : u)}
                  className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
            placeholder="First Name"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Last Name</label>
                <input
                  type="text"
                  value={selectedUser.lastName || ''}
                  onChange={e => setSelectedUser(u => u ? { ...u, lastName: e.target.value } : u)}
                  className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
            placeholder="Last Name"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Email</label>
                <input
                  type="email"
                  value={selectedUser.email}
                  onChange={e => setSelectedUser(u => u ? { ...u, email: e.target.value } : u)}
                  className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded"
            placeholder="Email"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Active</label>
                <input
                  type="checkbox"
                  checked={selectedUser.isActive}
                  onChange={e => setSelectedUser(u => u ? { ...u, isActive: e.target.checked } : u)}
                  className="ml-2"
            title="Active"
                />
              </div>
              <div>
                <label htmlFor="edit-role" className="block text-sm font-medium mb-1">Role</label>
                <select
                  id="edit-role"
                  value={selectedUser.roles[0] || ''}
                  onChange={(e) => {
                    setSelectedUser(u => {
                      if (!u) return u;
                      const newRole = e.target.value;
                      // Auto-update permissions based on role
                      let newPermissions = u.permissions;
                      if (newRole === 'farm_admin') {
                        // Admins get access to everything
                        newPermissions = applicationAreas.map(a => a.id);
                      } else if (newRole === 'farm_user' && !newPermissions.includes('printers')) {
                        // Regular users get basic access
                        newPermissions = ['printers', 'files', 'jobs', 'spools'];
                      }
                      return {
                        ...u,
                        roles: newRole ? [newRole] : [],
                        permissions: newPermissions
                      };
                    });
                  }}
                  className="w-full input-base"
                >
                  <option value="">Select a role...</option>
                  {roles.map(role => (
                    <option key={role.id} value={role.name}>
                      {role.displayName}
                    </option>
                  ))}
                </select>
                <p className="text-xs text-pf-text-tertiary mt-1">
                  {selectedUser.roles[0] ? roles.find(r => r.name === selectedUser.roles[0])?.description || '' : 'Choose a role to assign permissions'}
                </p>
              </div>
              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="block text-sm font-medium">Application Access</label>
                  <button
                    type="button"
                    onClick={() => {
                      setSelectedUser(u => u ? { ...u } : u);
                      setShowPermissionsModal(true);
                    }}
                    className="text-xs text-pf-accent hover:underline"
                  >
                    Edit Permissions →
                  </button>
                </div>
                <div className="flex flex-wrap gap-2 p-2 bg-pf-bg-0 rounded border border-pf-border">
                  {selectedUser.permissions.length > 0 ? (
                    selectedUser.permissions.map(p => {
                      const area = applicationAreas.find(a => a.id === p);
                      return (
                        <span key={p} className="inline-block bg-pf-bg-2 px-2 py-1 rounded text-xs" title={area?.description}>
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
            <div className="modal-footer">
                <button
                  type="button"
                  onClick={() => {
                    setShowEditModal(false);
                    setSelectedUser(null);
                  }}
                  className="btn-base btn-md btn-secondary"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={async () => {
                    try {
                      const response = await fetch(`${getApiBaseUrl()}/users/${selectedUser.id}`, {
                        method: 'PUT',
                        headers: {
                          'Content-Type': 'application/json',
                          ...getAuthHeaders()
                        },
                        body: JSON.stringify({
                          firstName: selectedUser.firstName,
                          lastName: selectedUser.lastName,
                          email: selectedUser.email,
                          isActive: selectedUser.isActive,
                          roles: selectedUser.roles,
                          permissions: selectedUser.permissions
                        })
                      });
                      if (response.ok) {
                        toast.success('User updated successfully');
                        setUsers(users => users.map(u => u.id === selectedUser.id ? { ...u, ...selectedUser } : u));
                        setShowEditModal(false);
                        setSelectedUser(null);
                      } else {
                        const err = await response.json().catch(() => ({}));
                        toast.error(err.message || 'Failed to update user');
                      }
                    } catch {
                      toast.error('Error updating user');
                    }
                  }}
                  className="btn-base btn-md btn-primary"
                >
                  Save
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Permissions Modal */}
      {showPermissionsModal && selectedUser && (
        <div className="modal-overlay" onClick={() => setShowPermissionsModal(false)}>
          <div className="modal-container" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h2 className="text-lg font-semibold text-pf-text-primary">
                Manage Application Access for {selectedUser.username}
              </h2>
              <button
                type="button"
                onClick={() => setShowPermissionsModal(false)}
                className="text-pf-text-secondary hover:text-pf-text-primary transition"
              >
                ×
              </button>
            </div>
            <div className="modal-body space-y-4">
              <p className="text-sm text-pf-text-secondary">
                Select which areas of the application this user can access:
              </p>
              <div className="space-y-3 bg-pf-bg-0 p-3 rounded border border-pf-border">
                {applicationAreas.map(area => {
                  const userRole = selectedUser.roles[0];
                  const isAdmin = userRole === 'farm_admin';
                  const isDisabled = isAdmin; // Admins always have full access
                  const hasAccess = selectedUser.permissions?.includes(area.id) ?? false;
                  
                  return (
                    <label key={area.id} className="flex items-start gap-2 p-2 hover:bg-pf-bg-1 rounded cursor-pointer transition">
                      <input
                        type="checkbox"
                        checked={hasAccess}
                        onChange={() => {
                          if (!isDisabled) {
                            const updatedPermissions = hasAccess
                              ? selectedUser.permissions.filter(id => id !== area.id)
                              : [...selectedUser.permissions, area.id];
                            setSelectedUser({
                              ...selectedUser,
                              permissions: updatedPermissions
                            });
                          }
                        }}
                        disabled={isDisabled}
                        className="mt-0.5 cursor-pointer"
                      />
                      <div className="flex-1">
                        <div className="text-sm font-medium text-pf-text-primary">{area.name}</div>
                        <div className="text-xs text-pf-text-secondary">{area.description}</div>
                        {isDisabled && <div className="text-xs text-pf-warning-text mt-0.5">Included with admin role</div>}
                      </div>
                    </label>
                  );
                })}
              </div>
            </div>
            <div className="modal-footer">
              <button
                type="button"
                onClick={() => setShowPermissionsModal(false)}
                className="btn-base btn-md btn-secondary"
              >
                Close
              </button>
              <button
                type="button"
                onClick={async () => {
                  try {
                    const response = await fetch(
                      `${getApiBaseUrl()}/users/${selectedUser.id}`,
                      {
                        method: 'PUT',
                        headers: {
                          'Content-Type': 'application/json',
                          ...getAuthHeaders()
                        },
                        body: JSON.stringify({
                          accessibleAreas: selectedUser.permissions
                        })
                      }
                    );
                    if (!response.ok) throw new Error('Failed to update permissions');
                    toast.success('Permissions updated');
                    setShowPermissionsModal(false);
                    loadUsers();
                  } catch (error) {
                    toast.error('Failed to update permissions');
                  }
                }}
                className="btn-base btn-md btn-primary"
              >
                Save Permissions
              </button>
            </div>
          </div>
        </div>
      )}
      </div>
    </PageTemplate>
  );
}