import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { GridView } from '../components/GridView';
import { ExplorerView } from '../components/ExplorerView';
import type { ColumnDef, FileItem, FolderNode } from '../types';

const files: FileItem[] = [
  { id: '1', path: '/file.gcode', fileName: 'file.gcode', isDirectory: false, size: 1024 },
  { id: '2', path: '/dir', fileName: 'dir', isDirectory: true },
];

const folders: FolderNode[] = [
  { path: '/', name: 'Root', children: [{ path: '/dir', name: 'dir', children: [] }] },
];

const columns: ColumnDef[] = [
  { key: 'fileName', label: 'Name', sortable: true },
];

describe('GridView', () => {
  it('supports select all', async () => {
    const onSelectAll = vi.fn();
    render(
      <GridView
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={onSelectAll}
        renderItemActions={() => null}
      />
    );

    await userEvent.click(screen.getByLabelText('Select all files'));
    expect(onSelectAll).toHaveBeenCalled();
  });
});

describe('ExplorerView', () => {
  it('supports sorting and select all', async () => {
    const onSort = vi.fn();
    const onSelectAll = vi.fn();

    render(
      <ExplorerView
        folders={folders}
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={onSelectAll}
        onNavigate={vi.fn()}
        currentPath="/"
        renderItemActions={() => null}
        sortBy="fileName"
        sortOrder="asc"
        onSort={onSort}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
        pageSize={25}
        onPageSizeChange={vi.fn()}
        columns={columns}
      />
    );

    await userEvent.click(screen.getByLabelText('Select all files'));
    expect(onSelectAll).toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: /Sort by Name/i }));
    expect(onSort).toHaveBeenCalledWith('fileName');
  });
});
