import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  AdminEmpty,
  AdminError,
  AdminLoading,
  AdminSaveBar,
  adminToast,
  useDirtyState,
} from '@/common/components/admin';
import {
  Badge,
  Button,
  Checkbox,
  FormField,
  Input,
  Select,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Textarea,
  Toggle,
} from '@/common/components/ui';
import {
  AlertIcon,
  CheckIcon,
  CloseIcon,
  CopyIcon,
  EditIcon,
  DeleteIcon,
  PlusIcon,
  RefreshIcon,
  ShieldIcon,
} from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { getErrorMessage, isApiError } from '@/common/utils/apiErrors';
import type {
  CreateCustomRoleRequest,
  PermissionCatalog,
  PermissionCatalogEntry,
  RoleDetail,
  RoleHasMembersError,
  RolePermissionEntry,
  RolePermissions,
  RoleSummary,
  UpdateCustomRoleRequest,
} from '@/types/api';

/** Immutable slug rule enforced server-side — mirrored here only as a fast client-side check. */
const ROLE_NAME_PATTERN = /^[a-z][a-z0-9_]{2,49}$/;

interface RoleFormValues {
  name: string;
  displayName: string;
  description: string;
  isActive: boolean;
  copyFromRoleId: string;
}

const EMPTY_ROLE_FORM: RoleFormValues = {
  name: '',
  displayName: '',
  description: '',
  isActive: true,
  copyFromRoleId: '',
};

function permissionKey(resource: string, action: string): string {
  return `${resource}:${action}`;
}

/** Flattens a role's fetched permission grants into a working `permission -> granted` map. */
function buildGrantMap(rolePermissions: RolePermissions): Record<string, boolean> {
  const map: Record<string, boolean> = {};
  for (const resource of rolePermissions.resources) {
    for (const entry of resource.permissions) {
      map[entry.permission] = entry.status === 'Granted';
    }
  }
  return map;
}

function buildCatalogIndex(catalog: PermissionCatalog | undefined): Map<string, PermissionCatalogEntry> {
  const index = new Map<string, PermissionCatalogEntry>();
  if (!catalog) return index;
  for (const resource of catalog.resources) {
    for (const entry of resource.permissions) {
      index.set(entry.permission, entry);
    }
  }
  return index;
}

/**
 * Role management admin page (#1455). Lets a `farm_admin` create, edit, and retire
 * custom roles, and view/toggle every enforced permission per role via a matrix.
 * Mounted embedded inside `SettingsShell` at `/admin/manage?tab=users&sub=roles`.
 */
