import React, { useEffect, useState } from 'react';
import { CheckCircle, AlertCircle, Loader, X } from 'lucide-react';

interface FileImportStatus {
  fileId: string;
  fileName: string;
  status: 'pending' | 'importing' | 'completed' | 'failed' | 'skipped';
  progress: number;
  error?: string;
}

interface HarvestWizardStep4ProgressProps {
  totalFiles: number;
  onCompleted: () => void;
  onCancel?: () => void;
}

export function HarvestWizardStep4Progress({
  totalFiles,
  onCompleted,
  onCancel,
}: HarvestWizardStep4ProgressProps) {
  const [fileStatuses, setFileStatuses] = useState<FileImportStatus[]>([]);
  const [isImporting, setIsImporting] = useState(true);
  const [elapsedSeconds, setElapsedSeconds] = useState(0);
  const [startTime] = useState(Date.now());

  // Update elapsed time
  useEffect(() => {
    if (!isImporting) return;

    const interval = setInterval(() => {
      setElapsedSeconds(Math.floor((Date.now() - startTime) / 1000));
    }, 1000);

    return () => clearInterval(interval);
  }, [isImporting, startTime]);

  // TODO: Subscribe to SignalR progress events for operationId to update file statuses in real-time
  // signalRService.onHarvestFileProgress((evt) => { ... })
  
  // Simulate file import progress (in real implementation, this would come from SignalR)
  useEffect(() => {
    if (!isImporting || totalFiles === 0) return;

    // Simulate gradual file imports
    const interval = setInterval(() => {
      setFileStatuses(prev => {
        const updated = [...prev];

        // Find files still pending
        const pendingCount = updated.filter(f => f.status === 'pending').length;

        if (pendingCount === 0 && updated.length === totalFiles) {
          // All done
          setIsImporting(false);
          return updated;
        }

        // Move one pending to importing
        const pendingIdx = updated.findIndex(f => f.status === 'pending');
        if (pendingIdx !== -1) {
          updated[pendingIdx] = {
            ...updated[pendingIdx],
            status: 'importing',
            progress: Math.random() * 100,
          };
        }

        // Advance random importing files to completion
        updated.forEach((file, idx) => {
          if (file.status === 'importing' && Math.random() > 0.7) {
            updated[idx] = {
              ...file,
              status: 'completed',
              progress: 100,
            };
          }
        });

        return updated;
      });
    }, 1000);

    return () => clearInterval(interval);
  }, [isImporting, totalFiles]);

  // Initialize file statuses on mount
  useEffect(() => {
    if (fileStatuses.length === 0 && totalFiles > 0) {
      const statuses: FileImportStatus[] = Array.from({ length: totalFiles }, (_, i) => ({
        fileId: `file-${i}`,
        fileName: `File ${i + 1}`,
        status: 'pending',
        progress: 0,
      }));
      setFileStatuses(statuses);
    }
  }, [totalFiles, fileStatuses.length]);

  const completedCount = fileStatuses.filter(f => f.status === 'completed').length;
  const failedCount = fileStatuses.filter(f => f.status === 'failed').length;
  const skippedCount = fileStatuses.filter(f => f.status === 'skipped').length;
  const progressPercent = totalFiles > 0 ? Math.round((completedCount / totalFiles) * 100) : 0;

  const formatTime = (seconds: number) => {
    const hrs = Math.floor(seconds / 3600);
    const mins = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;

    if (hrs > 0) return `${hrs}h ${mins}m ${secs}s`;
    if (mins > 0) return `${mins}m ${secs}s`;
    return `${secs}s`;
  };

  return (
    <div className="space-y-6">
      <div className="bg-pf-surface border border-pf-border rounded-lg p-4">
        <div className="space-y-3">
          {/* Progress bar */}
          <div>
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium text-pf-text-primary">
                Import Progress
              </span>
              <span className="text-sm text-pf-text-secondary">
                {completedCount}/{totalFiles} files • {progressPercent}%
              </span>
            </div>
            {/* Text-based progress indicator */}
            <div className="bg-pf-border rounded-lg p-2 text-center">
              <div className="text-sm font-mono text-pf-text-secondary">
                {'['}
                {Array.from({ length: 20 }).map((_, i) =>
                  i < Math.round((progressPercent / 100) * 20) ? '█' : '░'
                )}
                {']'}
              </div>
            </div>
          </div>

          {/* Stats */}
          <div className="grid grid-cols-3 gap-3">
            <div className="bg-pf-bg p-2 rounded text-center">
              <div className="text-lg font-semibold text-pf-success">{completedCount}</div>
              <div className="text-xs text-pf-text-secondary">Imported</div>
            </div>
            {failedCount > 0 && (
              <div className="bg-pf-bg p-2 rounded text-center">
                <div className="text-lg font-semibold text-pf-error">{failedCount}</div>
                <div className="text-xs text-pf-text-secondary">Failed</div>
              </div>
            )}
            {skippedCount > 0 && (
              <div className="bg-pf-bg p-2 rounded text-center">
                <div className="text-lg font-semibold text-pf-warning">{skippedCount}</div>
                <div className="text-xs text-pf-text-secondary">Skipped</div>
              </div>
            )}
          </div>

          {/* Time */}
          <div className="text-center">
            <span className="text-sm text-pf-text-secondary">
              Elapsed: <span className="font-mono font-medium">{formatTime(elapsedSeconds)}</span>
            </span>
            {isImporting && (
              <span className="text-sm text-pf-text-secondary ml-4">
                Importing: {fileStatuses.filter(f => f.status === 'importing').length} file
                {fileStatuses.filter(f => f.status === 'importing').length !== 1 ? 's' : ''}
              </span>
            )}
          </div>
        </div>
      </div>

      {/* File list */}
      <div className="space-y-2">
        <h3 className="text-sm font-medium text-pf-text-primary">Import Details</h3>
        <div className="max-h-64 overflow-y-auto space-y-1 border border-pf-border rounded-lg p-3 bg-pf-bg">
          {fileStatuses.map(file => (
            <div
              key={file.fileId}
              className="flex items-center gap-3 p-2 rounded text-sm"
            >
              <div className="flex-shrink-0 w-4 h-4">
                {file.status === 'pending' && (
                  <div className="w-4 h-4 border-2 border-pf-border rounded-full" />
                )}
                {file.status === 'importing' && (
                  <Loader className="w-4 h-4 text-pf-accent animate-spin" />
                )}
                {file.status === 'completed' && (
                  <CheckCircle className="w-4 h-4 text-pf-success" />
                )}
                {file.status === 'failed' && (
                  <AlertCircle className="w-4 h-4 text-pf-error" />
                )}
                {file.status === 'skipped' && (
                  <div className="w-4 h-4 border-2 border-pf-warning rounded-full flex items-center justify-center">
                    <div className="w-2 h-2 bg-pf-warning rounded-full" />
                  </div>
                )}
              </div>
              <div className="flex-1 min-w-0">
                <div className="text-pf-text-primary truncate">{file.fileName}</div>
                {file.error && (
                  <div className="text-xs text-pf-error truncate">{file.error}</div>
                )}
              </div>
              <div className="text-xs text-pf-text-secondary">
                {file.status === 'importing' && `${Math.round(file.progress)}%`}
                {file.status === 'completed' && 'Done'}
                {file.status === 'pending' && 'Waiting'}
                {file.status === 'failed' && 'Failed'}
                {file.status === 'skipped' && 'Skipped'}
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Actions */}
      <div className="flex gap-3">
        {isImporting && onCancel && (
          <button
            onClick={onCancel}
            className="flex-1 px-4 py-2 border border-pf-border text-pf-text-primary rounded-lg hover:bg-pf-hover transition-colors font-medium"
          >
            <X className="w-4 h-4 inline mr-2" />
            Cancel Import
          </button>
        )}
        {!isImporting && (
          <button
            onClick={onCompleted}
            className="flex-1 px-4 py-2 bg-pf-accent text-white rounded-lg hover:bg-pf-accent-hover transition-colors font-medium"
          >
            <CheckCircle className="w-4 h-4 inline mr-2" />
            Complete
          </button>
        )}
      </div>

      {!isImporting && (
        <div className="p-3 bg-pf-success-bg border border-pf-success rounded-lg">
          <p className="text-sm text-pf-success font-medium">
            ✓ Import completed! {completedCount} file{completedCount !== 1 ? 's' : ''} imported successfully.
          </p>
        </div>
      )}
    </div>
  );
}
