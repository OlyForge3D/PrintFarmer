import { useMutationState } from '@tanstack/react-query';
import { getAuthEpoch } from '@/common/auth/authEpoch';
import { USER_SETTINGS_KEY, useUserSettings } from '@/features/settings/hooks/useUserSettings';
import type { UpdateUserSettingsRequest } from '@/features/settings/types';

export type PrinterControlsMode = 'guided' | 'expert';

/**
 * Read-only view of the account Printer Control Mode chosen in User Settings.
 * Presentation only; never an authorization or motion lock.
 */
export function usePrinterControlsMode() {
  const settings = useUserSettings();
  // Mutation-cache state reflects an in-flight User Settings save without browser persistence.
  // Ignore old-account saves just as useUpdateUserSettings ignores their responses.
  const saves = useMutationState({
    filters: { mutationKey: USER_SETTINGS_KEY },
    select: mutation => {
      const context = mutation.state.context as { epochAtStart: number } | undefined;
      return {
        epoch: context?.epochAtStart,
        variables: mutation.state.variables as UpdateUserSettingsRequest | undefined,
        status: mutation.state.status,
      };
    },
  });
  const latestModeSave = saves
    .filter(save => save.epoch === getAuthEpoch() && save.variables?.printerControlMode !== undefined)
    .at(-1);
  const savedMode: PrinterControlsMode = settings.data?.printerControlMode === 'Expert' ? 'expert' : 'guided';
  const mode: PrinterControlsMode = latestModeSave?.status === 'pending'
    ? latestModeSave.variables?.printerControlMode === 'Expert' ? 'expert' : 'guided'
    : savedMode;

  return { mode };
}
