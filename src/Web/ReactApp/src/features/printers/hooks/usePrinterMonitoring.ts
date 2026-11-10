import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import { ADMIN_OVERVIEW_QUERY_KEY } from '@/features/admin/hooks/useAdminOverview';
import type { Printer } from '@/types/api';

export function usePrinterMonitoring() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ printers, expectedOffline }: { printers: Printer[]; expectedOffline: boolean }) => {
      const failures: string[] = [];
      let updated = 0;
      for (const printer of printers) {
        try {
          if (!printer.rowVersion) {
            throw new Error('No reviewed revision. Refresh and review again.');
          }
          await apiClient.setPrinterExpectedOffline(printer.id, expectedOffline, printer.rowVersion);
          updated += 1;
        } catch (error) {
          failures.push(`${printer.name}: ${mutationErrorMessage(error, 'Could not change monitoring intent')}`);
        }
      }
      return { updated, failures, expectedOffline };
    },
    onSuccess: ({ updated, failures, expectedOffline }) => {
      if (updated > 0) {
        toast.success(`${updated} printer${updated === 1 ? '' : 's'} ${expectedOffline ? 'marked expected offline until reconnect' : 'restored to expected online'}`);
      }
      if (failures.length > 0) {
        toast.error(`Could not update ${failures.length} printer${failures.length === 1 ? '' : 's'}`, {
          description: failures.join('\n'),
          duration: 10_000,
        });
      }
    },
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['printers'] }),
        queryClient.invalidateQueries({ queryKey: ADMIN_OVERVIEW_QUERY_KEY }),
        queryClient.invalidateQueries({ queryKey: ['attention'] }),
      ]);
    },
  });
}
