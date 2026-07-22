import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Select, FormField, Badge, Spinner } from '@/common/components/ui';
import { EmptyState } from '@/common/components/ui';
import { PlusIcon, DeleteIcon, ShieldIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { PrinterGroupAccessRule, SetAccessRuleItem, RoleDto } from '@/types/api';
import { PrinterGroupAccessLevel } from '@/types/api';

interface AccessControlTabProps {
  groupId: string;
}

const ACCESS_LEVEL_OPTIONS: { value: PrinterGroupAccessLevel; label: string }[] = [
  { value: PrinterGroupAccessLevel.View, label: 'View' },
  { value: PrinterGroupAccessLevel.Submit, label: 'Submit' },
  { value: PrinterGroupAccessLevel.Manage, label: 'Manage' },
];

function accessLevelBadgeVariant(level: PrinterGroupAccessLevel) {
  switch (level) {
    case PrinterGroupAccessLevel.Manage:
      return 'error' as const;
    case PrinterGroupAccessLevel.Submit:
      return 'primary' as const;
    case PrinterGroupAccessLevel.View:
    default:
      return 'default' as const;
  }
}

export function AccessControlTab({ groupId }: AccessControlTabProps) {
  const queryClient = useQueryClient();
  const [selectedRoleId, setSelectedRoleId] = useState('');
  const [selectedLevel, setSelectedLevel] = useState<PrinterGroupAccessLevel>(
    PrinterGroupAccessLevel.Submit
  );

  const { data: rules = [], isLoading: rulesLoading } = useQuery({
    queryKey: ['printer-groups', groupId, 'access'],
    queryFn: () => apiClient.getPrinterGroupAccessRules(groupId),
    staleTime: 30_000,
  });

  const { data: roles = [], isLoading: rolesLoading } = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiClient.getRoles(),
    staleTime: 300_000,
  });

  const saveMutation = useMutation({
    mutationFn: (newRules: SetAccessRuleItem[]) =>
      apiClient.setPrinterGroupAccessRules(groupId, { rules: newRules }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups', groupId, 'access'] });
      toast.success('Access rules updated');
    },
    onError: (err: Error) => {
      toast.error(`Failed to update access rules: ${err.message}`);
    },
  });

  const handleAddRule = () => {
    if (!selectedRoleId) {
      toast.error('Please select a role');
      return;
    }

    const duplicate = rules.some(
      (r) => r.roleId === selectedRoleId && r.accessLevel === selectedLevel
    );
    if (duplicate) {
      toast.error('This role already has that access level');
      return;
    }

    const newRules: SetAccessRuleItem[] = [
      ...rules.map((r) => ({ roleId: r.roleId, accessLevel: r.accessLevel as PrinterGroupAccessLevel })),
      { roleId: selectedRoleId, accessLevel: selectedLevel },
    ];
    saveMutation.mutate(newRules);
    setSelectedRoleId('');
  };

  const handleRemoveRule = (rule: PrinterGroupAccessRule) => {
    const newRules: SetAccessRuleItem[] = rules
      .filter((r) => r.id !== rule.id)
      .map((r) => ({ roleId: r.roleId, accessLevel: r.accessLevel as PrinterGroupAccessLevel }));
    saveMutation.mutate(newRules);
  };

  if (rulesLoading || rolesLoading) {
    return (
      <div className="flex items-center justify-center py-8">
        <Spinner size="lg" />
      </div>
    );
  }

  const availableRoles = roles.filter((r: RoleDto) => r.isActive !== false);

  return (
    <div className="space-y-6">
      <div>
        <h3 className="text-lg font-semibold text-pf-text-primary mb-1">Access Control</h3>
        <p className="text-sm text-pf-text-secondary">
          Restrict which roles can interact with this printer group. When no rules are defined, the
          group is open to all authenticated users.
        </p>
      </div>

      <div className="flex items-end gap-3">
        <FormField label="Role" htmlFor="access-role">
          <Select
            id="access-role"
            value={selectedRoleId}
            onChange={(e) => setSelectedRoleId(e.target.value)}
          >
            <option value="">Select a role…</option>
            {availableRoles.map((role: RoleDto) => (
              <option key={role.id} value={role.id}>
                {role.displayName || role.name}
              </option>
            ))}
          </Select>
        </FormField>
        <FormField label="Access Level" htmlFor="access-level">
          <Select
            id="access-level"
            value={selectedLevel}
            onChange={(e) => setSelectedLevel(e.target.value as PrinterGroupAccessLevel)}
          >
            {ACCESS_LEVEL_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </Select>
        </FormField>
        <Button
          variant="primary"
          onClick={handleAddRule}
          loading={saveMutation.isPending}
          iconLeft={<PlusIcon />}
        >
          Add Rule
        </Button>
      </div>

      {rules.length === 0 ? (
        <EmptyState
          icon={<ShieldIcon className="w-12 h-12" />}
          title="No access rules"
          description="This group is open to all authenticated users. Add rules above to restrict access by role."
        />
      ) : (
        <div className="border border-pf-border rounded-lg overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-pf-bg-1 text-left text-pf-text-secondary">
                <th className="px-4 py-2 font-medium">Role</th>
                <th className="px-4 py-2 font-medium">Access Level</th>
                <th className="px-4 py-2 font-medium w-24 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {rules.map((rule) => (
                <tr key={rule.id} className="hover:bg-pf-bg-1/50">
                  <td className="px-4 py-2 text-pf-text-primary">{rule.roleName}</td>
                  <td className="px-4 py-2">
                    <Badge variant={accessLevelBadgeVariant(rule.accessLevel as PrinterGroupAccessLevel)}>
                      {rule.accessLevel}
                    </Badge>
                  </td>
                  <td className="px-4 py-2 text-right">
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => handleRemoveRule(rule)}
                      loading={saveMutation.isPending}
                      iconLeft={<DeleteIcon />}
                    >
                      Remove
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
