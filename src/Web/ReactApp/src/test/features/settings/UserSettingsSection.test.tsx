import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render as renderTree, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
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
  USER_SETTINGS_KEY: ['settings', 'user'],
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

function render(element: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactElement }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return renderTree(element, { wrapper });
}

describe('UserSettingsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockMutate.mockReset();

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

    act(() => options.onSuccess?.());
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
  it('preserves the latest printer mode when saving unrelated preferences', () => {
    const { rerender } = render(<UserSettingsSection />);
    fireEvent.change(screen.getByLabelText('Items per page'), { target: { value: '50' } });
    mockUseUserSettings.mockReturnValue({
      data: { ...baseUserSettings, printerControlMode: 'Expert', rowVersion: 'next-revision' },
      isLoading: false, error: null, refetch: vi.fn(), isFetching: false,
    });
    rerender(<UserSettingsSection />);
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));
    expect(mockMutate).toHaveBeenCalledWith(expect.objectContaining({
      itemsPerPage: 50, printerControlMode: 'Expert', rowVersion: 'next-revision',
    }), expect.any(Object));
  });

  it('defaults older responses to Guided and stages Expert until Save Preferences', () => {
    render(<UserSettingsSection />);
    expect(screen.getByRole('radio', { name: 'Guided' })).toBeChecked();
    expect(screen.getByRole('group', { name: 'Printer control mode' })).toHaveAccessibleDescription(/does not change permissions or protections/);
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    expect(mockMutate).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));
    expect(mockMutate).toHaveBeenCalledWith({
      theme: 'dark', printerControlMode: 'Expert', locale: 'en', itemsPerPage: 25,
      defaultSlicerPreset: null, printablesUsername: '', rowVersion: 'AAAAABCD',
    }, expect.any(Object));
  });

  it('refreshes untouched fields from cache while retaining a deliberate mode edit', () => {
    const { rerender } = render(<UserSettingsSection />);
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    mockUseUserSettings.mockReturnValue({
      data: { ...baseUserSettings, theme: 'matrix', locale: 'fr', itemsPerPage: 75,
        defaultSlicerPreset: 'quality', printablesUsername: 'maker', rowVersion: 'v2' },
      isLoading: false, error: null, isFetching: false,
    });
    rerender(<UserSettingsSection />);
    expect(screen.getByLabelText('Locale')).toHaveValue('fr');
    expect(screen.getByLabelText('Items per page')).toHaveValue(75);
    expect(screen.getByLabelText('Printables username')).toHaveValue('maker');
    expect(screen.getByRole('status')).toHaveTextContent(/Your unsaved edits are kept/);
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));
    expect(mockMutate).toHaveBeenCalledWith(expect.objectContaining({
      theme: 'matrix', printerControlMode: 'Expert', locale: 'fr', itemsPerPage: 75,
      defaultSlicerPreset: 'quality', printablesUsername: 'maker', rowVersion: 'v2',
    }), expect.any(Object));
  });

  it.each(['', '0', '201', '1.5'])('rejects invalid list size %s', (value) => {
    render(<UserSettingsSection />);
    fireEvent.change(screen.getByLabelText('Items per page'), { target: { value } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));
    expect(mockMutate).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('whole number between 1 and 200');
    expect(screen.getByLabelText('Items per page')).toHaveFocus();
    expect(screen.getByLabelText('Items per page')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Items per page')).toHaveAccessibleDescription('Items per page must be a whole number between 1 and 200.');
  });

  it('guards repeat submits before pending state renders', () => {
    render(<UserSettingsSection />);
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));
    expect(mockMutate).toHaveBeenCalledTimes(1);
  });

  it('retains edits on a background load error and disables saving until retry succeeds', () => {
    const { rerender } = render(<UserSettingsSection />);
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    const refetch = vi.fn();
    mockUseUserSettings.mockReturnValue({ data: baseUserSettings, error: new Error('offline'), isFetching: false, refetch });
    rerender(<UserSettingsSection />);
    expect(screen.getByRole('radio', { name: 'Expert' })).toBeChecked();
    expect(screen.getByRole('button', { name: 'Save Preferences' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('resets drafts when the signed-in account changes', () => {
    const { rerender } = render(<UserSettingsSection />);
    fireEvent.click(screen.getByRole('radio', { name: 'Expert' }));
    mockUseUserSettings.mockReturnValue({ data: { ...baseUserSettings, userId: 'other-user' }, isFetching: false });
    rerender(<UserSettingsSection />);
    expect(screen.getByRole('radio', { name: 'Guided' })).toBeChecked();
  });

  it.each([true, false])('announces unknown preferences without claiming an error (isLoading=%s)', (isLoading) => {
    mockUseUserSettings.mockReturnValue({ isLoading, isFetching: isLoading, data: undefined, error: null });
    render(<UserSettingsSection />);
    expect(screen.getByRole('status', { name: 'Loading user preferences' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Preferences' })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

});
