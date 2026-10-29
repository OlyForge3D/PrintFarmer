import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { LocalLibraryFolders } from '../LocalLibraryFolders';

vi.mock('@/services/api', () => ({
  apiClient: {
    listModelsFolders: vi.fn(),
    getGcodeFilesFolders: vi.fn(),
    createModelFolder: vi.fn(),
    createGcodeDirectory: vi.fn(),
    moveModel3dFiles: vi.fn(),
    moveGcodeFiles: vi.fn(),
  },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn() } }));

const folder = (path: string) => ({ path, fileName: path.split('/').at(-1) || '/', isDirectory: true });

function setup(overrides: Partial<React.ComponentProps<typeof LocalLibraryFolders>> = {}) {
  const props = {
    path: '',
    onNavigate: vi.fn(),
    modelIds: [] as string[],
    gcodeIds: [] as string[],
    canCreate: true,
    canMove: true,
    onMoved: vi.fn(),
    ...overrides,
  };
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
  render(<QueryClientProvider client={queryClient}><LocalLibraryFolders {...props} /></QueryClientProvider>);
  return { props, invalidate };
}

async function openCreate(name: string) {
  await waitFor(() => expect(screen.getByRole('button', { name: 'New folder' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'New folder' }));
  fireEvent.change(screen.getByLabelText('Folder name'), { target: { value: name } });
}

async function openMove() {
  await waitFor(() => expect(screen.getByRole('button', { name: 'Move to folder...' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Move to folder...' }));
}

describe('LocalLibraryFolders', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.listModelsFolders).mockResolvedValue([folder('/models/deep'), folder('/shared')]);
    vi.mocked(apiClient.getGcodeFilesFolders).mockResolvedValue([folder('/gcode'), folder('/shared')]);
    vi.mocked(apiClient.createModelFolder).mockResolvedValue(undefined);
    vi.mocked(apiClient.createGcodeDirectory).mockResolvedValue({ success: true });
    vi.mocked(apiClient.moveModel3dFiles).mockResolvedValue({ success: true });
    vi.mocked(apiClient.moveGcodeFiles).mockResolvedValue({ success: true });
  });

  it('offers All folders, Root, and the deduplicated union including parent paths', async () => {
    const { props } = setup();
    const select = screen.getByRole('combobox', { name: 'Folder' });
    await waitFor(() => expect(within(select).getAllByRole('option')).toHaveLength(6));
    expect(within(select).getAllByRole('option').map(option => (option as HTMLOptionElement).value))
      .toEqual(['', '/', '/gcode', '/models', '/models/deep', '/shared']);
    expect(select).toHaveValue('');
    fireEvent.change(select, { target: { value: '/models/deep' } });
    expect(props.onNavigate).toHaveBeenCalledWith('/models/deep');
    fireEvent.change(select, { target: { value: '/' } });
    expect(props.onNavigate).toHaveBeenLastCalledWith('/');
    fireEvent.change(select, { target: { value: '' } });
    expect(props.onNavigate).toHaveBeenLastCalledWith('');
  });

  it('provides clickable ancestors and an explicit way back to all folders', async () => {
    const { props } = setup({ path: '/models/deep' });
    const breadcrumb = within(screen.getByRole('navigation', { name: 'Folder path' }));
    expect(breadcrumb.getByRole('button', { name: 'deep' })).toHaveAttribute('aria-current', 'page');
    for (const [label, path] of [['models', '/models'], ['Root', '/'], ['All folders', '']]) {
      fireEvent.click(breadcrumb.getByRole('button', { name: label }));
      expect(props.onNavigate).toHaveBeenLastCalledWith(path);
    }
    await waitFor(() => expect(screen.getByRole('button', { name: 'New folder' })).toBeEnabled());
  });

  it.each(['', '/models'])('creates a folder in both libraries relative to %j', async path => {
    const { props, invalidate } = setup({ path });
    await openCreate('  Parts  ');
    fireEvent.click(screen.getByRole('button', { name: 'Create folder' }));
    const target = `${path}/Parts`;
    await waitFor(() => expect(props.onNavigate).toHaveBeenCalledWith(target));
    expect(apiClient.createModelFolder).toHaveBeenCalledWith(target);
    expect(apiClient.createGcodeDirectory).toHaveBeenCalledWith(target);
    expect(toast.success).toHaveBeenCalledWith('Folder created');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['local-library-folders'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
  });

  it.each(['.', '..', 'a/b', 'a\\b', '   '])('rejects an invalid folder name %j without a request', async name => {
    setup();
    await openCreate(name);
    expect(screen.getByRole('button', { name: 'Create folder' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('Enter one folder name');
    expect(apiClient.createModelFolder).not.toHaveBeenCalled();
    expect(apiClient.createGcodeDirectory).not.toHaveBeenCalled();
  });

  it('surfaces partial creation and retries only the registry still missing the folder', async () => {
    vi.mocked(apiClient.createGcodeDirectory).mockRejectedValueOnce(new Error('Storage unavailable'));
    const { props } = setup();
    await openCreate('Parts');
    vi.mocked(apiClient.listModelsFolders).mockResolvedValue([folder('/Parts')]);
    fireEvent.click(screen.getByRole('button', { name: 'Create folder' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('G-code: Storage unavailable');
    expect(screen.getByRole('alert')).toHaveTextContent('may exist in only one library');
    expect(props.onNavigate).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create folder' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Create folder' }));
    await waitFor(() => expect(props.onNavigate).toHaveBeenCalledWith('/Parts'));
    expect(apiClient.createModelFolder).toHaveBeenCalledTimes(1);
    expect(apiClient.createGcodeDirectory).toHaveBeenCalledTimes(2);
  });

  it('accepts an already-existing G-code directory reported as a conflict', async () => {
    vi.mocked(apiClient.createGcodeDirectory).mockRejectedValue({ statusCode: 409, message: 'Already exists' });
    const { props } = setup();
    await openCreate('Parts');
    fireEvent.click(screen.getByRole('button', { name: 'Create folder' }));
    await waitFor(() => expect(props.onNavigate).toHaveBeenCalledWith('/Parts'));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('moves a mixed selection to the chosen folder with the matching endpoint for each type', async () => {
    const { props, invalidate } = setup({ modelIds: ['model-1'], gcodeIds: ['gcode-1'] });
    await openMove();
    fireEvent.change(screen.getByRole('combobox', { name: 'Destination folder' }), { target: { value: '/shared' } });
    fireEvent.click(screen.getByRole('button', { name: 'Move files' }));
    await waitFor(() => expect(props.onMoved).toHaveBeenCalledTimes(1));
    expect(apiClient.moveModel3dFiles).toHaveBeenCalledWith(['model-1'], '/shared');
    expect(apiClient.moveGcodeFiles).toHaveBeenCalledWith(['gcode-1'], '/shared');
    expect(toast.success).toHaveBeenCalledWith('Files moved');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
    expect(screen.queryByRole('combobox', { name: 'Destination folder' })).not.toBeInTheDocument();
  });

  it.each(['rejected', 'unsuccessful'])('reports a %s move after refreshing and clearing selection', async outcome => {
    if (outcome === 'rejected') {
      vi.mocked(apiClient.moveGcodeFiles).mockRejectedValueOnce(new Error('Move denied'));
    } else {
      vi.mocked(apiClient.moveGcodeFiles).mockResolvedValueOnce({ success: false, message: 'Move denied' });
    }
    const { props, invalidate } = setup({ modelIds: ['model-1'], gcodeIds: ['gcode-1'] });
    await openMove();
    fireEvent.click(screen.getByRole('button', { name: 'Move files' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Move incomplete: G-code: Move denied');
    expect(screen.getByRole('alert')).toHaveTextContent('Some files may have moved');
    expect(props.onMoved).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
    expect(toast.success).not.toHaveBeenCalled();
    expect(screen.queryByRole('combobox', { name: 'Destination folder' })).not.toBeInTheDocument();
  });

  it('blocks writes when folder discovery fails and provides a working retry', async () => {
    vi.mocked(apiClient.listModelsFolders).mockRejectedValueOnce(new Error('Folders offline'));
    setup({ modelIds: ['model-1'] });
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load folders: Folders offline');
    expect(screen.getByRole('button', { name: 'New folder' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Move to folder...' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Retry folders' }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'New folder' })).toBeEnabled();
    expect(apiClient.listModelsFolders).toHaveBeenCalledTimes(2);
  });

  it('hides write controls without the corresponding permission', async () => {
    setup({ canCreate: false, canMove: false, modelIds: ['model-1'] });
    expect(screen.queryByRole('button', { name: 'New folder' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Move to folder...' })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Folder' })).toContainHTML('/models/deep'));
  });
});
