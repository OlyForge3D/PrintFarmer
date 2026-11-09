import '@testing-library/jest-dom';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PrintProjectStatus, type PrintProjectListDto } from '@/types/api';
import { ProjectCard } from './ProjectsPage';

const project: PrintProjectListDto = {
  id: 'project-1',
  name: 'Calibration batch',
  description: 'A project used to verify card activation.',
  status: PrintProjectStatus.InProgress,
  priority: 1,
  totalFiles: 4,
  completedFiles: 2,
  totalPrints: 6,
  completedPrints: 3,
  progressPercent: 50,
  createdAt: '2025-01-01T00:00:00Z',
};

describe('ProjectCard', () => {
  it('uses the full card as the primary action and keeps delete separate', () => {
    const onClick = vi.fn();
    const onDelete = vi.fn();
    render(<ProjectCard project={project} onClick={onClick} onDelete={onDelete} />);

    const openButton = screen.getByRole('button', { name: 'Open project Calibration batch' });
    const deleteButton = screen.getByRole('button', { name: 'Delete project Calibration batch' });
    const cardContent = screen.getByText('Calibration batch').closest('[class*="pointer-events-none"]');

    expect(openButton).toHaveClass('absolute', 'inset-0');
    expect(cardContent).toBeInTheDocument();
    expect(openButton).not.toContainElement(deleteButton);

    fireEvent.click(openButton);
    expect(onClick).toHaveBeenCalledOnce();
    expect(onDelete).not.toHaveBeenCalled();

    fireEvent.click(deleteButton);
    expect(onDelete).toHaveBeenCalledOnce();
    expect(onClick).toHaveBeenCalledOnce();
  });
});
