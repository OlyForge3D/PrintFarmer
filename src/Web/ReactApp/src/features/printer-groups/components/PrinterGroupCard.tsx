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
    <article aria-label={`${group.name} printer group`}>
      <Card className="relative hover:border-pf-accent transition-colors">
        <Button
          type="button"
          variant="unstyled"
          aria-label={`Select ${group.name} printer group`}
          onClick={() => onSelect(group)}
          className="absolute inset-0 z-0 cursor-pointer rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-pf-accent"
        />
        <Card.Body className="relative z-10 pointer-events-none">
          <div className="flex items-start justify-between mb-3">
            <div className="flex-1 min-w-0 text-left">
              <h3 className="text-lg font-semibold text-pf-text-primary truncate">{group.name}</h3>
              {group.description && (
                <p className="text-sm text-pf-text-secondary mt-1 line-clamp-2">{group.description}</p>
              )}
            </div>
            <div className="relative z-10 flex gap-2 ml-3 pointer-events-auto">
              <Button
                variant="ghost"
                size="sm"
                onClick={() => onEdit(group)}
                iconLeft={<EditIcon />}
                aria-label="Edit group"
              />
              <Button
                variant="danger"
                size="sm"
                onClick={() => onDelete(group)}
                iconLeft={<DeleteIcon />}
                aria-label="Delete group"
              />
            </div>
          </div>
          <div className="flex items-center gap-3 text-sm">
            <PrinterIcon className="w-4 h-4 text-pf-text-tertiary" />
            <span className="text-pf-text-tertiary">
              {group.printerCount} {group.printerCount === 1 ? 'printer' : 'printers'}
            </span>
            <Badge variant="default" size="sm">
              Updated {formatDistanceToNow(new Date(group.updatedDate), { addSuffix: true })}
            </Badge>
          </div>
        </Card.Body>
      </Card>
    </article>
  );
}
