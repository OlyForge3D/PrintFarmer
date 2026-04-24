import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Card, Badge, Spinner, Input, Select, FormField } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { PlusIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import type {
  QuotaDto,
  CreateQuotaRequest,
  QuotaType,
  QuotaPeriodType,
} from '@/types/api';

const QUOTA_TYPES: QuotaType[] = ['Cost', 'Count', 'Weight'];
const PERIOD_TYPES: QuotaPeriodType[] = ['Daily', 'Weekly', 'Monthly', 'Semester', 'Manual'];

function quotaTypeLabel(t: QuotaType): string {
  return t === 'Cost' ? 'Cost ($)' : t === 'Weight' ? 'Weight (g)' : 'Job Count';
}

function periodBadgeVariant(p: QuotaPeriodType): 'default' | 'primary' | 'success' | 'warning' | 'error' {
  switch (p) {
    case 'Daily': return 'primary';
    case 'Weekly': return 'success';
    case 'Monthly': return 'default';
    case 'Semester': return 'warning';
    case 'Manual': return 'error';
  }
}

export function QuotaManagementPage() {
  const queryClient = useQueryClient();
  const [showCreate, setShowCreate] = useState(false);

  const { data: quotas = [], isLoading, error } = useQuery({
    queryKey: ['quotas'],
    queryFn: () => apiClient.getQuotas(),
    staleTime: 30_000,
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteQuota(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quotas'] });
      toast.success('Quota deleted');
    },
    onError: (err: Error) => toast.error(`Failed to delete: ${err.message}`),
  });

  const resetMutation = useMutation({
    mutationFn: () => apiClient.resetExpiredQuotas(),
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ['quotas'] });
      toast.success(`Reset ${result.resetCount} expired quota(s)`);
    },
    onError: (err: Error) => toast.error(`Failed to reset: ${err.message}`),
  });

  if (isLoading) {
    return (
      <PageTemplate title="Print Quotas">
        <Spinner size="lg" />
      </PageTemplate>
    );
  }

  if (error) {
    return (
      <PageTemplate title="Print Quotas">
        <div className="p-4 text-pf-error">Failed to load quotas: {String(error)}</div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Print Quotas"
      subtitle="Manage per-user and per-group print limits"
      actions={
        <div className="flex gap-2">
          <Button variant="secondary" onClick={() => resetMutation.mutate()} loading={resetMutation.isPending}>
            Reset Expired
          </Button>
          <Button variant="primary" iconLeft={<PlusIcon />} onClick={() => setShowCreate(true)}>
            New Quota
          </Button>
        </div>
      }
    >
      {quotas.length === 0 ? (
        <Card>
          <Card.Body>
            <p className="text-pf-text-secondary text-center py-8">No quotas configured yet. Click &quot;New Quota&quot; to create one.</p>
          </Card.Body>
        </Card>
      ) : (
        <div className="grid gap-3">
          {quotas.map((q: QuotaDto) => (
            <Card key={q.id}>
              <Card.Body className="flex items-center justify-between gap-4">
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 mb-1">
                    <span className="font-medium text-pf-text-primary">
                      {q.userId ? `User: ${q.userId.slice(0, 8)}…` : `Group: ${q.groupName}`}
                    </span>
                    <Badge variant={q.isActive ? 'success' : 'default'} size="sm">
                      {q.isActive ? 'Active' : 'Inactive'}
                    </Badge>
                    <Badge variant={periodBadgeVariant(q.periodType)} size="sm">
                      {q.periodType}
                    </Badge>
                  </div>
                  <div className="text-sm text-pf-text-secondary">
                    {quotaTypeLabel(q.quotaType)}: {q.usedAmount.toFixed(2)} / {q.limitAmount.toFixed(2)}
                    {q.resetAt && ` · Resets ${new Date(q.resetAt).toLocaleDateString()}`}
                  </div>
                  {q.notes && <div className="text-xs text-pf-text-secondary mt-1">{q.notes}</div>}
                </div>
                <div className="flex-shrink-0">
                  <div className="w-32 bg-pf-bg-1 rounded-full h-2 mb-2">
                    <div
                      className="bg-pf-accent rounded-full h-2 transition-all"
                      style={{ width: `${Math.min((q.usedAmount / q.limitAmount) * 100, 100)}%` }}
                    />
                  </div>
                  <div className="text-xs text-right text-pf-text-secondary">
                    {((q.usedAmount / q.limitAmount) * 100).toFixed(0)}% used
                  </div>
                </div>
                <Button
                  variant="danger"
                  size="sm"
                  iconLeft={<DeleteIcon />}
                  onClick={() => deleteMutation.mutate(q.id)}
                  loading={deleteMutation.isPending}
                >
                  Delete
                </Button>
              </Card.Body>
            </Card>
          ))}
        </div>
      )}

      <CreateQuotaModal
        isOpen={showCreate}
        onClose={() => setShowCreate(false)}
      />
    </PageTemplate>
  );
}

