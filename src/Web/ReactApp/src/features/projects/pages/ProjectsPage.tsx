import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { projectService } from '@/services/projectService';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui';
import { Card } from '@/common/components/ui/Card';
import { Badge } from '@/common/components/ui/Badge';
import { 
  PlusIcon, 
  SearchIcon, 
  FilterIcon, 
  DeleteIcon,
  CheckIcon,
} from '@/common/components/icons/MdiIcons';
import { CreateProjectModal } from '@/features/projects/components/CreateProjectModal';
import { ProjectDetailModal } from '@/features/projects/components/ProjectDetailModal';
import type { 
  PrintProjectListDto, 
  PrintProjectStatus,
  PrintProjectDetailDto,
} from '@/types/api';

// Status badge color mapping
const statusVariantMap: Record<PrintProjectStatus, 'default' | 'primary' | 'success' | 'warning' | 'error'> = {
  Open: 'default',
  InProgress: 'primary',
  Completed: 'success',
  Cancelled: 'error',
  OnHold: 'warning',
};

// Status display labels
const statusLabelMap: Record<PrintProjectStatus, string> = {
  Open: 'Open',
  InProgress: 'In Progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  OnHold: 'On Hold',
};

export const ProjectsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [searchQuery, setSearchQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState<PrintProjectStatus | ''>('');
  const [showCreateModal, setShowCreateModal] = useState(false);
  const [selectedProject, setSelectedProject] = useState<PrintProjectDetailDto | null>(null);

  // Fetch projects
  const { data: projects = [], isLoading, error } = useQuery({
    queryKey: ['projects', statusFilter, searchQuery],
    queryFn: () => projectService.getProjects({
      status: statusFilter || undefined,
      search: searchQuery || undefined,
    }),
    staleTime: 30 * 1000, // 30 seconds
  });

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: (id: string) => projectService.deleteProject(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] });
    },
  });

  // Group projects by status for overview
  const projectsByStatus = projects.reduce((acc, project) => {
    const status = project.status;
    if (!acc[status]) acc[status] = [];
    acc[status].push(project);
    return acc;
  }, {} as Record<PrintProjectStatus, PrintProjectListDto[]>);

  const handleProjectClick = async (project: PrintProjectListDto) => {
    const detail = await projectService.getProject(project.id);
    setSelectedProject(detail);
  };

  const handleDeleteProject = (e: React.MouseEvent, projectId: string) => {
    e.stopPropagation();
    if (confirm('Are you sure you want to delete this project?')) {
      deleteMutation.mutate(projectId);
    }
  };

  if (error) {
    return (
      <PageTemplate title="Projects" showHeader={false} padding="px-4" backgroundColor="bg-pf-bg-2">
        <div className="p-4 text-pf-error">Failed to load projects: {String(error)}</div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Projects"
      subtitle="Track multi-print jobs and progress"
      showHeader={false}
      padding="px-4"
      backgroundColor="bg-pf-bg-2"
    >
      <div className="space-y-4 h-full flex flex-col">
        {/* Header with search and filters */}
        <div className="flex flex-wrap items-center gap-3 bg-pf-bg-1 rounded-lg border border-pf-border p-3">
          {/* Search */}
          <div className="relative flex-1 min-w-[200px]">
            <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-tertiary" />
            <input
              type="text"
              placeholder="Search projects..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full pl-9 pr-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-sm text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            />
          </div>

          {/* Status filter */}
          <div className="flex items-center gap-2">
            <FilterIcon className="w-4 h-4 text-pf-text-tertiary" />
            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value as PrintProjectStatus | '')}
              className="bg-pf-bg-2 border border-pf-border rounded-lg px-3 py-2 text-sm text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            >
              <option value="">All Status</option>
              <option value="Open">Open</option>
              <option value="InProgress">In Progress</option>
              <option value="Completed">Completed</option>
              <option value="OnHold">On Hold</option>
              <option value="Cancelled">Cancelled</option>
            </select>
          </div>

          {/* Create button */}
          <Button
            variant="primary"
            onClick={() => setShowCreateModal(true)}
            iconLeft={<PlusIcon className="w-4 h-4" />}
          >
            New Project
          </Button>
        </div>

        {/* Summary cards */}
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <SummaryCard
            label="Active"
            count={(projectsByStatus['Open']?.length || 0) + (projectsByStatus['InProgress']?.length || 0)}
            variant="primary"
          />
          <SummaryCard
            label="In Progress"
            count={projectsByStatus['InProgress']?.length || 0}
            variant="info"
          />
          <SummaryCard
            label="Completed"
            count={projectsByStatus['Completed']?.length || 0}
            variant="success"
          />
          <SummaryCard
            label="On Hold"
            count={projectsByStatus['OnHold']?.length || 0}
            variant="warning"
          />
        </div>

        {/* Projects list */}
        <div className="flex-1 min-h-0 overflow-y-auto">
          {isLoading ? (
            <div className="flex items-center justify-center py-12">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
            </div>
          ) : projects.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 text-pf-text-secondary">
              <p className="text-lg mb-2">No projects found</p>
              <p className="text-sm mb-4">Create a project to track multi-print jobs</p>
              <Button
                variant="primary"
                onClick={() => setShowCreateModal(true)}
                iconLeft={<PlusIcon className="w-4 h-4" />}
              >
                Create First Project
              </Button>
            </div>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {projects.map((project) => (
                <ProjectCard
                  key={project.id}
                  project={project}
                  onClick={() => handleProjectClick(project)}
                  onDelete={(e) => handleDeleteProject(e, project.id)}
                />
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Create Project Modal */}
      <CreateProjectModal
        isOpen={showCreateModal}
        onClose={() => setShowCreateModal(false)}
        onSuccess={() => {
          setShowCreateModal(false);
          queryClient.invalidateQueries({ queryKey: ['projects'] });
        }}
      />

      {/* Project Detail Modal */}
      {selectedProject && (
        <ProjectDetailModal
          project={selectedProject}
          isOpen={!!selectedProject}
          onClose={() => setSelectedProject(null)}
          onUpdate={() => {
            queryClient.invalidateQueries({ queryKey: ['projects'] });
            // Refresh the selected project
            projectService.getProject(selectedProject.id).then(setSelectedProject);
          }}
        />
      )}
    </PageTemplate>
  );
};

// Summary card component
interface SummaryCardProps {
  label: string;
  count: number;
  variant: 'primary' | 'info' | 'success' | 'warning';
}

const SummaryCard: React.FC<SummaryCardProps> = ({ label, count, variant }) => {
  const bgColors = {
    primary: 'bg-pf-accent/10 border-pf-accent/30',
    info: 'bg-blue-500/10 border-blue-500/30',
    success: 'bg-pf-success/10 border-pf-success/30',
    warning: 'bg-pf-warning/10 border-pf-warning/30',
  };

  const textColors = {
    primary: 'text-pf-accent',
    info: 'text-blue-500',
    success: 'text-pf-success',
    warning: 'text-pf-warning',
  };

  return (
    <div className={`rounded-lg border p-3 ${bgColors[variant]}`}>
      <div className={`text-2xl font-bold ${textColors[variant]}`}>{count}</div>
      <div className="text-sm text-pf-text-secondary">{label}</div>
    </div>
  );
};

// Project card component
interface ProjectCardProps {
  project: PrintProjectListDto;
  onClick: () => void;
  onDelete: (e: React.MouseEvent) => void;
}

const ProjectCard: React.FC<ProjectCardProps> = ({ project, onClick, onDelete }) => {
  return (
    <Card hoverable onClick={onClick} className="cursor-pointer">
      <Card.Body className="p-4">
        <div className="flex items-start justify-between gap-2 mb-3">
          <h3 className="font-semibold text-pf-text-primary truncate flex-1">
            {project.name}
          </h3>
          <Badge variant={statusVariantMap[project.status]} size="sm">
            {statusLabelMap[project.status]}
          </Badge>
        </div>

        {project.description && (
          <p className="text-sm text-pf-text-secondary mb-3 line-clamp-2">
            {project.description}
          </p>
        )}

        {/* Progress bar */}
        <div className="mb-3">
          <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
            <span>{project.completedPrints} / {project.totalPrints} prints</span>
            <span>{project.progressPercent}%</span>
          </div>
          <div className="h-2 bg-pf-bg-2 rounded-full overflow-hidden">
            <div
              className="h-full bg-pf-accent rounded-full transition-all duration-300"
              style={{ width: `${project.progressPercent}%` }}
            />
          </div>
        </div>

        {/* Footer info */}
        <div className="flex items-center justify-between text-xs text-pf-text-tertiary">
          <div className="flex items-center gap-2">
            <span>{project.totalFiles} files</span>
            {project.completedFiles > 0 && (
              <span className="flex items-center gap-1 text-pf-success">
                <CheckIcon className="w-3 h-3" />
                {project.completedFiles} done
              </span>
            )}
          </div>
          
          {project.dueDate && (
            <span>Due: {new Date(project.dueDate).toLocaleDateString()}</span>
          )}

          <Button
            variant="subtle"
            size="sm"
            onClick={onDelete}
            className="!p-1 opacity-50 hover:opacity-100 hover:text-pf-error"
            title="Delete project"
          >
            <DeleteIcon className="w-4 h-4" />
          </Button>
        </div>
      </Card.Body>
    </Card>
  );
};

export default ProjectsPage;
