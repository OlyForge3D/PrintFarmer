import React, { useMemo } from 'react';
import {
  useFileHealthSummary,
  useFileAuditHistory,
  useFilesWithIssues,
} from '@/hooks/useApi';
import { FileAuditType } from '@/types/api';
import { HealthGauge } from './HealthGauge';
import { HealthStatistics } from './HealthStatistics';
import { AuditTimeline } from './AuditTimeline';
import { IssuesList } from './IssuesList';
import { PageTemplate } from '@/components/PageTemplate';
import { Skeleton } from '@/components/Skeleton';

export function FileHealthDashboard() {
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

    const { healthPercentage } = healthSummary.data;

    if (healthPercentage >= 95) {
      return {
        status: 'healthy' as const,
        message: 'All files are healthy',
      };
    } else if (healthPercentage >= 75) {
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
    <PageTemplate title="File Health Dashboard">
      <div className="space-y-6">
        {/* Error State */}
        {error && (
          <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg p-4">
            <p className="text-red-800 dark:text-red-200">
              Error loading file health data: {error.message}
            </p>
          </div>
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
              <div className="lg:col-span-2 bg-pf-surface rounded-lg border border-pf-border p-6">
                <div className="flex items-center justify-between mb-6">
                  <h2 className="text-xl font-semibold text-pf-text">Overall Health</h2>
                  <div
                    className={`px-3 py-1 rounded-full text-sm font-medium ${
                      statusInfo.status === 'healthy'
                        ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-200'
                        : statusInfo.status === 'warning'
                          ? 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-200'
                          : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-200'
                    }`}
                  >
                    {statusInfo.message}
                  </div>
                </div>
                {healthSummary.data && (
                  <div className="flex items-center justify-between">
                    <HealthGauge percentage={healthSummary.data.healthPercentage} />
                    <div className="text-right">
                      <div className="text-4xl font-bold text-pf-accent">
                        {Math.round(healthSummary.data.healthPercentage)}%
                      </div>
                      <p className="text-sm text-pf-text-secondary mt-1">
                        {healthSummary.data.healthyFiles} of {healthSummary.data.totalFiles} files healthy
                      </p>
                    </div>
                  </div>
                )}
              </div>

              {/* Statistics Cards */}
              {healthSummary.data && (
                <HealthStatistics
                  totalFiles={healthSummary.data.totalFiles}
                  healthyFiles={healthSummary.data.healthyFiles}
                  missingFiles={healthSummary.data.missingFiles}
                  corruptedFiles={healthSummary.data.corruptedFiles}
                  inaccessibleFiles={healthSummary.data.inaccessibleFiles}
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
    </PageTemplate>
  );
}
