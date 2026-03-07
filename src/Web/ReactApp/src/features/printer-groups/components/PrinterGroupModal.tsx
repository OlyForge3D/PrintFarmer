import { useState, useEffect } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Textarea, FormField } from '@/common/components/ui';
import { toast } from 'sonner';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { PrinterGroup, CreatePrinterGroupRequest, UpdatePrinterGroupRequest } from '@/types/api';

interface PrinterGroupModalProps {
  isOpen: boolean;
  onClose: () => void;
  editGroup?: PrinterGroup | null;
}

export function PrinterGroupModal({ isOpen, onClose, editGroup }: PrinterGroupModalProps) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [nameError, setNameError] = useState('');

  // Reset form when modal opens with new data
  useEffect(() => {
    if (isOpen) {
      if (editGroup) {
        // eslint-disable-next-line react-hooks/set-state-in-effect
        setName(editGroup.name);
         
        setDescription(editGroup.description || '');
      } else {
         
        setName('');
         
        setDescription('');
      }
       
      setNameError('');
    }
  }, [isOpen, editGroup]);

  const createMutation = useMutation({
    mutationFn: (dto: CreatePrinterGroupRequest) => apiClient.createPrinterGroup(dto),
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      toast.success(`Group "${created.name}" created`);
      onClose();
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to create group: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdatePrinterGroupRequest }) =>
      apiClient.updatePrinterGroup(id, dto),
    onSuccess: (updated) => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      queryClient.invalidateQueries({ queryKey: ['printer-groups', editGroup?.id] });
      toast.success(`Group "${updated.name}" updated`);
      onClose();
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to update group: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const handleSubmit = () => {
    // Validate name
    if (!name.trim()) {
      setNameError('Name is required');
      return;
    }
    setNameError('');

    const dto = {
      name: name.trim(),
      description: description.trim() || undefined,
    };

    if (editGroup) {
      updateMutation.mutate({ id: editGroup.id, dto });
    } else {
      createMutation.mutate(dto);
    }
  };

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={editGroup ? 'Edit Printer Group' : 'Create Printer Group'}
      size="md"
      footer={
        <div className="flex gap-3">
          <Button variant="secondary" onClick={onClose} disabled={isPending}>
            Cancel
          </Button>
          <Button variant="primary" onClick={handleSubmit} loading={isPending} disabled={isPending}>
            {editGroup ? 'Update' : 'Create'}
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        <FormField label="Name" htmlFor="group-name" required error={nameError}>
          <Input
            id="group-name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Enter group name"
            invalid={!!nameError}
            disabled={isPending}
          />
        </FormField>

        <FormField label="Description" htmlFor="group-description">
          <Textarea
            id="group-description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Optional description"
            rows={3}
            disabled={isPending}
          />
        </FormField>
      </div>
    </Modal>
  );
}
