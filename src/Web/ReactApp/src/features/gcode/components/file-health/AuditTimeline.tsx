import React from 'react';
import { FileHealthAuditDto, FileAuditType } from '@/types/api';
import { Card } from '@/common/components/ui/Card';
import { Badge } from '@/common/components/ui/Badge';

interface AuditTimelineProps {
  audits: FileHealthAuditDto[];
  getAuditTypeLabel: (type: FileAuditType) => string;
  isLoading: boolean;
}

export function AuditTimeline({ audits, getAuditTypeLabel, isLoading }: AuditTimelineProps) {
  if (isLoading) {
    return <div>Loading audit history...</div>;
  }

  if (!audits || audits.length === 0) {
    return (
      <Card>
        <Card.Header>
          <h3 className="text-lg font-semibold">Audit History</h3>
        </Card.Header>
        <Card.Body>
          <p className="text-pf-text-secondary">No audit history available yet</p>
        </Card.Body>
      </Card>
    );
  }

  const formatDate = (dateString: string): string => {
    try {
      const date = new Date(dateString);
      return date.toLocaleString('en-US', {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return dateString;
    }
  };

  return (
    <Card>
      <Card.Header>
        <h3 className="text-lg font-semibold">Audit History</h3>
      </Card.Header>
      <Card.Body>
        <div className="space-y-4">
          {audits.map((audit) => (
            <div key={audit.auditId} className="border border-pf-border rounded-lg p-4 hover:bg-pf-bg-2 transition-colors">
              <div className="flex items-start justify-between">
                <div className="flex-1">
                  {/* Audit header */}
                  <div className="flex items-center gap-3 mb-2">
                    <div className="shrink-0 w-2 h-2 rounded-full bg-pf-accent" />
                    <span className="font-medium text-pf-text-primary">{getAuditTypeLabel(audit.auditType)}</span>
                    <Badge variant={audit.hasIssues ? 'error' : 'success'}>
                      {audit.hasIssues ? 'Issues Found' : 'No Issues'}
                    </Badge>
                  </div>

                  {/* Audit details */}
                  <p className="text-sm text-pf-text-secondary mb-3">{formatDate(audit.auditDate)}</p>

                  {/* Summary message */}
                  <p className="text-sm text-pf-text-primary mb-3">{audit.summaryMessage}</p>

                  {/* Statistics */}
                  <div className="grid grid-cols-5 gap-2 text-xs">
                    <div>
                      <div className="text-pf-text-primary font-semibold">{audit.filesChecked}</div>
                      <div className="text-pf-text-secondary">Checked</div>
                    </div>
                    <div>
                      <div className="text-green-600 dark:text-green-400 font-semibold">{audit.validCount}</div>
                      <div className="text-pf-text-secondary">Valid</div>
                    </div>
                    <div>
                      <div className="text-red-600 dark:text-red-400 font-semibold">{audit.missingCount}</div>
                      <div className="text-pf-text-secondary">Missing</div>
                    </div>
                    <div>
                      <div className="text-orange-600 dark:text-orange-400 font-semibold">{audit.corruptedCount}</div>
                      <div className="text-pf-text-secondary">Corrupted</div>
                    </div>
                    <div>
                      <div className="text-purple-600 dark:text-purple-400 font-semibold">{audit.orphanedCount}</div>
                      <div className="text-pf-text-secondary">Orphaned</div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          ))}
        </div>

        {audits.length > 0 && (
          <p className="text-xs text-pf-text-secondary mt-6 text-center">
            Showing {audits.length} most recent audits
          </p>
        )}
      </Card.Body>
    </Card>
  );
}
