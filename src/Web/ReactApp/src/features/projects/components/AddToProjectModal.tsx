import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import clsx from 'clsx';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Spinner } from '@/common/components/ui';
import { PlusIcon, SearchIcon, ClipboardListIcon } from '@/common/components/icons/MdiIcons';
import { projectService } from '@/services/projectService';
import type { GcodeFile, PrintProjectListDto, AddFileToProjectRequest } from '@/types/api';

interface AddToProjectModalProps {
  files?: GcodeFile[];
  fileIds?: string[];
  isOpen: boolean;
  onClose: () => void;
}

export function AddToProjectModal({ files, fileIds, isOpen, onClose }: AddToProjectModalProps) {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);

  const effectiveIds = files?.map((f) => f.id) ?? fileIds ?? [];
  const fileCount = effectiveIds.length;

  const { data: projects = [], isLoading } = useQuery({
    queryKey: ['projects'],
    queryFn: () => projectService.getProjects(),
    staleTime: 30_000,
    enabled: isOpen,
  });

  const addMutation = useMutation({
    mutationFn: async ({ projectId, gcodeFileIds }: { projectId: string; gcodeFileIds: string[] }) => {
      const requests: AddFileToProjectRequest[] = gcodeFileIds.map((id) => ({
        gcodeFileId: id,
        printCount: 1,
        materialRequirement: files?.find((f) => f.id === id)?.requiredMaterial || undefined,
      }));
      return projectService.addFilesToProject(projectId, requests);
    },
    onSuccess: (_data, { projectId }) => {
      queryClient.invalidateQueries({ queryKey: ['projects'] });
      queryClient.invalidateQueries({ queryKey: ['project', projectId] });
      const project = projects.find((p) => p.id === projectId);
      toast.success(
        `Added ${fileCount} file${fileCount !== 1 ? 's' : ''} to "${project?.name ?? 'project'}"`
      );
      handleClose();
    },
    onError: (err: Error) => {
      toast.error(`Failed to add files: ${err.message}`);
    },
  });

  const handleClose = () => {
    setSearch('');
    setSelectedProjectId(null);
    onClose();
  };

  const handleAdd = () => {
    if (!selectedProjectId) return;
    addMutation.mutate({ projectId: selectedProjectId, gcodeFileIds: effectiveIds });
  };

  const activeProjects = projects.filter(
    (p) => p.status === 'Open' || p.status === 'InProgress'
  );

  const filtered = activeProjects.filter((p) =>
    p.name.toLowerCase().includes(search.toLowerCase())
  );

  const fileLabel = files?.length === 1 ? files[0].name : `${fileCount} file${fileCount !== 1 ? 's' : ''}`;

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Add to Project"
      size="md"
      footer={
        <div className="flex gap-3 justify-end w-full">
          <Button variant="secondary" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleAdd}
            disabled={!selectedProjectId || addMutation.isPending}
            loading={addMutation.isPending}
            iconLeft={<PlusIcon className="w-4 h-4" />}
          >
            Add to Project
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        <p className="text-sm text-pf-text-secondary">
          Adding <span className="font-medium text-pf-text-primary">{fileLabel}</span> to a project.
        </p>

        <div className="relative">
          <SearchIcon className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search projects..."
            className="pl-9"
          />
        </div>

        {isLoading ? (
          <div className="flex justify-center py-8">
            <Spinner size="md" />
          </div>
        ) : filtered.length === 0 ? (
          <div className="text-center py-8 text-pf-text-secondary">
            <ClipboardListIcon className="w-8 h-8 mx-auto mb-2 opacity-40" />
            <p>{activeProjects.length === 0 ? 'No active projects' : 'No matching projects'}</p>
          </div>
        ) : (
          <div className="max-h-64 overflow-y-auto space-y-1 border border-pf-border rounded-lg p-1">
            {filtered.map((project) => (
              <ProjectRow
                key={project.id}
                project={project}
                isSelected={selectedProjectId === project.id}
                onSelect={() => setSelectedProjectId(project.id)}
              />
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}

function ProjectRow({
  project,
  isSelected,
  onSelect,
}: {
  project: PrintProjectListDto;
  isSelected: boolean;
  onSelect: () => void;
}) {
  const progress = project.totalPrints > 0
    ? Math.round((project.completedPrints / project.totalPrints) * 100)
    : 0;

  return (
    <Button
      type="button"
      variant="unstyled"
      onClick={onSelect}
      className={clsx(
        'w-full text-left px-3 py-2.5 rounded-md transition-colors flex items-center justify-between gap-3',
        isSelected
          ? 'bg-pf-accent-bg border border-pf-accent text-pf-text-primary'
          : 'hover:bg-pf-bg-1 border border-transparent text-pf-text-primary'
      )}
    >
      <div className="min-w-0 flex-1">
        <div className="font-medium truncate">{project.name}</div>
        <div className="text-xs text-pf-text-secondary mt-0.5">
          {project.totalFiles} file{project.totalFiles !== 1 ? 's' : ''} · {progress}% complete
        </div>
      </div>
      <div className="flex-shrink-0 text-xs text-pf-text-tertiary">
        {project.status}
      </div>
    </Button>
  );
}