function CreateQuotaModal({ isOpen, onClose }: { isOpen: boolean; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [targetType, setTargetType] = useState<'user' | 'group'>('user');
  const [userId, setUserId] = useState('');
  const [groupName, setGroupName] = useState('');
  const [quotaType, setQuotaType] = useState<QuotaType>('Cost');
  const [limitAmount, setLimitAmount] = useState('');
  const [periodType, setPeriodType] = useState<QuotaPeriodType>('Monthly');
  const [notes, setNotes] = useState('');

  const createMutation = useMutation({
    mutationFn: (req: CreateQuotaRequest) => apiClient.createQuota(req),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['quotas'] });
      toast.success('Quota created');
      resetForm();
      onClose();
    },
    onError: (err: Error) => toast.error(`Failed to create: ${err.message}`),
  });

  function resetForm() {
    setTargetType('user');
    setUserId('');
    setGroupName('');
    setQuotaType('Cost');
    setLimitAmount('');
    setPeriodType('Monthly');
    setNotes('');
  }

  function handleSubmit() {
    const limit = parseFloat(limitAmount);
    if (isNaN(limit) || limit <= 0) {
      toast.error('Limit must be a positive number');
      return;
    }

    if (targetType === 'user' && !userId.trim()) {
      toast.error('User ID is required');
      return;
    }

    if (targetType === 'group' && !groupName.trim()) {
      toast.error('Group name is required');
      return;
    }

    createMutation.mutate({
      userId: targetType === 'user' ? userId.trim() : undefined,
      groupName: targetType === 'group' ? groupName.trim() : undefined,
      quotaType,
      limitAmount: limit,
      periodType,
      notes: notes.trim() || undefined,
    });
  }

  const footer = (
    <div className="flex justify-end gap-2">
      <Button variant="secondary" onClick={onClose}>Cancel</Button>
      <Button variant="primary" onClick={handleSubmit} loading={createMutation.isPending}>Create Quota</Button>
    </div>
  );

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Create Quota" size="md" footer={footer}>
      <div className="space-y-4">
        <FormField label="Target Type" htmlFor="target-type">
          <Select
            id="target-type"
            value={targetType}
            onChange={(e) => setTargetType(e.target.value as 'user' | 'group')}
          >
            <option value="user">User</option>
            <option value="group">Group</option>
          </Select>
        </FormField>

        {targetType === 'user' ? (
          <FormField label="User ID" htmlFor="user-id" required>
            <Input id="user-id" value={userId} onChange={(e) => setUserId(e.target.value)} placeholder="User GUID" />
          </FormField>
        ) : (
          <FormField label="Group Name" htmlFor="group-name" required>
            <Input id="group-name" value={groupName} onChange={(e) => setGroupName(e.target.value)} placeholder="e.g. students" />
          </FormField>
        )}

        <FormField label="Quota Type" htmlFor="quota-type">
          <Select id="quota-type" value={quotaType} onChange={(e) => setQuotaType(e.target.value as QuotaType)}>
            {QUOTA_TYPES.map((t) => <option key={t} value={t}>{quotaTypeLabel(t)}</option>)}
          </Select>
        </FormField>

        <FormField label="Limit" htmlFor="limit-amount" required>
          <Input id="limit-amount" type="number" min="0" step="0.01" value={limitAmount} onChange={(e) => setLimitAmount(e.target.value)} placeholder="e.g. 100" />
        </FormField>

        <FormField label="Period" htmlFor="period-type">
          <Select id="period-type" value={periodType} onChange={(e) => setPeriodType(e.target.value as QuotaPeriodType)}>
            {PERIOD_TYPES.map((p) => <option key={p} value={p}>{p}</option>)}
          </Select>
        </FormField>

        <FormField label="Notes" htmlFor="notes">
          <Input id="notes" value={notes} onChange={(e) => setNotes(e.target.value)} placeholder="Optional notes" />
        </FormField>
      </div>
    </Modal>
  );
}
