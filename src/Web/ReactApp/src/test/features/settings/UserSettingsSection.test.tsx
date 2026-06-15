import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { toast } from 'sonner';
import { UserSettingsSection } from '@/features/settings/components/UserSettingsSection';
import type { UserSettingsResponse } from '@/features/settings/types';

const mockUseUserSettings = vi.fn();
const mockUseUpdateUserSettings = vi.fn();
const mockMutate = vi.fn();

vi.mock('sonner', () => ({
  toast: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

vi.mock('@/features/settings/hooks/useUserSettings', () => ({
  useUserSettings: () => mockUseUserSettings(),
  useUpdateUserSettings: () => mockUseUpdateUserSettings(),
}));

const baseUserSettings: UserSettingsResponse = {
  userId: 'user-1',
  theme: 'dark',
  locale: 'en',
  itemsPerPage: 25,
  defaultSlicerPreset: null,
  printablesUsername: '',
  rowVersion: 'AAAAABCD',
};

describe('UserSettingsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();

    mockUseUserSettings.mockReturnValue({
      data: baseUserSettings,
      isLoading: false,
      error: null,
      refetch: vi.fn(),
      isFetching: false,
    });

    mockUseUpdateUserSettings.mockReturnValue({
      mutate: mockMutate,
      isPending: false,
    });
  });

  it("blocks saving when Printables username starts with '@'", () => {
    render(<UserSettingsSection />);

    fireEvent.change(screen.getByLabelText('Printables username'), { target: { value: '@ripley' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    expect(toast.error).toHaveBeenCalledWith("Printables username must not begin with '@'.");
    expect(mockMutate).not.toHaveBeenCalled();
  });

  it('keeps save behavior unchanged for valid Printables usernames', () => {
    render(<UserSettingsSection />);

    fireEvent.change(screen.getByLabelText('Printables username'), { target: { value: '  ripley  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    expect(mockMutate).toHaveBeenCalledTimes(1);
    const [payload, options] = mockMutate.mock.calls[0] as [{ printablesUsername: string }, { onSuccess?: () => void }];
    expect(payload.printablesUsername).toBe('ripley');

    options.onSuccess?.();
    expect(toast.success).toHaveBeenCalledWith('Preferences saved.');
  });

  it("surfaces backend username validation errors with explicit '@' guidance", () => {
    mockMutate.mockImplementation((_payload: unknown, options?: { onError?: (error: Error) => void }) => {
      options?.onError?.({
        message: "Printables username must not begin with '@'.",
        name: 'ApiError',
      });
    });

    render(<UserSettingsSection />);

    fireEvent.change(screen.getByLabelText('Printables username'), { target: { value: 'ripley' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    expect(toast.error).toHaveBeenCalledWith("Printables username must not begin with '@'.");
  });
});
