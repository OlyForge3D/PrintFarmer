import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ProjectsTableView } from '../ProjectsTableView';
import { PrintProjectStatus, type PrintProjectListDto } from '@/types/api';

const makeProject = (id: string, overrides: Partial<PrintProjectListDto>): PrintProjectListDto => ({
  id, name: id, status: PrintProjectStatus.Open, priority: 0, totalFiles: 0, completedFiles: 0,
  totalPrints: 0, completedPrints: 0, createdAt: '2026-01-01', progressPercent: 0, ...overrides,
});
const projects = [
  makeProject('Zeta', { totalFiles: 100, totalPrints: 100, progressPercent: 100, estimatedTotalCost: 100, dueDate: '2026-02-01', createdAt: '2026-02-01' }),
  makeProject('Alpha', { totalFiles: 2, totalPrints: 2, progressPercent: 2, estimatedTotalCost: 2, dueDate: '2026-01-01' }),
  makeProject('Missing', { totalFiles: 10, totalPrints: 10, progressPercent: 10, estimatedTotalCost: null, createdAt: '2026-01-15' }),
];
const order = () => within(screen.getByRole('table', { name: 'Projects list' })).getAllByRole('row')
  .slice(1).map(row => within(row).getAllByRole('cell')[0].textContent);

describe('ProjectsTableView sorting', () => {
  it.each([
    ['Files', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Prints', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Progress', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
    ['Est. Cost', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
    ['Due Date', ['Alpha', 'Zeta', 'Missing'], ['Zeta', 'Alpha', 'Missing']],
    ['Created', ['Alpha', 'Missing', 'Zeta'], ['Zeta', 'Missing', 'Alpha']],
  ])('sorts %s by numeric/date values and restores default order', async (label, ascending, descending) => {
    const user = userEvent.setup();
    render(<ProjectsTableView projects={projects} onProjectClick={vi.fn()} onDelete={vi.fn()} />);
    const header = screen.getByRole('columnheader', { name: label });
    const button = within(header).getByRole('button', { name: label });
    expect(header).toHaveAttribute('aria-sort', 'none');
    await user.click(button);
    expect(order()).toEqual(ascending);
    expect(header).toHaveAttribute('aria-sort', 'ascending');
    await user.click(button);
    expect(order()).toEqual(descending);
    expect(header).toHaveAttribute('aria-sort', 'descending');
    await user.click(button);
    expect(order()).toEqual(['Zeta', 'Alpha', 'Missing']);
    expect(projects.map(project => project.id)).toEqual(['Zeta', 'Alpha', 'Missing']);
  });

  it('keeps row and delete actions attached to the displayed project after keyboard sorting', async () => {
    const user = userEvent.setup();
    const onProjectClick = vi.fn();
    const onDelete = vi.fn();
    render(<ProjectsTableView projects={projects} onProjectClick={onProjectClick} onDelete={onDelete} />);
    screen.getByRole('button', { name: 'Name' }).focus();
    await user.keyboard('{Enter}');
    expect(order()).toEqual(['Alpha', 'Missing', 'Zeta']);
    const row = screen.getAllByRole('row')[1];
    await user.click(within(row).getByText('Alpha'));
    expect(onProjectClick).toHaveBeenCalledWith(projects[1]);
    await user.click(within(row).getByRole('button'));
    expect(onDelete.mock.calls[0][1]).toBe('Alpha');
    expect(onProjectClick).toHaveBeenCalledTimes(1);
    screen.getByRole('button', { name: 'Name' }).focus();
    await user.keyboard(' ');
    expect(order()).toEqual(['Zeta', 'Missing', 'Alpha']);
  });
});
