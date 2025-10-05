import React, { useState, useEffect } from 'react';
import { usePasswordPolicy } from '@/hooks/usePasswordPolicy';
import { toast } from 'sonner';
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
  const { data: passwordPolicy } = usePasswordPolicy();
  const [newUser, setNewUser] = useState({ username: '', email: '', password: '', firstName: '', lastName: '' });
  const [creating, setCreating] = useState(false);
  const [selectedRoleIds, setSelectedRoleIds] = useState<string[]>([]);
  const [createErrors, setCreateErrors] = useState<{username?: string; email?: string; password?: string; general?: string; roles?: string}>({});
  type AvailabilityStatus = 'idle' | 'checking' | 'available' | 'taken' | 'error';
  const [usernameStatus, setUsernameStatus] = useState<AvailabilityStatus>('idle');
  const [emailStatus, setEmailStatus] = useState<AvailabilityStatus>('idle');
  const [, setAvailabilityMessage] = useState('');
  const DEBOUNCE_MS = 450;

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
        const res = await fetch(`/api/users/availability?${params.toString()}`, { signal: ctrl.signal });
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
      const token = localStorage.getItem('auth-token');

      const response = await fetch('/api/users', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          username: newUser.username.trim(),
          email: newUser.email.trim(),
          password: newUser.password,
          firstName: newUser.firstName.trim() || undefined,
          lastName: newUser.lastName.trim() || undefined,
          roleIds: selectedRoleIds
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
      setSelectedRoleIds([]);
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
  }, []);

  const loadUsers = async () => {
    try {
      const token = localStorage.getItem('auth-token');
      const response = await fetch('/api/users', {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
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
      const token = localStorage.getItem('auth-token');
      const response = await fetch('/api/users/roles', {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
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
      <div className="p-6" aria-busy="true" aria-live="polite" aria-label="Loading users">
        <h1 className="text-2xl font-bold text-pf-text-primary mb-4 flex items-center">
          <Users className="h-6 w-6 mr-2" aria-hidden="true" />
          User Management
        </h1>
        <TableSkeleton rows={6} cols={6} />
      </div>
    );
  }

  return (
    <div className="p-6">
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-pf-text-primary mb-2 flex items-center">
          <Users className="h-6 w-6 mr-2" />
          User Management
        </h1>
        <p className="text-pf-text-secondary">
          Manage user accounts, roles, and permissions for PrintFarmer.
        </p>
      </div>
      {/* Controls */}
      <div className="mb-6 flex flex-col sm:flex-row gap-4 justify-between">
        <div className="relative">
          <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 h-4 w-4 text-pf-text-tertiary" />
          <input
            type="text"
            placeholder="Search users..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="pl-10 pr-4 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
          />
        </div>
        <button
          onClick={() => {
            const farmUserRole = roles.find(r => r.name === 'farm_user');
            setSelectedRoleIds(farmUserRole ? [farmUserRole.id] : []);
            setCreateErrors({});
            setNewUser({ username: '', email: '', password: '', firstName: '', lastName: '' });
            setShowCreateModal(true);
          }}
          className="px-4 py-2 bg-pf-accent text-white rounded-md hover:bg-pf-accent-dark focus:outline-none focus:ring-2 focus:ring-pf-accent flex items-center"
        >
          <Plus className="h-4 w-4 mr-2" />
          Add User
        </button>
      </div>
      {/* Users Table */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-lg overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead className="bg-pf-bg-2 border-b border-pf-border">
              <tr>
                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">User</th>
                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">Roles</th>
                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">Status</th>
                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">Last Login</th>
                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">Created</th>
                <th className="px-6 py-3 text-right text-sm font-medium text-pf-text-primary">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
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
      {/* User count */}
      <div className="mt-4 text-sm text-pf-text-secondary">
        Showing {filteredUsers.length} of {users.length} users
      </div>
      {/* TODO: Modals for create/edit users */}
      {showCreateModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
          <div className="bg-pf-bg-1 rounded-lg p-6 max-w-lg w-full mx-4">
            <h3 className="text-lg font-semibold mb-4">Create New User</h3>
            {createErrors.general && (
              <div className="mb-4 p-2 rounded bg-red-50 text-red-600 text-sm" role="alert">
                {createErrors.general}
              </div>
            )}
            <div className="space-y-4">
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
                <div className="flex items-center justify-between mb-1">
                  <span className="block text-sm font-medium">Roles</span>
                  {roles.length > 0 && (
                    <div className="flex gap-2 text-xs">
                      <button
                        type="button"
                        onClick={() => setSelectedRoleIds(roles.map(r => r.id))}
                        className="text-pf-accent hover:underline"
                      >Select All</button>
                      <span className="text-pf-text-tertiary">|</span>
                      <button
                        type="button"
                        onClick={() => setSelectedRoleIds([])}
                        className="text-pf-accent hover:underline"
                      >Clear</button>
                    </div>
                  )}
                </div>
                <div className="flex flex-wrap gap-2" role="group" aria-label="Assign roles">
                  {roles.map(role => (
                    <label key={role.id} className="inline-flex items-center space-x-1 bg-pf-bg-0 border border-pf-border rounded px-2 py-1 text-xs cursor-pointer select-none">
                      <input
                        type="checkbox"
                        className="accent-pf-accent"
                        checked={selectedRoleIds.includes(role.id)}
                        onChange={() => setSelectedRoleIds(prev => prev.includes(role.id) ? prev.filter(id => id !== role.id) : [...prev, role.id])}
                      />
                      <span>{role.displayName}</span>
                    </label>
                  ))}
                  {roles.length === 0 && (
                    <span className="text-xs text-pf-text-tertiary">No roles available</span>
                  )}
                </div>
                {createErrors.roles && <p className="text-xs text-red-500 mt-1" role="alert">{createErrors.roles}</p>}
              </div>
            </div>
            <div className="mt-6 flex justify-end gap-2">
              <button
                onClick={() => setShowCreateModal(false)}
                className="px-4 py-2 bg-gray-600 text-white rounded-md"
              >Cancel</button>
                <button
                  onClick={createUser}
                  disabled={creating || !newUser.username || !newUser.email || !passwordMeetsPolicy() || usernameStatus === 'taken' || emailStatus === 'taken'}
                  className="px-4 py-2 bg-pf-accent text-white rounded-md disabled:opacity-50 flex items-center gap-2"
                >{creating && (
                  <svg className="animate-spin h-4 w-4 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"></path>
                  </svg>
                )}Create</button>
            </div>
          </div>
        </div>
      )}
      {showEditModal && selectedUser && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
          <div className="bg-pf-bg-1 rounded-lg p-6 max-w-lg w-full mx-4">
            <h3 className="text-lg font-semibold mb-4">Edit User: {selectedUser.username}</h3>
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                try {
                  const token = localStorage.getItem('auth-token');
                  const response = await fetch(`/api/users/${selectedUser.id}`, {
                    method: 'PUT',
                    headers: {
                      'Authorization': `Bearer ${token}`,
                      'Content-Type': 'application/json'
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
                <label className="block text-sm font-medium mb-1">Roles</label>
                <div className="flex flex-wrap gap-2">
                  {roles.map(role => (
                    <label key={role.id} className="flex items-center gap-1">
                      <input
                        type="checkbox"
                        checked={selectedUser.roles.includes(role.name)}
                        onChange={e => {
                          setSelectedUser(u => {
                            if (!u) return u;
                            // removed unused hasRole variable
                            return {
                              ...u,
                              roles: e.target.checked
                                ? [...u.roles, role.name]
                                : u.roles.filter(r => r !== role.name)
                            };
                          });
                        }}
                      />
                      <span>{role.displayName}</span>
                    </label>
                  ))}
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Permissions</label>
                <div className="flex flex-wrap gap-2">
                  {selectedUser.permissions.length > 0 ? (
                    selectedUser.permissions.map(p => (
                      <span key={p} className="inline-block bg-pf-bg-2 px-2 py-1 rounded text-xs">{p}</span>
                    ))
                  ) : (
                    <span className="text-pf-text-tertiary text-xs">No permissions</span>
                  )}
                </div>
              </div>
              <div className="flex justify-end gap-2 mt-6">
                <button
                  type="button"
                  onClick={() => {
                    setShowEditModal(false);
                    setSelectedUser(null);
                  }}
                  className="px-4 py-2 bg-pf-border text-pf-text-primary rounded-md"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  className="px-4 py-2 bg-pf-accent text-white rounded-md"
                >
                  Save
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}