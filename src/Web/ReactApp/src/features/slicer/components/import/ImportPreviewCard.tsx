import clsx from 'clsx';
import { Card, Badge, Button, Checkbox } from '@/common/components/ui';
import { AlertIcon, EyeIcon } from '@/common/components/icons/MdiIcons';

export interface ImportPreviewCardProps {
  name: string;
  type: 'process' | 'machine' | 'filament';
  source: string;
  fieldCount: number;
  hasConflict: boolean;
  isSelected: boolean;
  onToggleSelect: () => void;
  onViewDetails?: () => void;
  className?: string;
}

const typeConfig = {
  process: {
    label: 'Process',
    color: 'bg-blue-500/10 text-blue-500 border-blue-500/20',
  },
  machine: {
    label: 'Machine',
    color: 'bg-purple-500/10 text-purple-500 border-purple-500/20',
  },
  filament: {
    label: 'Filament',
    color: 'bg-green-500/10 text-green-500 border-green-500/20',
  },
};

export function ImportPreviewCard({
  name,
  type,
  source,
  fieldCount,
  hasConflict,
  isSelected,
  onToggleSelect,
  onViewDetails,
  className,
}: ImportPreviewCardProps) {
  return (
    <Card
      className={clsx(
        'transition-all duration-200',
        isSelected ? 'border-pf-accent' : 'border-pf-border',
        !isSelected && 'opacity-60',
        className
      )}
    >
      <Card.Body className="p-4">
        <div className="flex items-start gap-3">
          {/* Checkbox */}
          <div className="pt-0.5">
            <Checkbox
              checked={isSelected}
              onChange={onToggleSelect}
              aria-label={`Select ${name}`}
            />
          </div>

          {/* Content */}
          <div className="flex-1 min-w-0">
            <div className="flex items-start justify-between gap-3">
              <div className="flex-1 min-w-0">
                {/* Name and type */}
                <div className="flex items-center gap-2 mb-2">
                  <h4 className="text-base font-semibold text-pf-text-primary truncate">
                    {name}
                  </h4>
                  <Badge
                    variant="default"
                    size="sm"
                    className={typeConfig[type].color}
                  >
                    {typeConfig[type].label}
                  </Badge>
                  {hasConflict && (
                    <div
                      className="flex items-center gap-1 text-pf-warning"
                      title="This profile has naming conflicts"
                    >
                      <AlertIcon className="w-4 h-4" />
                    </div>
                  )}
                </div>

                {/* Metadata */}
                <div className="flex items-center gap-4 text-sm text-pf-text-secondary">
                  <div className="flex items-center gap-1">
                    <span className="font-medium">Source:</span>
                    <span className="truncate">{source}</span>
                  </div>
                  <div className="flex items-center gap-1">
                    <span className="font-medium">Fields:</span>
                    <span>{fieldCount}</span>
                  </div>
                </div>

                {/* Conflict warning */}
                {hasConflict && (
                  <div className="mt-2 px-2 py-1 rounded bg-pf-warning/10 border border-pf-warning/20">
                    <p className="text-xs text-pf-warning font-medium">
                      Naming conflict detected — review required
                    </p>
                  </div>
                )}
              </div>

              {/* View details button */}
              {onViewDetails && (
                <Button
                  variant="unstyled"
                  onClick={(e) => {
                    e.stopPropagation();
                    onViewDetails();
                  }}
                  className={clsx(
                    'flex items-center gap-1 px-2 py-1 rounded text-sm font-medium transition-colors',
                    'text-pf-accent hover:bg-pf-accent/10',
                    !isSelected && 'opacity-50'
                  )}
                  disabled={!isSelected}
                  aria-label={`View details for ${name}`}
                >
                  <EyeIcon className="w-4 h-4" />
                  <span>Details</span>
                </Button>
              )}
            </div>
          </div>
        </div>
      </Card.Body>
    </Card>
  );
}
