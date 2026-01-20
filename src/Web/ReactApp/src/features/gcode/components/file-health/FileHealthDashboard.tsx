/**
 * FileHealthDashboard Component
 * 
 * Displays gcode file health metrics and audit information.
 * This is a placeholder component for the file health monitoring feature.
 */

import { HealthGauge } from './HealthGauge';
import { HealthStatistics } from './HealthStatistics';
import { AuditTimeline } from './AuditTimeline';
import { IssuesList } from './IssuesList';
import { FileAuditType, FileHealthAuditDto } from '@/types/api';

export function FileHealthDashboard() {
  // Mock data for placeholder component
  const mockIssuesData = {
    totalIssues: 0,
    missingFiles: [],
    corruptedFiles: [],
    inaccessibleFiles: [],
  };

  const mockAudits: FileHealthAuditDto[] = [];

  const getAuditTypeLabel = (type: FileAuditType): string => {
    switch (type) {
      case FileAuditType.Model3D:
        return 'Model3D Files Audit';
      case FileAuditType.GcodeFile:
        return 'G-code Files Audit';
      case FileAuditType.OrphanedFiles:
        return 'Orphaned Files Audit';
      case FileAuditType.FullAudit:
        return 'Full System Audit';
      default:
        return 'Unknown Audit Type';
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-pf-text-primary">File Health Dashboard</h1>
          <p className="text-sm text-pf-text-secondary mt-1">
            Monitor gcode file quality and health metrics
          </p>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Health Gauge */}
        <div className="bg-pf-bg-2 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4">Overall Health</h2>
          <HealthGauge percentage={100} />
        </div>

        {/* Health Statistics */}
        <div className="bg-pf-bg-2 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4">Statistics</h2>
          <HealthStatistics
            totalModel3DFiles={0}
            model3DHealthy={0}
            model3DMissing={0}
            model3DCorrupted={0}
            totalGcodeFiles={0}
            gcodeHealthy={0}
            gcodeMissing={0}
            gcodeCorrupted={0}
          />
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Issues List */}
        <div className="bg-pf-bg-2 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4">Issues</h2>
          <IssuesList data={mockIssuesData} isLoading={false} />
        </div>

        {/* Audit Timeline */}
        <div className="bg-pf-bg-2 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4">Audit Timeline</h2>
          <AuditTimeline audits={mockAudits} getAuditTypeLabel={getAuditTypeLabel} isLoading={false} />
        </div>
      </div>
    </div>
  );
}
