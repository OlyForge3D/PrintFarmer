import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Spinner } from '@/common/components/ui';
import { PlusIcon, PrinterIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { PrinterGroupCard } from '../components/PrinterGroupCard';
import { PrinterGroupModal } from '../components/PrinterGroupModal';
import { PrinterGroupDetail } from '../components/PrinterGroupDetail';
import { DeleteConfirmationModal } from '@/common/components/modals/DeleteConfirmationModal';
import type { PrinterGroup } from '@/types/api';

export function PrinterGroupsPage() {
  const queryClient = useQueryClient();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editGroup, setEditGroup] = useState<PrinterGroup | null>(null);
  const [deleteGroup, setDeleteGroup] = useState<PrinterGroup | null>(null);
  const [selectedGroupId, setSelectedGroupId] = useState<string | null>(null);

  const { data: groups = [], isLoading, error } = useQuery({
    queryKey: ['printer-groups'],
    queryFn: () => apiClient.getPrinterGroups(),
    staleTime: 30_000,
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deletePrinterGroup(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      toast.success('Group deleted');
      setDeleteGroup(null);
      // If we're viewing the deleted group, go back to list
      if (selectedGroupId === deleteGroup?.id) {
        setSelectedGroupId(null);
      }
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to delete group: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const handleCreate = () => {
    setEditGroup(null);
    setIsModalOpen(true);
  };

  const handleEdit = (group: PrinterGroup) => {
    setEditGroup(group);
    setIsModalOpen(true);
  };

  const handleDelete = (group: PrinterGroup) => {
    setDeleteGroup(group);
  };

  const handleConfirmDelete = () => {
    if (deleteGroup) {
      deleteMutation.mutate(deleteGroup.id);
    }
  };

  const handleSelect = (group: PrinterGroup) => {
    setSelectedGroupId(group.id);
  };

  const handleBack = () => {
    setSelectedGroupId(null);
  };

  // If a group is selected, show detail view
  if (selectedGroupId) {
    return (
      <PageTemplate title="Printer Groups" icon={PrinterIcon}>
        <PrinterGroupDetail
          groupId={selectedGroupId}
          onBack={handleBack}
          onEdit={handleEdit}
          onDelete={handleDelete}
        />
        <PrinterGroupModal isOpen={isModalOpen} onClose={() => setIsModalOpen(false)} editGroup={editGroup} />
        <DeleteConfirmationModal
          isOpen={!!deleteGroup}
          onClose={() => setDeleteGroup(null)}
          onConfirm={handleConfirmDelete}
          title="Delete Printer Group"
          message={`Are you sure you want to delete "${deleteGroup?.name}"? This will not delete the printers, only unassign them from this group.`}
          confirmText="Delete"
          isDeleting={deleteMutation.isPending}
        />
      </PageTemplate>
    );
  }

  // List view
  return (
    <PageTemplate
      title="Printer Groups"
      subtitle="Organize printers into groups for easier management"
      icon={PrinterIcon}
      actions={
        <Button variant="primary" onClick={handleCreate} iconLeft={<PlusIcon />}>
          Create Group
        </Button>
      }
    >
      {isLoading ? (
        <div className="flex items-center justify-center min-h-[40vh]">
          <Spinner size="lg" />
        </div>
      ) : error ? (
        <div className="p-4 text-pf-error">Failed to load groups: {String(error)}</div>
      ) : groups.length === 0 ? (
        <div className="text-center py-12">
          <PrinterIcon className="w-16 h-16 mx-auto text-pf-text-tertiary mb-4" />
          <h3 className="text-lg font-semibold text-pf-text-primary mb-2">No printer groups yet</h3>
          <p className="text-sm text-pf-text-secondary mb-4">
            Create groups to organize your printers and simplify management
          </p>
          <Button variant="primary" onClick={handleCreate} iconLeft={<PlusIcon />}>
            Create First Group
          </Button>
        </div>
      ) : (
        <div className="grid gap-4 grid-cols-1 md:grid-cols-2 lg:grid-cols-3">
          {groups.map((group) => (
            <PrinterGroupCard
              key={group.id}
              group={group}
              onEdit={handleEdit}
              onDelete={handleDelete}
              onSelect={handleSelect}
            />
          ))}
        </div>
      )}

      <PrinterGroupModal isOpen={isModalOpen} onClose={() => setIsModalOpen(false)} editGroup={editGroup} />
      <DeleteConfirmationModal
        isOpen={!!deleteGroup}
        onClose={() => setDeleteGroup(null)}
        onConfirm={handleConfirmDelete}
        title="Delete Printer Group"
        message={`Are you sure you want to delete "${deleteGroup?.name}"? This will not delete the printers, only unassign them from this group.`}
        confirmText="Delete"
        isDeleting={deleteMutation.isPending}
      />
    </PageTemplate>
  );
}
