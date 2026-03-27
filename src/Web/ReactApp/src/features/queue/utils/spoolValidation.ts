import { apiClient } from '@/services/api';
import type { PrinterSpoolInfo } from '@/types/api';

export interface SpoolValidationContext {
  jobId: string;
  jobName: string;
  requiredMaterial?: string;
  printerId: string;
  printerName: string;
  spoolInfo?: PrinterSpoolInfo;
}

/**
 * Detects spool issues: no spool loaded, or material mismatch.
 * Returns the issue type or null if everything is fine.
 */
export function detectSpoolIssue(ctx: SpoolValidationContext): 'no-spool' | 'material-mismatch' | null {
  if (!ctx.spoolInfo?.hasActiveSpool) return 'no-spool';

  if (ctx.requiredMaterial && ctx.spoolInfo?.material) {
    const loaded = ctx.spoolInfo.material.toLowerCase().trim();
    const required = ctx.requiredMaterial.toLowerCase().trim();
    if (loaded !== required) return 'material-mismatch';
  }

  return null;
}

/**
 * Validates spool state for a printer before dispatch.
 * Returns the validation context if there's an issue, or null if everything is fine.
 */
export async function validateSpoolForDispatch(
  job: { id: string; name?: string; requiredMaterialType?: string; filamentName?: string },
  printer: { id: string; name: string },
): Promise<SpoolValidationContext | null> {
  try {
    const fullPrinter = await apiClient.getPrinter(printer.id);
    const spoolInfo = fullPrinter.spoolInfo;
    const requiredMaterial = job.requiredMaterialType || undefined;

    const ctx: SpoolValidationContext = {
      jobId: job.id,
      jobName: job.name || 'Unknown Job',
      requiredMaterial,
      printerId: printer.id,
      printerName: printer.name,
      spoolInfo: spoolInfo || undefined,
    };

    const issue = detectSpoolIssue(ctx);
    return issue ? ctx : null;
  } catch {
    // If we can't fetch printer data, don't block dispatch
    return null;
  }
}
