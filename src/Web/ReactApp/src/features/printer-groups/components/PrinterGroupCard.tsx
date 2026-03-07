import { Card, Button, Badge } from '@/common/components/ui';
import { EditIcon, DeleteIcon, PrinterIcon } from '@/common/components/icons/MdiIcons';
import type { PrinterGroup } from '@/types/api';
import { formatDistanceToNow } from 'date-fns';

interface PrinterGroupCardProps {
  group: PrinterGroup;
  onEdit: (group: PrinterGroup) => void;
  onDelete: (group: PrinterGroup) => void;
  onSelect: (group: PrinterGroup) => void;
}

export function PrinterGroupCard({ group, onEdit, onDelete, onSelect }: PrinterGroupCardProps) {
  return (
    <Card className="hover:border-pf-accent cursor-pointer transition-colors" onClick={() => onSelect(group)}>
      <Card.Body>
        <div className="flex items-start justify-between mb-3">
          <div className="flex-1 min-w-0">
            <h3 className="text-lg font-semibold text-pf-text-primary truncate">{group.name}</h3>
            {group.description && (
              <p className="text-sm text-pf-text-secondary mt-1 line-clamp-2">{group.description}</p>
            )}
          </div>
          <div className="flex gap-2 ml-3">
            <Button
              variant="ghost"
              size="sm"
              onClick={(e) => {
                e.stopPropagation();
                onEdit(group);
              }}
              iconLeft={<EditIcon />}
              aria-label="Edit group"
            />
            <Button
              variant="danger"
              size="sm"
              onClick={(e) => {
                e.stopPropagation();
                onDelete(group);
              }}
              iconLeft={<DeleteIcon />}
              aria-label="Delete group"
            />
          </div>
        </div>
        <div className="flex items-center gap-3 text-sm">
          <div className="flex items-center gap-1 text-pf-text-tertiary">
            <PrinterIcon className="w-4 h-4" />
            <span>
              {group.printerCount} {group.printerCount === 1 ? 'printer' : 'printers'}
            </span>
          </div>
          <Badge variant="default" size="sm">
            Updated {formatDistanceToNow(new Date(group.updatedDate), { addSuffix: true })}
          </Badge>
        </div>
      </Card.Body>
    </Card>
  );
}
