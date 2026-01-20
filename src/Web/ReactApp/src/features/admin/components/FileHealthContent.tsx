import React, { useMemo } from 'react';
import {
  useFileHealthSummary,
  useFileAuditHistory,
  useFilesWithIssues,
} from '@/common/hooks/useApi';
import { FileAuditType } from '@/types/api';
import { HealthGauge } from '@/features/gcode/components/file-health/HealthGauge';
import { HealthStatistics } from '@/features/gcode/components/file-health/HealthStatistics';
import { AuditTimeline } from '@/features/gcode/components/file-health/AuditTimeline';
import { IssuesList } from '@/features/gcode/components/file-health/IssuesList';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Alert } from '@/common/components/ui/Alert';
import { Badge } from '@/common/components/ui/Badge';
import { Card } from '@/common/components/ui/Card';

export function FileHealthContent() {
  const healthSummary = useFileHealthSummary();
  const auditHistory = useFileAuditHistory(20);
  const filesWithIssues = useFilesWithIssues();

  const isLoading = healthSummary.isLoading || auditHistory.isLoading || filesWithIssues.isLoading;
  const error = healthSummary.error || auditHistory.error || filesWithIssues.error;

  const getAuditTypeLabel = (type: FileAuditType): string => {
    switch (type) {
      case FileAuditType.Model3D:
        return 'Model3D Files';
      case FileAuditType.GcodeFile:
        return 'G-code Files';
      case FileAuditType.OrphanedFiles:
        return 'Orphaned Files';
      case FileAuditType.FullAudit:
        return 'Full Audit';
      default:
        return 'Unknown';
    }
  };

  const statusInfo = useMemo(() => {
    if (!healthSummary.data) {
      return {
        status: 'unknown' as const,
        message: 'No health data available',
      };
    }

    const { overallHealthPercentage } = healthSummary.data;

    if (overallHealthPercentage >= 95) {
      return {
        status: 'healthy' as const,
        message: 'All files are healthy',
      };
    } else if (overallHealthPercentage >= 75) {
      return {
        status: 'warning' as const,
        message: 'Some files have issues',
      };
    } else {
      return {
        status: 'critical' as const,
        message: 'Multiple files require attention',
      };
    }
  }, [healthSummary.data]);

  return (
    <div className="space-y-6">
      {/* Error State */}
      {error && (
        <Alert type="error">
          Error loading file health data: {error.message}
        </Alert>
      )}

      {/* Loading State */}
      {isLoading ? (
        <div className="space-y-6">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
            <Skeleton className="h-64" />
            <Skeleton className="h-64" />
          </div>
          <Skeleton className="h-96" />
          <Skeleton className="h-80" />
        </div>
      ) : (
        <>
          {/* Health Overview Section */}
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {/* Gauge and Status */}
            <Card className="lg:col-span-2">
              <Card.Body>
                <div className="flex items-center justify-between mb-6">
                  <h2 className="text-xl font-semibold text-pf-text">Overall Health</h2>
                  <Badge
                    variant={
                      statusInfo.status === 'healthy'
                        ? 'success'
                        : statusInfo.status === 'warning'
                          ? 'warning'
                          : 'error'
                    }
                  >
                    {statusInfo.message}
                  </Badge>
                </div>
                {healthSummary.data && (
                  <div className="flex items-center justify-between">
                    <HealthGauge percentage={healthSummary.data.overallHealthPercentage} />
                    <div className="text-right">
                      <div className="text-4xl font-bold text-pf-accent">
                        {Math.round(healthSummary.data.overallHealthPercentage)}%
                      </div>
                      <p className="text-sm text-pf-text-secondary mt-1">
                        {healthSummary.data.model3DHealthy + healthSummary.data.gcodeHealthy} of {healthSummary.data.totalModel3DFiles + healthSummary.data.totalGcodeFiles} files healthy
                      </p>
                    </div>
                  </div>
                )}
              </Card.Body>
            </Card>

            {/* Statistics Cards */}
            {healthSummary.data && (
              <HealthStatistics
                totalModel3DFiles={healthSummary.data.totalModel3DFiles}
                model3DHealthy={healthSummary.data.model3DHealthy}
                model3DMissing={healthSummary.data.model3DMissing}
                model3DCorrupted={healthSummary.data.model3DCorrupted}
                totalGcodeFiles={healthSummary.data.totalGcodeFiles}
                gcodeHealthy={healthSummary.data.gcodeHealthy}
                gcodeMissing={healthSummary.data.gcodeMissing}
                gcodeCorrupted={healthSummary.data.gcodeCorrupted}
              />
            )}
          </div>

          {/* Issues Section */}
          {filesWithIssues.data && (
            <IssuesList
              data={filesWithIssues.data}
              isLoading={filesWithIssues.isLoading}
            />
          )}

          {/* Audit History Section */}
          {auditHistory.data && (
            <AuditTimeline
              audits={auditHistory.data}
              getAuditTypeLabel={getAuditTypeLabel}
              isLoading={auditHistory.isLoading}
            />
          )}
        </>
      )}
    </div>
  );
}
