import { useState } from 'react';
import clsx from 'clsx';
import { Button, Select, Input, Card, Badge, Alert, Radio } from '@/common/components/ui';
import { AlertIcon } from '@/common/components/icons/MdiIcons';

export interface ImportConflict {
  importedName: string;
  importedType: 'process' | 'machine' | 'filament';
  existingId?: string;
  existingName?: string;
  resolution: 'skip' | 'overwrite' | 'rename' | 'keep_both';
  newName?: string;
}

export interface ImportConflictResolverProps {
  conflicts: ImportConflict[];
  onChange: (conflicts: ImportConflict[]) => void;
  disabled?: boolean;
  className?: string;
}

const typeColors = {
  process: 'bg-blue-500/10 text-blue-500',
  machine: 'bg-purple-500/10 text-purple-500',
  filament: 'bg-green-500/10 text-green-500',
};

const resolutionLabels = {
  skip: 'Skip',
  overwrite: 'Overwrite',
  rename: 'Rename',
  keep_both: 'Keep Both',
};

export function ImportConflictResolver({
  conflicts,
  onChange,
  disabled = false,
  className,
}: ImportConflictResolverProps) {
  const [bulkResolution, setBulkResolution] = useState<string>('');

  const updateConflict = (index: number, updates: Partial<ImportConflict>) => {
    const updated = [...conflicts];
    updated[index] = { ...updated[index], ...updates };
    onChange(updated);
  };

  const applyBulkResolution = () => {
    if (!bulkResolution) return;
    
    const resolution = bulkResolution as ImportConflict['resolution'];
    const updated = conflicts.map((conflict) => ({
      ...conflict,
      resolution,
      newName: resolution === 'rename' ? `${conflict.importedName}-imported` : conflict.newName,
    }));
    onChange(updated);
    setBulkResolution('');
  };

  const summary = conflicts.reduce(
    (acc, conflict) => {
      acc[conflict.resolution]++;
      return acc;
    },
    { skip: 0, overwrite: 0, rename: 0, keep_both: 0 }
  );

  const summaryText = Object.entries(summary)
    .filter(([, count]) => count > 0)
    .map(([key, count]) => `${count} ${resolutionLabels[key as keyof typeof resolutionLabels].toLowerCase()}`)
    .join(', ');

  return (
    <div className={clsx('space-y-4', className)}>
      {/* Header with bulk actions */}
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-lg font-semibold text-pf-text-primary">
            Import Conflicts ({conflicts.length})
          </h3>
          {summaryText && (
            <p className="text-sm text-pf-text-secondary mt-1">
              {summaryText}
            </p>
          )}
        </div>
        <div className="flex items-center gap-2">
          <Select
            value={bulkResolution}
            onChange={(e) => setBulkResolution(e.target.value)}
            disabled={disabled}
            containerClassName="w-48"
          >
            <option value="">Apply to all...</option>
            <option value="skip">Skip all</option>
            <option value="overwrite">Overwrite all</option>
            <option value="rename">Rename all</option>
            <option value="keep_both">Keep both for all</option>
          </Select>
          <Button
            variant="secondary"
            size="sm"
            onClick={applyBulkResolution}
            disabled={!bulkResolution || disabled}
          >
            Apply
          </Button>
        </div>
      </div>

      {/* Conflict list */}
      <div className="space-y-3">
        {conflicts.map((conflict, index) => (
          <Card key={index}>
            <Card.Body className="p-4">
              <div className="space-y-3">
                {/* Profile info */}
                <div className="flex items-start justify-between">
                  <div className="flex-1">
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-pf-text-primary">
                        {conflict.importedName}
                      </span>
                      <Badge
                        variant="default"
                        size="sm"
                        className={typeColors[conflict.importedType]}
                      >
                        {conflict.importedType}
                      </Badge>
                    </div>
                    {conflict.existingName && (
                      <p className="text-sm text-pf-text-secondary mt-1">
                        Conflicts with existing: <span className="font-medium">{conflict.existingName}</span>
                      </p>
                    )}
                  </div>
                  {conflict.resolution === 'overwrite' && (
                    <div className="flex items-center gap-1 text-pf-warning">
                      <AlertIcon className="w-4 h-4" />
                      <span className="text-xs font-medium">Warning</span>
                    </div>
                  )}
                </div>

                {/* Resolution options */}
                <div className="space-y-2">
                  <label className="text-sm font-medium text-pf-text-primary">
                    Resolution:
                  </label>
                  <div className="grid grid-cols-2 gap-2">
                    {(['skip', 'overwrite', 'rename', 'keep_both'] as const).map((option) => (
                      <label
                        key={option}
                        className={clsx(
                          'flex items-center gap-2 px-3 py-2 rounded border cursor-pointer transition-colors',
                          conflict.resolution === option
                            ? 'border-pf-accent bg-pf-accent-bg'
                            : 'border-pf-border bg-pf-bg-0 hover:border-pf-accent/50',
                          disabled && 'opacity-50 cursor-not-allowed'
                        )}
                      >
                        <Radio
                          name={`resolution-${index}`}
                          value={option}
                          checked={conflict.resolution === option}
                          onChange={(e) =>
                            updateConflict(index, { resolution: e.target.value as ImportConflict['resolution'] })
                          }
                          disabled={disabled}
                        />
                        <span className="text-sm font-medium">
                          {resolutionLabels[option]}
                        </span>
                      </label>
                    ))}
                  </div>
                </div>

                {/* Rename input */}
                {conflict.resolution === 'rename' && (
                  <div>
                    <label className="text-sm font-medium text-pf-text-primary block mb-1">
                      New name:
                    </label>
                    <Input
                      value={conflict.newName || ''}
                      onChange={(e) => updateConflict(index, { newName: e.target.value })}
                      placeholder={`${conflict.importedName}-imported`}
                      disabled={disabled}
                    />
                  </div>
                )}

                {/* Keep both explanation */}
                {conflict.resolution === 'keep_both' && (
                  <Alert type="info" className="text-xs">
                    Profile will be imported as "{conflict.importedName}-imported"
                  </Alert>
                )}

                {/* Overwrite warning */}
                {conflict.resolution === 'overwrite' && (
                  <Alert type="warning" className="text-xs">
                    The existing profile will be permanently replaced. This action cannot be undone.
                  </Alert>
                )}
              </div>
            </Card.Body>
          </Card>
        ))}
      </div>
    </div>
  );
}
