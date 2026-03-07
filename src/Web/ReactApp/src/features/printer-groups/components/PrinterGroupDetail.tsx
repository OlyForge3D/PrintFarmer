import { useQuery } from '@tanstack/react-query';
import { Card, Button, Spinner, Badge } from '@/common/components/ui';
import { ArrowLeftIcon, EditIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { PrinterAssignment } from './PrinterAssignment';
import type { PrinterGroup } from '@/types/api';
import { formatDistanceToNow } from 'date-fns';

interface PrinterGroupDetailProps {
  groupId: string;
  onBack: () => void;
  onEdit: (group: PrinterGroup) => void;
  onDelete: (group: PrinterGroup) => void;
}

export function PrinterGroupDetail({ groupId, onBack, onEdit, onDelete }: PrinterGroupDetailProps) {
  const { data: group, isLoading, error } = useQuery({
    queryKey: ['printer-groups', groupId],
    queryFn: () => apiClient.getPrinterGroup(groupId),
    staleTime: 10_000,
  });

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[40vh]">
        <Spinner size="lg" />
      </div>
    );
  }

  if (error || !group) {
    return (
      <div className="p-4">
        <Button variant="ghost" onClick={onBack} iconLeft={<ArrowLeftIcon />}>
          Back to Groups
        </Button>
        <div className="mt-4 text-pf-error">
          Failed to load group details: {String(error)}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <Button variant="ghost" onClick={onBack} iconLeft={<ArrowLeftIcon />}>
          Back to Groups
        </Button>
        <div className="flex gap-2">
          <Button
            variant="secondary"
            onClick={() =>
              onEdit({
                id: group.id,
                name: group.name,
                description: group.description,
                createdDate: group.createdDate,
                updatedDate: group.updatedDate,
                printerCount: group.printers.length,
              })
            }
            iconLeft={<EditIcon />}
          >
            Edit
          </Button>
          <Button
            variant="danger"
            onClick={() =>
              onDelete({
                id: group.id,
                name: group.name,
                description: group.description,
                createdDate: group.createdDate,
                updatedDate: group.updatedDate,
                printerCount: group.printers.length,
              })
            }
            iconLeft={<DeleteIcon />}
          >
            Delete
          </Button>
        </div>
      </div>

      <Card>
        <Card.Header>
          <div className="flex items-start justify-between">
            <div>
              <h2 className="text-2xl font-bold text-pf-text-primary">{group.name}</h2>
              {group.description && (
                <p className="text-sm text-pf-text-secondary mt-1">{group.description}</p>
              )}
            </div>
            <Badge variant="default">
              {group.printers.length} {group.printers.length === 1 ? 'printer' : 'printers'}
            </Badge>
          </div>
        </Card.Header>
        <Card.Body>
          <div className="flex gap-4 text-sm text-pf-text-tertiary mb-6">
            <span>Created {formatDistanceToNow(new Date(group.createdDate), { addSuffix: true })}</span>
            <span>•</span>
            <span>Updated {formatDistanceToNow(new Date(group.updatedDate), { addSuffix: true })}</span>
          </div>

          <PrinterAssignment groupId={group.id} assignedPrinters={group.printers} />
        </Card.Body>
      </Card>
    </div>
  );
}
