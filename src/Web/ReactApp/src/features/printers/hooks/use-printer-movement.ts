import { useIsMutating, useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import type { CommandResult, MoveRequest, Printer } from '@/types/api';

export interface PrinterMovement extends MoveRequest {
  kind: 'HomeAll' | 'HomeXY' | 'HomeZ' | 'Jog' | 'MoveTo';
}

// Guard same-tick clicks across mounted controls before React publishes pending state.
const inFlight = new WeakMap<QueryClient, Set<string>>();

export function usePrinterMovement(printer?: Pick<Printer, 'id'>) {
  const queryClient = useQueryClient();
  const printerId = printer?.id;
  const mutationKey = ['printer-movement', printerId];
  const pendingCount = useIsMutating({ mutationKey, exact: true });
  const mutation = useMutation({
    mutationKey,
    retry: false,
    networkMode: 'always',
    mutationFn: async ({ kind, ...move }: PrinterMovement): Promise<CommandResult> => {
      if (!printerId) throw new Error('Select a printer first.');
      let active = inFlight.get(queryClient);
      if (!active) {
        active = new Set();
        inFlight.set(queryClient, active);
      }
      if (active.has(printerId)) throw new Error('Another movement command is pending. Wait before sending another command.');
      active.add(printerId);
      try {
        switch (kind) {
          case 'HomeAll': return await apiClient.homePrinter(printerId);
          case 'HomeXY': return await apiClient.homeXY(printerId);
          case 'HomeZ': return await apiClient.homeZ(printerId);
          case 'Jog': return await apiClient.movePrinter(printerId, move);
          case 'MoveTo': return await apiClient.movePrinterTo(printerId, move);
        }
      } catch (error) {
        throw new Error(`${mutationErrorMessage(error, 'Movement request failed')}. Check the printer before sending another command; it may have already moved.`);
      } finally {
        active.delete(printerId);
      }
    },
    onSuccess: result => {
      if (result.success) void queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    },
  });

  return { execute: mutation.mutateAsync, blocked: !printerId || pendingCount > 0 };
}
