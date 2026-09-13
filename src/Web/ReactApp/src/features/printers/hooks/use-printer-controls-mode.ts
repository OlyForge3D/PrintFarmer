import { useMutationState, useQueryClient } from '@tanstack/react-query';
import { getAuthEpoch } from '@/common/auth/authEpoch';
import { mutationErrorStatus } from '@/common/utils/mutationError';
import { USER_SETTINGS_KEY, useUserSettings, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';
import type { UpdateUserSettingsRequest } from '@/features/settings/types';

export type PrinterControlsMode = 'guided' | 'expert';

/** Account-backed presentation preference. Never an authorization or motion lock. */
export function usePrinterControlsMode() {
  const settings = useUserSettings();
  const update = useUpdateUserSettings();
  const queryClient = useQueryClient();
  // Mutation-cache state is shared by detail/sidebar without browser persistence.
  // Ignore old-account saves just as useUpdateUserSettings ignores their responses.
  const saves = useMutationState({
    filters: { mutationKey: USER_SETTINGS_KEY },
    select: mutation => {
      const context = mutation.state.context as { epochAtStart: number } | undefined;
      return {
        epoch: context?.epochAtStart,
        variables: mutation.state.variables as UpdateUserSettingsRequest | undefined,
        status: mutation.state.status,
        error: mutation.state.error,
      };
    },
  });
  const accountSaves = saves.filter(save => save.epoch === getAuthEpoch());
  const latestModeSave = accountSaves.filter(save => save.variables?.printerControlMode !== undefined).at(-1);
  const pending = accountSaves.some(save => save.status === 'pending');
  const savedMode = settings.data?.printerControlMode === 'Expert' ? 'expert' : 'guided';
  const mode: PrinterControlsMode = latestModeSave?.status === 'pending'
    ? latestModeSave.variables?.printerControlMode === 'Expert' ? 'expert' : 'guided'
    : savedMode;
  const saveError = latestModeSave?.status === 'error'
    ? mutationErrorStatus(latestModeSave.error) === 409
      ? 'Your preferences changed elsewhere. Reload preferences, then choose the control mode again.'
      : 'Could not save your control mode. The last saved mode is selected; choose a mode again to retry.'
    : null;
  const loadError = settings.error
    ? 'Could not load your control mode. Showing the last available mode, or Guided by default. Motion controls are unaffected.'
    : null;

  const setMode = (next: PrinterControlsMode) => {
    // Do not invent a settings revision or queue overlapping same-tab writes.
    if (!settings.data || pending || queryClient.isMutating({
      mutationKey: USER_SETTINGS_KEY,
      predicate: mutation => {
        const context = mutation.state.context as { epochAtStart: number } | undefined;
        return !context || context.epochAtStart === getAuthEpoch();
      },
    }) > 0) return;
    update.mutate({
      printerControlMode: next === 'expert' ? 'Expert' : 'Guided',
      rowVersion: settings.data.rowVersion,
    });
  };

  return {
    mode, setMode, saveError, loadError, pending,
    loading: settings.isLoading,
    canSave: !!settings.data && !pending,
    reloading: settings.isFetching,
    reload: () => { void settings.refetch(); },
  };
}
