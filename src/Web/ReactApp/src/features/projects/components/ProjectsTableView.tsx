import React from 'react';
import { Badge } from '@/common/components/ui/Badge';
import { Button } from '@/common/components/ui/Button';
import { DeleteIcon, CheckIcon } from '@/common/components/icons/MdiIcons';
import type { PrintProjectListDto } from '@/types/api';

// Status badge color mapping — use string keys to avoid TDZ in production builds
const statusVariantMap: Record<string, 'default' | 'primary' | 'success' | 'warning' | 'error'> = {
  Open: 'default',
  InProgress: 'primary',
  Completed: 'success',
  Cancelled: 'error',
  OnHold: 'warning',
};

const statusLabelMap: Record<string, string> = {
  Open: 'Open',
  InProgress: 'In Progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  OnHold: 'On Hold',
};

interface ProjectsTableViewProps {
  projects: PrintProjectListDto[];
  onProjectClick: (project: PrintProjectListDto) => void;
  onDelete: (e: React.MouseEvent, projectId: string) => void;
}

/**
 * ProjectsTableView - Tabular view of projects with sortable columns.
 * Displays name, status, progress, files, prints, due date, and actions.
 */
export const ProjectsTableView: React.FC<ProjectsTableViewProps> = ({
  projects,
  onProjectClick,
  onDelete,
}) => {
  return (
    <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto flex-1">
      <table className="w-full text-sm" role="table" aria-label="Projects list">
        <thead>
          <tr className="border-b border-pf-border bg-pf-bg-2 sticky top-0">
            <th scope="col" className="px-4 py-3 text-left font-semibold text-pf-text-primary">Name</th>
            <th scope="col" className="px-4 py-3 text-left font-semibold text-pf-text-primary">Status</th>
            <th scope="col" className="px-4 py-3 text-left font-semibold text-pf-text-primary">Progress</th>
            <th scope="col" className="px-4 py-3 text-center font-semibold text-pf-text-primary">Files</th>
            <th scope="col" className="px-4 py-3 text-center font-semibold text-pf-text-primary">Prints</th>
            <th scope="col" className="px-4 py-3 text-right font-semibold text-pf-text-primary">Est. Cost</th>
            <th scope="col" className="px-4 py-3 text-left font-semibold text-pf-text-primary">Due Date</th>
            <th scope="col" className="px-4 py-3 text-left font-semibold text-pf-text-primary">Created</th>
            <th scope="col" className="px-4 py-3 w-16"><span className="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody className="divide-y divide-pf-border">
          {projects.map((project) => (
            <tr
              key={project.id}
              onClick={() => onProjectClick(project)}
              className="hover:bg-pf-bg-2 cursor-pointer transition-colors"
              role="row"
            >
              {/* Name + description */}
              <td className="px-4 py-3 max-w-[250px]">
                <div className="font-medium text-pf-text-primary truncate">{project.name}</div>
                {project.description && (
                  <div className="text-xs text-pf-text-tertiary truncate mt-0.5">{project.description}</div>
                )}
              </td>

              {/* Status badge */}
              <td className="px-4 py-3">
                <Badge variant={statusVariantMap[project.status]} size="sm">
                  {statusLabelMap[project.status]}
                </Badge>
              </td>

              {/* Progress bar */}
              <td className="px-4 py-3 min-w-[140px]">
                <div className="flex items-center gap-2">
                  <div className="flex-1 h-2 bg-pf-bg-2 rounded-full overflow-hidden">
                    <div
                      className="h-full bg-pf-accent rounded-full transition-all duration-300"
                      style={{ width: `${project.progressPercent}%` }}
                    />
                  </div>
                  <span className="text-xs text-pf-text-secondary w-10 text-right">{project.progressPercent}%</span>
                </div>
              </td>

              {/* Files */}
              <td className="px-4 py-3 text-center">
                <span className="text-pf-text-primary">{project.completedFiles}</span>
                <span className="text-pf-text-tertiary"> / {project.totalFiles}</span>
                {project.completedFiles > 0 && project.completedFiles === project.totalFiles && (
                  <CheckIcon className="w-3 h-3 text-pf-success inline ml-1" />
                )}
              </td>

              {/* Prints */}
              <td className="px-4 py-3 text-center">
                <span className="text-pf-text-primary">{project.completedPrints}</span>
                <span className="text-pf-text-tertiary"> / {project.totalPrints}</span>
              </td>

              {/* Est. Cost */}
              <td className="px-4 py-3 text-right text-pf-text-secondary whitespace-nowrap">
                {project.estimatedTotalCost != null && project.estimatedTotalCost > 0
                  ? `$${project.estimatedTotalCost.toFixed(2)}`
                  : <span className="text-pf-text-tertiary">—</span>}
              </td>

              {/* Due Date */}
              <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">
                {project.dueDate
                  ? new Date(project.dueDate).toLocaleDateString()
                  : <span className="text-pf-text-tertiary">—</span>}
              </td>

              {/* Created */}
              <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">
                {new Date(project.createdAt).toLocaleDateString()}
              </td>

              {/* Actions */}
              <td className="px-4 py-3">
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={(e) => { e.stopPropagation(); onDelete(e, project.id); }}
                  className="!p-1 opacity-50 hover:opacity-100 hover:text-pf-error"
                  title="Delete project"
                >
                  <DeleteIcon className="w-4 h-4" />
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

export default ProjectsTableView;
