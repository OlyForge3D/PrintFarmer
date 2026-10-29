import { Fragment, useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button, FormField, Input, Label, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { getErrorMessage, isApiError } from '@/common/utils/apiErrors';
import { apiClient } from '@/services/api';

interface LocalLibraryFoldersProps {
  controls?: { id: string; expanded: boolean };
  path: string;
  onNavigate: (path: string) => void;
  modelIds: string[];
  gcodeIds: string[];
  canCreate: boolean;
  canMove: boolean;
  onMoved: () => void;
}

function folderLabel(path: string): string {
  const parts = path.split('/').filter(Boolean);
  return parts.length === 0 ? 'Root' : `${'\u00a0\u00a0'.repeat(parts.length)}${parts.at(-1)}`;
}

async function checkMove(result: Promise<{ success: boolean; message?: string }>): Promise<void> {
  const response = await result;
  if (!response.success) throw new Error(response.message || 'Some files could not be moved');
}

async function runFolderOperations(operations: Array<{ label: string; run: () => Promise<void> }>): Promise<void> {
  const results = await Promise.allSettled(operations.map(operation => operation.run()));
  const failures = results.flatMap((result, index) => result.status === 'rejected'
    ? [`${operations[index].label}: ${getErrorMessage(result.reason, 'Request failed')}`]
    : []);
  if (failures.length > 0) throw new Error(failures.join('. '));
}

export function LocalLibraryFolders({
  controls, path, onNavigate, modelIds, gcodeIds, canCreate, canMove, onMoved,
}: LocalLibraryFoldersProps) {
  const id = useId();
  const queryClient = useQueryClient();
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const [moveSelection, setMoveSelection] = useState<{ models: string[]; gcode: string[] } | null>(null);
  const [destination, setDestination] = useState('/');
  const folders = useQuery({
    queryKey: ['local-library-folders'],
    queryFn: async () => {
      const [models, gcode] = await Promise.all([
        apiClient.listModelsFolders(), apiClient.getGcodeFilesFolders(),
      ]);
      return { models, gcode };
    },
  });
  const paths = useMemo(() => {
    const result = new Set(['/']);
    for (const folder of [...(folders.data?.models ?? []), ...(folders.data?.gcode ?? [])]) {
      const segments = folder.path.split('/').filter(Boolean);
      segments.forEach((_, index) => result.add('/' + segments.slice(0, index + 1).join('/')));
    }
    if (path) result.add(path);
    return [...result].sort((a, b) => a.localeCompare(b));
  }, [folders.data, path]);
  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['local-library-folders'] }),
      queryClient.invalidateQueries({ queryKey: ['file-browser'] }),
    ]);
  };
  const create = useMutation({
    mutationFn: async (target: string) => {
      await runFolderOperations([
        {
          label: 'Models',
          run: async () => {
            if (!folders.data?.models.some(folder => folder.path === target)) {
              await apiClient.createModelFolder(target);
            }
          },
        },
        {
          label: 'G-code',
          run: async () => {
            if (folders.data?.gcode.some(folder => folder.path === target)) return;
            try {
              const response = await apiClient.createGcodeDirectory(target);
              if (!response.success) throw new Error(response.message || 'Could not create folder');
            } catch (error) {
              // This endpoint reports an existing directory as a conflict.
              if (!isApiError(error) || error.statusCode !== 409) throw error;
            }
          },
        },
      ]);
    },
    onSuccess: (_, target) => {
      setCreating(false);
      onNavigate(target);
      toast.success('Folder created');
    },
    onSettled: refresh,
  });
  const move = useMutation({
    mutationFn: async ({ models, gcode, target }: { models: string[]; gcode: string[]; target: string }) => {
      await runFolderOperations([
        ...(models.length ? [{ label: 'Models', run: () => checkMove(apiClient.moveModel3dFiles(models, target)) }] : []),
        ...(gcode.length ? [{ label: 'G-code', run: () => checkMove(apiClient.moveGcodeFiles(gcode, target)) }] : []),
      ]);
    },
    onSuccess: () => toast.success('Files moved'),
    onSettled: async () => {
      // Either endpoint can partially apply a batch; refresh and require a fresh selection on failure.
      setMoveSelection(null);
      onMoved();
      await refresh();
    },
  });
  const busy = create.isPending || move.isPending;
  const unavailable = folders.isPending || folders.isError || busy;
  const selectedCount = modelIds.length + gcodeIds.length;
  const trimmedName = name.trim();
  const validName = trimmedName.length > 0 && trimmedName !== '.' && trimmedName !== '..'
    && !/[\\/]/.test(trimmedName)
    && !Array.from(trimmedName).some(char => char.charCodeAt(0) < 0x20 || char.charCodeAt(0) === 0x7f);
  const segments = path.split('/').filter(Boolean);
  const folderOptions = paths.map(folder => <option key={folder} value={folder}>{folderLabel(folder)}</option>);

  return (
    <>
      <div id={controls?.id} hidden={controls ? !controls.expanded : false}>
        <div className="flex flex-wrap items-end gap-2">
          <FormField label="Folder" htmlFor={`${id}-folder`} className="min-w-0">
            <Select id={`${id}-folder`} containerClassName="w-48 max-w-full" value={path} onChange={event => onNavigate(event.target.value)} disabled={busy}>
              <option value="">All folders</option>
              {folderOptions}
            </Select>
          </FormField>
          {canCreate && <Button size="sm" variant="secondary" disabled={unavailable} onClick={() => {
            create.reset(); setName(''); setCreating(true);
          }}>New folder</Button>}
          {selectedCount > 0 && <span className="self-center text-xs text-pf-text-secondary">{selectedCount} selected</span>}
          {selectedCount > 0 && canMove && <Button size="sm" variant="secondary" disabled={unavailable} onClick={() => {
            move.reset(); setDestination(path || '/');
            setMoveSelection({ models: [...modelIds], gcode: [...gcodeIds] });
          }}>Move to folder...</Button>}
          {path && <nav aria-label="Folder path" className="flex min-w-0 flex-wrap items-center gap-1 text-xs">
            <Button size="sm" variant="subtle" disabled={busy}
              onClick={() => onNavigate('')}>All folders</Button>
            <span aria-hidden="true">/</span>
            <Button size="sm" variant="subtle" disabled={busy} aria-current={path === '/' ? 'page' : undefined}
              onClick={() => onNavigate('/')}>Root</Button>
            {segments.map((segment, index) => (
              <Fragment key={index}>
                <span aria-hidden="true">/</span>
                <Button size="sm" variant="subtle" disabled={busy} aria-current={index === segments.length - 1 ? 'page' : undefined}
                  onClick={() => onNavigate('/' + segments.slice(0, index + 1).join('/'))}>{segment}</Button>
              </Fragment>
            ))}
          </nav>}
        </div>
      </div>
      {folders.isError && <div role="alert" className="text-sm text-pf-error">
        Could not load folders: {getErrorMessage(folders.error, 'Request failed')}{' '}
        <Button size="sm" variant="secondary" disabled={folders.isFetching} onClick={() => void folders.refetch()}>Retry folders</Button>
      </div>}
      {move.isError && <p role="alert" className="text-sm text-pf-error">
        Move incomplete: {getErrorMessage(move.error, 'Request failed')}. Some files may have moved. Review the refreshed library and reselect remaining files before retrying.
      </p>}
      {!creating && create.isError && <p role="alert" className="text-sm text-pf-error">
        Folder creation incomplete: {getErrorMessage(create.error, 'Request failed')}. The folder may exist in only one library.
      </p>}
      <Modal isOpen={creating} title="New folder" size="sm" isDisabled={busy} onClose={() => setCreating(false)}
        footer={<>
          <Button variant="secondary" disabled={busy} onClick={() => setCreating(false)}>Cancel</Button>
          <Button type="submit" form={`${id}-create`} disabled={unavailable || !validName}>Create folder</Button>
        </>}>
        <form id={`${id}-create`} className="space-y-3" onSubmit={event => {
          event.preventDefault();
          if (!validName || unavailable || !canCreate) return;
          create.mutate(`${path === '/' ? '' : path}/${trimmedName}`);
        }}>
          <p className="text-sm text-pf-text-secondary">Create in {path || 'Root'}. Available to models and G-code.</p>
          <Label htmlFor={`${id}-name`}>Folder name</Label>
          <Input id={`${id}-name`} value={name} onChange={event => setName(event.target.value)} disabled={busy} data-autofocus />
          {name && !validName && <p role="alert" className="text-sm text-pf-error">Enter one folder name, without slashes, control characters, "." or "..".</p>}
          {create.isError && <p role="alert" className="text-sm text-pf-error">
            Folder creation incomplete: {getErrorMessage(create.error, 'Request failed')}. The folder may exist in only one library; retry to finish creating it.
          </p>}
        </form>
      </Modal>
      <Modal isOpen={moveSelection !== null} title="Move to folder" size="sm" isDisabled={busy} onClose={() => setMoveSelection(null)}
        footer={<>
          <Button variant="secondary" disabled={busy} onClick={() => setMoveSelection(null)}>Cancel</Button>
          <Button disabled={unavailable || !canMove} onClick={() => {
            if (moveSelection) move.mutate({ ...moveSelection, target: destination });
          }}>Move files</Button>
        </>}>
        <p className="mb-3 text-sm text-pf-text-secondary">Move {(moveSelection?.models.length ?? 0) + (moveSelection?.gcode.length ?? 0)} selected files.</p>
        <Label htmlFor={`${id}-destination`}>Destination folder</Label>
        <Select id={`${id}-destination`} value={destination} onChange={event => setDestination(event.target.value)} disabled={busy}>
          {folderOptions}
        </Select>
      </Modal>
    </>
  );
}
