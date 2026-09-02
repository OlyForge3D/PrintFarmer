import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { CreateProjectModal } from '../CreateProjectModal';
import type { ApiError } from '@/types/api';

// Regression tests for issue #2368: submitting a project name over the
// backend's 255-character limit used to surface as an HTTP 500 whose caught
// error rendered as the literal string "[object Object]", because
// `apiClient` rejects with a plain `ApiError` object (not an `Error`
// instance) and the modal used to call `String(activeMutation.error)` on it.
// These tests lock in both halves of the fix: the client now refuses to
// submit an over-length name, and the modal now extracts `.message` via
// `getErrorMessage` instead of stringifying the raw error object when the
// API does reject a request.

const createProjectMock = vi.hoisted(() => vi.fn());

vi.mock('@/services/projectService', () => ({
  projectService: {
    createProject: (...args: unknown[]) => createProjectMock(...args),
    updateProject: vi.fn(),
    removeFileFromProject: vi.fn(),
    addFileToProject: vi.fn(),
    updateProjectFile: vi.fn(),
  },
}));

vi.mock('@/services/templateService', () => ({
  templateService: {
    getTemplates: vi.fn().mockResolvedValue([]),
    getTemplate: vi.fn(),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getFilaments: vi.fn().mockResolvedValue([]),
  },
}));

function renderModal() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <CreateProjectModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    </QueryClientProvider>
  );
}

describe('CreateProjectModal - name length validation and error rendering (#2368)', () => {
  beforeEach(() => {
    createProjectMock.mockReset();
  });

  it('caps the name input at 255 characters so the client cannot submit an over-length Unicode name', async () => {
    renderModal();

    const nameInput = screen.getByPlaceholderText('e.g., Voron 2.4 Build Kit') as HTMLInputElement;
    expect(nameInput).toHaveAttribute('maxlength', '255');

    // The exact repro from #2368: 295 UTF-16 code units built from "測試🚀"
    // repeated. Composed as 73 full repetitions (292 code units) plus "測試"
    // then "測" (3 more BMP code units) rather than slicing the repeated
    // string, so no surrogate pair is split in half.
    const longName = '測試🚀'.repeat(73) + '測試' + '測';
    expect(longName.length).toBe(295);

    // Drive real keystrokes through userEvent, which (unlike a raw jsdom
    // "input" event dispatch) respects the native maxLength constraint the
    // same way a real browser does, refusing to insert characters beyond it.
    const user = userEvent.setup();
    await user.click(nameInput);
    await user.paste(longName);

    await waitFor(() => {
      expect(nameInput.value.length).toBe(255);
    });
    expect(nameInput.value).toBe(longName.slice(0, 255));
  });

  it('renders the backend validation message instead of "[object Object]" when the API rejects an over-length name', async () => {
    const apiError: ApiError = {
      message: 'Project name must be 255 characters or fewer (received 295).',
      statusCode: 400,
      isAxiosError: true,
    };
    createProjectMock.mockRejectedValueOnce(apiError);

    renderModal();

    const nameInput = screen.getByPlaceholderText('e.g., Voron 2.4 Build Kit');
    const user = userEvent.setup();
    await user.type(nameInput, 'A perfectly valid name');

    await user.click(screen.getByRole('button', { name: 'Create Project' }));

    const errorText = await screen.findByText(
      /Failed to create project: Project name must be 255 characters or fewer/
    );
    expect(errorText).toBeInTheDocument();
    expect(errorText.textContent).not.toContain('[object Object]');
  });
});
