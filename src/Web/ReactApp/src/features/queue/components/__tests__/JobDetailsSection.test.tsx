import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@testing-library/jest-dom';
import JobDetailsSection from '../JobDetailsSection';
import { PrintJobPriority } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getFilaments: vi.fn().mockResolvedValue([]),
  },
}));

describe('JobDetailsSection priority', () => {
  it('renders and emits all four canonical priority names', async () => {
    const onFieldChange = vi.fn();

    const { rerender } = render(
      <JobDetailsSection
        jobDetails={{
          id: 'job-1',
          name: 'priority.gcode',
          status: 'Queued',
          priority: PrintJobPriority.Normal,
          queuePosition: 1,
        }}
        isEditing
        onFieldChange={onFieldChange}
      />,
    );

    await waitFor(() => expect(screen.getAllByRole('radio')).toHaveLength(4));
    expect(screen.getByRole('radiogroup', { name: 'Priority' })).toBeInTheDocument();
    for (const priority of Object.values(PrintJobPriority)) {
      const currentPriority = priority === PrintJobPriority.Low
        ? PrintJobPriority.Normal
        : PrintJobPriority.Low;
      rerender(
        <JobDetailsSection
          jobDetails={{
            id: 'job-1',
            name: 'priority.gcode',
            status: 'Queued',
            priority: currentPriority,
            queuePosition: 1,
          }}
          isEditing
          onFieldChange={onFieldChange}
        />,
      );
      fireEvent.click(screen.getByRole('radio', { name: priority }));
      expect(onFieldChange).toHaveBeenLastCalledWith('priority', priority);
    }
  });
});