export function RoleManagementPage({ embedded = false }: EmbeddablePageProps) {
  const queryClient = useQueryClient();
  const [selectedRoleId, setSelectedRoleId] = useState<string | null>(null);
  const [formMode, setFormMode] = useState<'create' | 'edit' | null>(null);
  const [formValues, setFormValues] = useState<RoleFormValues>(EMPTY_ROLE_FORM);
  const [formError, setFormError] = useState<string | null>(null);
  const [roleToDelete, setRoleToDelete] = useState<RoleSummary | null>(null);
  const [deleteConflict, setDeleteConflict] = useState<RoleHasMembersError | null>(null);
  const [reassignToId, setReassignToId] = useState('');
  const [cascadeDelete, setCascadeDelete] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [pendingSaveConfirm, setPendingSaveConfirm] = useState(false);
  const [concurrencyConflict, setConcurrencyConflict] = useState<string | null>(null);
  const [lockoutViolation, setLockoutViolation] = useState<{ message: string; permissions: string[] } | null>(null);
  const [pendingRoleSwitch, setPendingRoleSwitch] = useState<string | null>(null);

  const rolesQuery = useQuery<RoleSummary[]>({
    queryKey: ['admin-roles'],
    queryFn: () => apiClient.getAdminRoles(),
  });

  const catalogQuery = useQuery<PermissionCatalog>({
    queryKey: ['admin-permission-catalog'],
    queryFn: () => apiClient.getPermissionCatalog(),
  });

  const rolePermissionsQuery = useQuery<RolePermissions>({
    queryKey: ['admin-role-permissions', selectedRoleId],
    queryFn: () => apiClient.getRolePermissions(selectedRoleId as string),
    enabled: Boolean(selectedRoleId),
  });

  const catalogIndex = useMemo(() => buildCatalogIndex(catalogQuery.data), [catalogQuery.data]);

  const grantState = useDirtyState<Record<string, boolean>>({}, { guardUnload: true });

  // The hook does not auto-sync on prop changes (by design, so in-flight edits are
  // never silently discarded); reset the baseline explicitly whenever the selected
  // role's data settles. A background refetch of the SAME role (e.g. triggered by an
  // unrelated metadata edit invalidating this query) must not clobber unsaved matrix
  // edits, so only force-sync when the role actually changed; otherwise skip syncing
  // while the admin has unsaved changes.
  //
  // Skipping the sync means `rolePermissionsQuery.data` can advance to a newer
  // `updatedAt` (fetched in the background) while `grantState` is still built from an
  // older snapshot. If the save mutation read the concurrency token straight off the
  // live query data, it would silently send the *newer* token together with the
  // *stale* working values, masking a genuine conflict instead of tripping the 409
  // path. `syncedBaselineRef` pins the save's concurrency token to the exact snapshot
  // `grantState` was last synced from, so a save always reflects what the admin
  // actually saw and edited.
  const lastSyncedRoleIdRef = useRef<string | null>(null);
  const syncedBaselineRef = useRef<RolePermissions | null>(null);
  useEffect(() => {
    if (!rolePermissionsQuery.data) return;
    const isNewRole = lastSyncedRoleIdRef.current !== rolePermissionsQuery.data.roleId;
    if (isNewRole || !grantState.isDirty) {
      grantState.markPristine(buildGrantMap(rolePermissionsQuery.data));
      setConcurrencyConflict(null);
      setLockoutViolation(null);
      lastSyncedRoleIdRef.current = rolePermissionsQuery.data.roleId;
      syncedBaselineRef.current = rolePermissionsQuery.data;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rolePermissionsQuery.data]);

  // Switching the selected role while the permission matrix has unsaved edits would
  // otherwise silently discard those edits once the new role's data loads and
  // markPristine overwrites the working set. Guard the switch behind a confirmation
  // whenever there are unsaved changes.
  const requestSelectRole = (roleId: string) => {
    if (roleId === selectedRoleId) return;
    if (grantState.isDirty) {
      setPendingRoleSwitch(roleId);
      return;
    }
    setSelectedRoleId(roleId);
  };

  const confirmRoleSwitch = () => {
    if (pendingRoleSwitch) setSelectedRoleId(pendingRoleSwitch);
    setPendingRoleSwitch(null);
  };

  const roles = rolesQuery.data ?? [];
  const selectedRole = roles.find((r) => r.id === selectedRoleId) ?? null;
  const rolePermissions = rolePermissionsQuery.data;

  // ── Create / edit role ──────────────────────────────────────────────────

  const openCreateModal = () => {
    setFormValues(EMPTY_ROLE_FORM);
    setFormError(null);
    setFormMode('create');
  };

  const openCloneModal = (source: RoleSummary) => {
    setFormValues({ ...EMPTY_ROLE_FORM, copyFromRoleId: source.id });
    setFormError(null);
    setFormMode('create');
  };

  const openEditModal = (role: RoleSummary) => {
    setFormValues({
      name: role.name,
      displayName: role.displayName,
      description: role.description ?? '',
      isActive: role.isActive,
      copyFromRoleId: '',
    });
    setFormError(null);
    setFormMode('edit');
  };

  const closeFormModal = () => {
    setFormMode(null);
    setFormError(null);
  };

  const createRoleMutation = useMutation({
    mutationFn: (dto: CreateCustomRoleRequest) => apiClient.createAdminRole(dto),
    onSuccess: (role: RoleDetail) => {
      void queryClient.invalidateQueries({ queryKey: ['admin-roles'] });
      adminToast.success(`Role "${role.displayName}" created.`);
      setSelectedRoleId(role.id);
      closeFormModal();
    },
    onError: (error: unknown) => {
      setFormError(getErrorMessage(error, 'Could not create the role.'));
    },
  });

  const updateRoleMutation = useMutation({
    mutationFn: ({ roleId, dto }: { roleId: string; dto: UpdateCustomRoleRequest }) =>
      apiClient.updateAdminRole(roleId, dto),
    onSuccess: (role: RoleDetail) => {
      void queryClient.invalidateQueries({ queryKey: ['admin-roles'] });
      void queryClient.invalidateQueries({ queryKey: ['admin-role-permissions', role.id] });
      adminToast.success(`Role "${role.displayName}" updated.`);
      closeFormModal();
    },
    onError: (error: unknown) => {
      setFormError(getErrorMessage(error, 'Could not update the role.'));
    },
  });

  const handleSubmitForm = (event: React.FormEvent) => {
    event.preventDefault();
    setFormError(null);

    if (formMode === 'create') {
      const name = formValues.name.trim();
      if (!ROLE_NAME_PATTERN.test(name)) {
        setFormError('Name must be lowercase letters, numbers, and underscores, starting with a letter (3-50 characters).');
        return;
      }
      if (name.startsWith('farm_')) {
        setFormError('The "farm_" prefix is reserved for built-in system roles.');
        return;
      }
      createRoleMutation.mutate({
        name,
        displayName: formValues.displayName.trim(),
        description: formValues.description.trim() || undefined,
        copyFromRoleId: formValues.copyFromRoleId || undefined,
      });
      return;
    }

    if (formMode === 'edit' && selectedRole) {
      updateRoleMutation.mutate({
        roleId: selectedRole.id,
        dto: {
          displayName: formValues.displayName.trim(),
          description: formValues.description.trim() || undefined,
          isActive: formValues.isActive,
        },
      });
    }
  };

  // ── Delete role ──────────────────────────────────────────────────────────

  const deleteRoleMutation = useMutation({
    mutationFn: ({ roleId, reassignTo, cascade }: { roleId: string; reassignTo?: string; cascade?: boolean }) =>
      apiClient.deleteAdminRole(roleId, { reassignTo, cascade }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin-roles'] });
      adminToast.success('Role deleted.');
      if (selectedRoleId === roleToDelete?.id) setSelectedRoleId(null);
      setRoleToDelete(null);
      setDeleteConflict(null);
      setReassignToId('');
      setCascadeDelete(false);
      setDeleteError(null);
    },
    onError: (error: unknown) => {
      if (isApiError(error) && error.statusCode === 409 && error.data && 'memberCount' in (error.data as object)) {
        setDeleteConflict(error.data as RoleHasMembersError);
        return;
      }
      setDeleteError(getErrorMessage(error, 'Could not delete the role.'));
    },
  });

  const confirmDelete = () => {
    if (!roleToDelete) return;
    setDeleteError(null);
    deleteRoleMutation.mutate({ roleId: roleToDelete.id });
  };

  const confirmDeleteWithResolution = () => {
    if (!roleToDelete) return;
    setDeleteError(null);
    deleteRoleMutation.mutate({
      roleId: roleToDelete.id,
      reassignTo: !cascadeDelete && reassignToId ? reassignToId : undefined,
      cascade: cascadeDelete || undefined,
    });
  };

  const closeDeleteModal = () => {
    setRoleToDelete(null);
    setDeleteConflict(null);
    setReassignToId('');
    setCascadeDelete(false);
    setDeleteError(null);
  };

  // ── Permission matrix save ───────────────────────────────────────────────

  const savePermissionsMutation = useMutation({
    mutationFn: () => {
      // Use the pinned baseline the working set was synced from, not the live query
      // data — a background refetch of the same role may have advanced `updatedAt`
      // past what `grantState` was actually built from while edits were dirty (see the
      // sync effect above). Sending the live token would mask a genuine conflict.
      const baseline = syncedBaselineRef.current;
      if (!baseline) throw new Error('No role selected.');
      const permissions = Object.entries(grantState.values)
        .filter(([, granted]) => granted)
        .map(([permission]) => permission);
      return apiClient.updateRolePermissions(baseline.roleId, {
        updatedAt: baseline.updatedAt,
        permissions,
      });
    },
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: ['admin-roles'] });
      void queryClient.invalidateQueries({ queryKey: ['admin-role-permissions', selectedRoleId] });
      queryClient.setQueryData(['admin-role-permissions', selectedRoleId], result.role);
      grantState.markPristine(buildGrantMap(result.role));
      setPendingSaveConfirm(false);
      setConcurrencyConflict(null);
      setLockoutViolation(null);
      if (result.revokedSessionCount > 0) {
        adminToast.success(
          `Permissions saved. ${result.revokedSessionCount} active session(s) were signed out.`,
        );
      } else {
        adminToast.success('Permissions saved.');
      }
    },
    onError: (error: unknown) => {
      setPendingSaveConfirm(false);
      if (isApiError(error) && error.statusCode === 409 && error.data && 'permissions' in (error.data as object)) {
        const data = error.data as { error?: string; permissions: string[] };
        setLockoutViolation({ message: data.error ?? error.message, permissions: data.permissions });
        return;
      }
      if (isApiError(error) && error.statusCode === 409) {
        setConcurrencyConflict(error.message || 'This role was modified by another request. Reload and retry.');
        return;
      }
      adminToast.error(getErrorMessage(error, 'Could not save permission changes.'));
    },
  });

  const reloadPermissions = () => {
    setConcurrencyConflict(null);
    setLockoutViolation(null);
    // The admin explicitly asked to discard their stale working set and reload latest —
    // unlike the passive same-role background-refetch guard above, this must force a
    // resync even while dirty, or the working set (and its pinned concurrency token)
    // stays stale and the very next save just resends the same rejected token, looping
    // on 409 forever.
    void rolePermissionsQuery.refetch().then((result) => {
      if (!result.data) return;
      grantState.markPristine(buildGrantMap(result.data));
      lastSyncedRoleIdRef.current = result.data.roleId;
      syncedBaselineRef.current = result.data;
    });
  };

  // ── Rendering ─────────────────────────────────────────────────────────────

  const isLoading = rolesQuery.isLoading || catalogQuery.isLoading;
  const loadError = rolesQuery.error ?? catalogQuery.error;

  if (isLoading) {
    return (
      <PageTemplate
        title="Roles & Permissions"
        subtitle="Create custom roles and manage their permission grants."
        icon={ShieldIcon}
        embedded={embedded}
      >
        <AdminLoading variant="table" rows={6} cols={5} label="Loading roles" />
      </PageTemplate>
    );
  }

  if (loadError) {
    return (
      <PageTemplate
        title="Roles & Permissions"
        subtitle="Create custom roles and manage their permission grants."
        icon={ShieldIcon}
        embedded={embedded}
      >
        <AdminError
          title="Couldn't load roles"
          description="Try loading role management data again."
          error={loadError}
          onRetry={() => {
            void rolesQuery.refetch();
            void catalogQuery.refetch();
          }}
        />
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Roles & Permissions"
      subtitle="Create custom roles and manage their permission grants."
      icon={ShieldIcon}
      embedded={embedded}
      actions={
        <Button variant="primary" onClick={openCreateModal} iconLeft={<PlusIcon className="w-4 h-4" />}>
          New role
        </Button>
      }
    >
      <div className="grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)] gap-6">
        <div>
          {roles.length === 0 ? (
            <AdminEmpty
              icon={<ShieldIcon className="w-10 h-10" />}
              title="No roles yet"
              description="Create a custom role to get started."
              action={
                <Button variant="primary" onClick={openCreateModal} iconLeft={<PlusIcon className="w-4 h-4" />}>
                  New role
                </Button>
              }
            />
          ) : (
            <Table aria-label="Roles">
              <TableHead>
                <TableRow>
                  <TableHeaderCell>Role</TableHeaderCell>
                  <TableHeaderCell>Members</TableHeaderCell>
                  <TableHeaderCell>Permissions</TableHeaderCell>
                  <TableHeaderCell>Actions</TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {roles.map((role) => (
                  <TableRow
                    key={role.id}
                    isSelected={role.id === selectedRoleId}
                    isHoverable
                    onClick={() => requestSelectRole(role.id)}
                    className="cursor-pointer"
                  >
                    <TableCell>
                      <div className="flex flex-col gap-1">
                        <div className="flex items-center gap-2">
                          <span className="font-medium text-pf-text-primary">{role.displayName}</span>
                          {role.isSystemRole && (
                            <Badge variant="info" size="sm">System</Badge>
                          )}
                          {!role.isActive && (
                            <Badge variant="default" size="sm">
                              <CloseIcon className="w-3 h-3 mr-1" /> Inactive
                            </Badge>
                          )}
                        </div>
                        <span className="text-xs text-pf-text-secondary font-mono">{role.name}</span>
                      </div>
                    </TableCell>
                    <TableCell>{role.memberCount}</TableCell>
                    <TableCell>{role.permissionCount}</TableCell>
                    <TableCell>
                      <div className="flex items-center gap-1">
                        {!role.isSystemRole && (
                          <Button
                            variant="subtle"
                            size="sm"
                            aria-label={`Edit ${role.displayName}`}
                            onClick={(e) => {
                              e.stopPropagation();
                              openEditModal(role);
                            }}
                          >
                            <EditIcon className="w-4 h-4" />
                          </Button>
                        )}
                        <Button
                          variant="subtle"
                          size="sm"
                          aria-label={`Clone ${role.displayName}`}
                          onClick={(e) => {
                            e.stopPropagation();
                            openCloneModal(role);
                          }}
                        >
                          <CopyIcon className="w-4 h-4" />
                        </Button>
                        {!role.isSystemRole && (
                          <Button
                            variant="subtle"
                            size="sm"
                            aria-label={`Delete ${role.displayName}`}
                            onClick={(e) => {
                              e.stopPropagation();
                              setRoleToDelete(role);
                            }}
                          >
                            <DeleteIcon className="w-4 h-4" />
                          </Button>
                        )}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>

        <div className="rounded-md border border-pf-border bg-pf-bg-0">
          {!selectedRole ? (
            <AdminEmpty
              size="compact"
              icon={<ShieldIcon className="w-8 h-8" />}
              title="Select a role"
              description="Choose a role from the list to view and edit its permissions."
            />
          ) : rolePermissionsQuery.isLoading || !rolePermissions ? (
            <div className="p-4">
              <AdminLoading variant="table" rows={5} cols={2} label="Loading permissions" />
            </div>
          ) : rolePermissionsQuery.error ? (
            <div className="p-4">
              <AdminError
                title="Couldn't load permissions"
                error={rolePermissionsQuery.error}
                onRetry={() => void rolePermissionsQuery.refetch()}
              />
            </div>
          ) : (
            <PermissionMatrix
              rolePermissions={rolePermissions}
              catalogIndex={catalogIndex}
              grants={grantState.values}
              onToggle={(perm, granted) => grantState.setValue(perm, granted)}
              concurrencyConflict={concurrencyConflict}
              onReload={reloadPermissions}
              lockoutViolation={lockoutViolation}
            />
          )}

          {rolePermissions?.isEditable && (
            <AdminSaveBar
              isDirty={grantState.isDirty}
              changeCount={grantState.changedCount}
              onDiscard={grantState.reset}
              onSave={() => setPendingSaveConfirm(true)}
              isSaving={savePermissionsMutation.isPending}
            />
          )}
        </div>
      </div>

      {/* Create / edit modal */}
      <Modal
        isOpen={formMode !== null}
        onClose={closeFormModal}
        title={formMode === 'create' ? 'New role' : 'Edit role'}
        titleIcon={<ShieldIcon className="w-5 h-5" />}
        footer={
          <>
            <Button variant="secondary" onClick={closeFormModal}>
              Cancel
            </Button>
            <Button
              type="submit"
              form="role-form"
              variant="primary"
              loading={createRoleMutation.isPending || updateRoleMutation.isPending}
            >
              {formMode === 'create' ? 'Create role' : 'Save changes'}
            </Button>
          </>
        }
      >
        <form id="role-form" onSubmit={handleSubmitForm} className="flex flex-col gap-4">
          {formError && (
            <div role="alert" className="text-sm text-pf-error-text bg-pf-error-bg border border-pf-error/30 rounded-md p-3">
              {formError}
            </div>
          )}
          {formMode === 'create' && (
            <FormField label="Name" htmlFor="role-name" required helper="Lowercase, letters/numbers/underscores only. Cannot be changed later.">
              <Input
                id="role-name"
                value={formValues.name}
                onChange={(e) => setFormValues((v) => ({ ...v, name: e.target.value }))}
                required
                data-autofocus
              />
            </FormField>
          )}
          <FormField label="Display name" htmlFor="role-display-name" required>
            <Input
              id="role-display-name"
              value={formValues.displayName}
              onChange={(e) => setFormValues((v) => ({ ...v, displayName: e.target.value }))}
              required
            />
          </FormField>
          <FormField label="Description" htmlFor="role-description">
            <Textarea
              id="role-description"
              value={formValues.description}
              onChange={(e) => setFormValues((v) => ({ ...v, description: e.target.value }))}
              rows={3}
            />
          </FormField>
          {formMode === 'create' && (
            <FormField label="Clone permissions from" htmlFor="role-clone-from" helper="Optional. Starts this role with another role's current permission grants.">
              <Select
                id="role-clone-from"
                value={formValues.copyFromRoleId}
                onChange={(e) => setFormValues((v) => ({ ...v, copyFromRoleId: e.target.value }))}
              >
                <option value="">None — start with no permissions</option>
                {roles.map((role) => (
                  <option key={role.id} value={role.id}>{role.displayName}</option>
                ))}
              </Select>
            </FormField>
          )}
          {formMode === 'edit' && (
            <FormField label="Active">
              <Toggle
                label={formValues.isActive ? 'Active' : 'Inactive'}
                checked={formValues.isActive}
                onChange={(e) => setFormValues((v) => ({ ...v, isActive: e.target.checked }))}
              />
            </FormField>
          )}
        </form>
      </Modal>

      {/* Delete flow */}
      <ConfirmationModal
        isOpen={roleToDelete !== null && deleteConflict === null}
        title="Delete role"
        message={`Delete "${roleToDelete?.displayName}"? This cannot be undone.`}
        isDangerous
        isConfirming={deleteRoleMutation.isPending}
        onConfirm={confirmDelete}
        onCancel={closeDeleteModal}
      >
        {deleteError && (
          <div role="alert" className="text-sm text-pf-error-text bg-pf-error-bg border border-pf-error/30 rounded-md p-3">
            {deleteError}
          </div>
        )}
      </ConfirmationModal>

      <Modal
        isOpen={deleteConflict !== null}
        onClose={closeDeleteModal}
        title="Role has members"
        titleIcon={<AlertIcon className="w-6 h-6 text-pf-warning-text" />}
        footer={
          <>
            <Button variant="secondary" onClick={closeDeleteModal}>
              Cancel
            </Button>
            <Button
              variant="danger"
              onClick={confirmDeleteWithResolution}
              loading={deleteRoleMutation.isPending}
              disabled={!cascadeDelete && !reassignToId}
            >
              Delete role
            </Button>
          </>
        }
      >
        <p className="text-sm text-pf-text-secondary mb-4">
          {deleteConflict?.error ?? `This role still has ${deleteConflict?.memberCount ?? 0} member(s).`}
        </p>
        <div className="flex flex-col gap-3">
          <FormField label="Reassign members to" htmlFor="reassign-to">
            <Select
              id="reassign-to"
              value={reassignToId}
              disabled={cascadeDelete}
              onChange={(e) => {
                setReassignToId(e.target.value);
                if (e.target.value) setCascadeDelete(false);
              }}
            >
              <option value="">Choose a role…</option>
              {roles.filter((r) => r.id !== roleToDelete?.id).map((role) => (
                <option key={role.id} value={role.id}>{role.displayName}</option>
              ))}
            </Select>
          </FormField>
          <Checkbox
            label="Delete anyway and remove members from this role"
            checked={cascadeDelete}
            onChange={(e) => {
              setCascadeDelete(e.target.checked);
              if (e.target.checked) setReassignToId('');
            }}
          />
        </div>
        {deleteError && (
          <div role="alert" className="mt-3 text-sm text-pf-error-text bg-pf-error-bg border border-pf-error/30 rounded-md p-3">
            {deleteError}
          </div>
        )}
      </Modal>

      {/* Permission save confirmation */}
      <ConfirmationModal
        isOpen={pendingSaveConfirm}
        title="Save permission changes"
        message={
          selectedRole && selectedRole.memberCount > 0
            ? `This role currently has ${selectedRole.memberCount} member(s). Saving may sign out any of their active sessions once the new permissions take effect.`
            : 'Save these permission changes?'
        }
        confirmButtonText="Save changes"
        isConfirming={savePermissionsMutation.isPending}
        onConfirm={() => savePermissionsMutation.mutate()}
        onCancel={() => setPendingSaveConfirm(false)}
      />

      {/* Unsaved permission changes when switching roles */}
      <ConfirmationModal
        isOpen={pendingRoleSwitch !== null}
        title="Discard unsaved permission changes?"
        message="You have unsaved permission changes for this role. Switching roles will discard them."
        confirmButtonText="Discard changes"
        onConfirm={confirmRoleSwitch}
        onCancel={() => setPendingRoleSwitch(null)}
      />
    </PageTemplate>
  );
}

interface PermissionMatrixProps {
  rolePermissions: RolePermissions;
  catalogIndex: Map<string, PermissionCatalogEntry>;
  grants: Record<string, boolean>;
  onToggle: (permission: string, granted: boolean) => void;
  concurrencyConflict: string | null;
  onReload: () => void;
  lockoutViolation: { message: string; permissions: string[] } | null;
}

function PermissionMatrix({
  rolePermissions,
  catalogIndex,
  grants,
  onToggle,
  concurrencyConflict,
  onReload,
  lockoutViolation,
}: PermissionMatrixProps) {
  const readOnly = !rolePermissions.isEditable;
  const deniedCount = rolePermissions.resources
    .flatMap((resource) => resource.permissions)
    .filter((entry) => entry.status === 'Denied').length;

  return (
    <div className="p-4 flex flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-base font-semibold text-pf-text-primary">{rolePermissions.roleDisplayName}</h2>
          <p className="text-xs text-pf-text-secondary font-mono">{rolePermissions.roleName}</p>
        </div>
      </div>

      {readOnly && (
        <div className="text-sm text-pf-text-secondary bg-pf-bg-1 border border-pf-border rounded-md p-3">
          The <span className="font-mono">farm_admin</span> role has implicit total access to every
          permission and cannot be edited.
        </div>
      )}

      {!readOnly && deniedCount > 0 && (
        <div className="text-sm text-pf-warning-text bg-pf-warning-bg border border-pf-warning/30 rounded-md p-3">
          {deniedCount} permission{deniedCount === 1 ? ' is' : 's are'} explicitly denied for this role
          (marked &ldquo;Denied&rdquo; below). Saving any change here replaces the full grant set, so those
          explicit denies will reset to not-granted &mdash; they cannot be re-applied through this page.
        </div>
      )}

      {concurrencyConflict && (
        <div role="alert" className="flex items-center justify-between gap-3 text-sm text-pf-warning-text bg-pf-warning-bg border border-pf-warning/30 rounded-md p-3">
          <span>{concurrencyConflict}</span>
          <Button variant="secondary" size="sm" onClick={onReload} iconLeft={<RefreshIcon className="w-3.5 h-3.5" />}>
            Reload latest
          </Button>
        </div>
      )}

      {lockoutViolation && (
        <div role="alert" className="text-sm text-pf-error-text bg-pf-error-bg border border-pf-error/30 rounded-md p-3">
          <p>{lockoutViolation.message}</p>
          {lockoutViolation.permissions.length > 0 && (
            <ul className="list-disc list-inside mt-1 font-mono text-xs">
              {lockoutViolation.permissions.map((permission) => (
                <li key={permission}>{permission}</li>
              ))}
            </ul>
          )}
        </div>
      )}

      {rolePermissions.resources.map((resource) => (
        <div key={resource.resource} className="rounded-md border border-pf-border overflow-hidden">
          <div className="bg-pf-bg-1 px-4 py-2">
            <h3 className="text-sm font-semibold text-pf-text-primary">
              {resource.displayName ?? resource.resource}
            </h3>
            {resource.description && (
              <p className="text-xs text-pf-text-secondary">{resource.description}</p>
            )}
          </div>
          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>Permission</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Grant</TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {resource.permissions.map((entry) => (
                <PermissionRow
                  key={entry.permission}
                  entry={entry}
                  catalogEntry={catalogIndex.get(entry.permission)}
                  granted={grants[entry.permission] ?? false}
                  adminGranted={grants[permissionKey(resource.resource, 'admin')] ?? false}
                  readOnly={readOnly}
                  onToggle={(granted) => onToggle(entry.permission, granted)}
                />
              ))}
            </TableBody>
          </Table>
        </div>
      ))}
    </div>
  );
}

interface PermissionRowProps {
  entry: RolePermissionEntry;
  catalogEntry: PermissionCatalogEntry | undefined;
  granted: boolean;
  adminGranted: boolean;
  readOnly: boolean;
  onToggle: (granted: boolean) => void;
}

function PermissionRow({ entry, catalogEntry, granted, adminGranted, readOnly, onToggle }: PermissionRowProps) {
  const isAdminAction = entry.action === 'admin';
  const impliedByGrantedAdmin = entry.impliedByAdmin && adminGranted && !granted;
  const routes = catalogEntry?.routes ?? [];

  return (
    <TableRow>
      <TableCell>
        <div className="flex flex-col gap-1">
          <div className="flex items-center gap-2">
            <span className={isAdminAction ? 'font-semibold text-pf-text-primary' : 'text-pf-text-primary'}>
              {entry.actionDisplayName ?? entry.action}
            </span>
            {isAdminAction && (
              <Badge variant="primary" size="sm">Includes every other permission on this resource</Badge>
            )}
            {impliedByGrantedAdmin && (
              <Badge variant="default" size="sm">via admin</Badge>
            )}
          </div>
          {entry.actionDescription && (
            <span className="text-xs text-pf-text-secondary">{entry.actionDescription}</span>
          )}
          {routes.length > 0 && (
            <details className="text-xs text-pf-text-secondary">
              <summary className="cursor-pointer select-none">
                Unlocks {routes.length} route{routes.length === 1 ? '' : 's'}
              </summary>
              <ul className="mt-1 pl-4 list-disc font-mono">
                {routes.map((route) => (
                  <li key={`${route.method} ${route.template}`}>
                    {route.method} {route.template}
                  </li>
                ))}
              </ul>
            </details>
          )}
        </div>
      </TableCell>
      <TableCell>
        {entry.status === 'Denied' ? (
          <Badge variant="error" size="sm">
            <CloseIcon className="w-3 h-3 mr-1" /> Denied
          </Badge>
        ) : granted ? (
          <Badge variant="success" size="sm">
            <CheckIcon className="w-3 h-3 mr-1" /> Granted
          </Badge>
        ) : (
          <Badge variant="default" size="sm">Not granted</Badge>
        )}
      </TableCell>
      <TableCell>
        <Toggle
          id={`toggle-${entry.permission}`}
          label={`Grant ${entry.actionDisplayName ?? entry.action}`}
          checked={granted}
          disabled={readOnly}
          onChange={(e) => onToggle(e.target.checked)}
        />
      </TableCell>
    </TableRow>
  );
}

export default RoleManagementPage;
