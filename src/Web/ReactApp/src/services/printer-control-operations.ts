import { z } from 'zod';
import { apiClient } from '@/services/api';
import { mutationErrorStatus } from '@/common/utils/mutationError';
import {
  isControlOperationResolved,
  matchesPrinterControlIntent,
  printerControlKindSchema as kindSchema,
  printerControlStateSchema as stateSchema,
  printerControlOperationSchema as operationSchema,
} from '@/types/api';
export { isControlOperationResolved } from '@/types/api';
import type {
  CommandResult, PrinterControlCurrent, PrinterControlIntent, PrinterControlOperation,
  PrinterControlOperationResponse, PrinterControlRecovery,
} from '@/types/api';

const currentSchema = z.object({
  physicalControl: z.object({
    supportedOperations: z.array(kindSchema), barrierHeld: z.boolean(),
    operationId: z.string().uuid().nullable(), state: stateSchema.nullable(), requiresRecovery: z.boolean(),
  }),
  operation: operationSchema.nullable(),
});
const savedSchema = z.object({
  operationId: z.string().uuid(),
  intent: z.object({ kind: kindSchema, x: z.number().optional(), y: z.number().optional(), z: z.number().optional(), f: z.number().optional() }),
  admissionConfirmed: z.boolean().optional(),
});
type SavedOperation = z.infer<typeof savedSchema>;

export const CONTROL_RECHECK_MS = 2_000;
export interface ControlOperationSnapshot {
  current: PrinterControlCurrent | null;
  operation: PrinterControlOperation | null;
  etag: string | null;
  saved: SavedOperation | null;
  checking: boolean;
  submitting: boolean;
  uncertain: boolean;
  missingAdmission: boolean;
  error: string | null;
}

/** A REST-only authority. SignalR and timers can request reads, never complete motion. */
export class PrinterControlTracker {
  private snapshot: ControlOperationSnapshot;
  private listeners = new Set<() => void>();
  private refreshTask: Promise<PrinterControlOperation | null> | null = null;
  private invalidated = false;
  private storageInvalid = false;
  private completedOperation: PrinterControlOperation | null = null;
  private lastReadCompletedAt = Number.NEGATIVE_INFINITY;

  private waitForChange(signal: AbortSignal): Promise<void> {
    return new Promise(resolve => {
      const finish = () => {
        clearTimeout(timer);
        unsubscribe();
        signal.removeEventListener('abort', finish);
        resolve();
      };
      const unsubscribe = this.subscribe(finish);
      const timer = setTimeout(finish, CONTROL_RECHECK_MS);
      signal.addEventListener('abort', finish, { once: true });
      if (signal.aborted) finish();
    });
  }

  constructor(
    readonly printerId: string,
    private readonly storageKey: string,
    private readonly isSessionCurrent: () => boolean,
  ) {
    let saved: SavedOperation | null = null;
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) saved = savedSchema.parse(JSON.parse(raw));
    } catch {
      this.storageInvalid = true;
    }
    this.snapshot = {
      current: null, operation: null, etag: null, saved,
      checking: true, submitting: false, uncertain: true, missingAdmission: false,
      error: this.storageInvalid ? 'The saved motion receipt cannot be read. Motion remains locked; contact an administrator.' : null,
    };
  }

  getSnapshot = (): ControlOperationSnapshot => this.snapshot;
  getCompletedOperation = (): PrinterControlOperation | null => this.completedOperation;
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  private update(change: Partial<ControlOperationSnapshot>) {
    if (!this.isSessionCurrent()) return;
    this.snapshot = { ...this.snapshot, ...change };
    this.listeners.forEach(listener => listener());
  }

  private assertSession() {
    if (!this.isSessionCurrent()) throw new Error('Your session changed. Reopen the printer to recheck motion.');
  }

  isBlocked = (): boolean => {
    const { current, saved, checking, submitting, uncertain } = this.snapshot;
    return !this.isSessionCurrent() || this.storageInvalid || checking || submitting || uncertain || !!saved || !current ||
      current.physicalControl.barrierHeld || current.physicalControl.requiresRecovery ||
      (!!current.operation && !isControlOperationResolved(current.operation));
  };

  private validateOperation(response: PrinterControlOperationResponse, operationId: string) {
    const operation = operationSchema.parse(response.operation);
    if (operation.printerId !== this.printerId || operation.operationId !== operationId) {
      throw new Error('The operation receipt does not match this printer.');
    }
    this.validateSavedIntent(operation);
    return operation;
  }

  private validateSavedIntent(operation: PrinterControlOperation, saved = this.snapshot.saved): void {
    if (saved?.operationId === operation.operationId && !matchesPrinterControlIntent(operation, saved.intent)) {
      throw new Error('The operation receipt does not match the saved motion intent. Motion remains uncertain.');
    }
  }

  private validateCurrent(value: unknown): PrinterControlCurrent {
    const current = currentSchema.parse(value);
    const { operation, physicalControl } = current;
    if (operation) this.validateSavedIntent(operation);
    if ((!physicalControl.barrierHeld && (operation !== null || physicalControl.operationId !== null ||
      physicalControl.state !== null || physicalControl.requiresRecovery)) ||
      (operation && (operation.printerId !== this.printerId || physicalControl.state !== operation.state)) ||
      physicalControl.operationId !== (operation?.operationId ?? null) ||
      (!operation && !physicalControl.barrierHeld && physicalControl.state !== null)) {
      throw new Error('Inconsistent current motion status. Recheck required.');
    }
    return current;
  }

  private async confirmAdmission(operation: PrinterControlOperation): Promise<void> {
    this.validateSavedIntent(operation);
    const { operationId } = operation;
    if (this.snapshot.saved?.operationId !== operationId || this.snapshot.saved.admissionConfirmed) return;
    const confirm = () => {
      this.assertSession();
      const raw = localStorage.getItem(this.storageKey);
      const persisted = raw ? savedSchema.parse(JSON.parse(raw)) : null;
      if (!persisted || persisted.operationId !== operationId) {
        return;
      }
      this.validateSavedIntent(operation, persisted);
      const saved = { ...persisted, admissionConfirmed: true };
      localStorage.setItem(this.storageKey, JSON.stringify(saved));
      this.update({ saved, missingAdmission: false });
    };
    try {
      if (navigator.locks) await navigator.locks.request(this.storageKey, confirm);
      else confirm();
    } catch (error) {
      this.storageInvalid = true;
      throw error;
    }
  }

  canRetryAdmission = (): boolean => {
    const { saved, missingAdmission, current, operation, submitting } = this.snapshot;
    return this.isSessionCurrent() && !this.storageInvalid && !!saved && !saved.admissionConfirmed &&
      missingAdmission && !submitting && !!current && !current.physicalControl.barrierHeld &&
      !current.physicalControl.requiresRecovery && current.operation === null &&
      current.physicalControl.operationId === null && current.physicalControl.state === null &&
      operation?.operationId !== saved.operationId &&
      current.physicalControl.supportedOperations.includes(saved.intent.kind);
  };

  private async clearSavedReceipt(operation: PrinterControlOperation): Promise<void> {
    const clear = () => {
      this.assertSession();
      const raw = localStorage.getItem(this.storageKey);
      const persisted = raw ? savedSchema.parse(JSON.parse(raw)) : null;
      if (persisted?.operationId === operation.operationId) {
        this.validateSavedIntent(operation, persisted);
        localStorage.removeItem(this.storageKey);
      }
    };
    if (navigator.locks) await navigator.locks.request(this.storageKey, clear);
    else clear();
  }

  /** Coalesces overlapping reads; a hint received during a read schedules another read. */
  refresh = (): Promise<PrinterControlOperation | null> => {
    if (this.refreshTask) {
      this.invalidated = true;
      return this.refreshTask;
    }
    this.refreshTask = this.read().finally(() => {
      this.refreshTask = null;
      if (this.invalidated && this.isSessionCurrent()) {
        this.invalidated = false;
        void this.refresh().catch(() => undefined);
      }
    });
    return this.refreshTask;
  };

  /** Shared read-only cadence across mounted views and a waiting submission. */
  poll = (): Promise<PrinterControlOperation | null> => {
    if (this.refreshTask) return this.refreshTask;
    if (Date.now() - this.lastReadCompletedAt < CONTROL_RECHECK_MS) return Promise.resolve(this.snapshot.operation);
    return this.refresh();
  };

  private async read(): Promise<PrinterControlOperation | null> {
    this.assertSession();
    let missingAdmission = false;
    try {
      let current = this.validateCurrent(await apiClient.getCurrentPrinterControlOperation(this.printerId));
      this.assertSession();
      let saved = this.snapshot.saved;
      const operationId = saved?.operationId ?? current.operation?.operationId;
      let operation = current.operation;
      let tracked: PrinterControlOperation | null = null;
      let etag: string | null = null;
      if (operationId) {
        try {
          const response = await apiClient.getPrinterControlOperation(this.printerId, operationId);
          this.assertSession();
          tracked = this.validateOperation(response, operationId);
          operation = tracked;
          etag = response.etag;
        } catch (error) {
          this.assertSession();
          if (mutationErrorStatus(error) === 404 && current.operation) await this.confirmAdmission(current.operation);
          missingAdmission = !!saved && !this.snapshot.saved?.admissionConfirmed && mutationErrorStatus(error) === 404;
          this.update({ current, missingAdmission });
          throw error;
        }
      }
      if (saved && tracked && isControlOperationResolved(tracked)) {
        // Terminal receipt is not permission to move: reread current to find a successor.
        const latest = this.validateCurrent(await apiClient.getCurrentPrinterControlOperation(this.printerId));
        this.assertSession();
        current = latest;
        if (latest.operation && latest.operation.operationId !== tracked.operationId) {
          const response = await apiClient.getPrinterControlOperation(this.printerId, latest.operation.operationId);
          this.assertSession();
          operation = this.validateOperation(response, latest.operation.operationId);
          etag = response.etag;
        }
        await this.clearSavedReceipt(tracked);
        this.assertSession();
        this.completedOperation = tracked;
      } else if (tracked) {
        await this.confirmAdmission(tracked);
        saved = this.snapshot.saved;
      }
      this.update({
        current, operation, etag, checking: false, missingAdmission: false,
        saved: saved && tracked && isControlOperationResolved(tracked) ? null : saved,
        uncertain: this.storageInvalid, error: this.storageInvalid ? this.snapshot.error : null,
      });
      return tracked;
    } catch (error) {
      const status = mutationErrorStatus(error);
      this.update({
        checking: false, uncertain: true, etag: null, missingAdmission,
        error: status === 404
          ? 'Motion status is unavailable: the printer or operation may be absent, inaccessible, or unsupported. Recheck access and server support; do not send another movement.'
          : status === 405 || status === 501
            ? 'Durable motion status is unsupported. Update the server before using Moonraker motion controls.'
            : 'Motion status is uncertain. Recheck the saved operation; do not send another movement.',
      });
      throw error;
    } finally {
      this.lastReadCompletedAt = Date.now();
    }
  }

  async execute(intent: PrinterControlIntent, signal: AbortSignal): Promise<CommandResult> {
    this.assertSession();
    if (this.isBlocked()) throw new Error(this.snapshot.error ?? 'Motion is locked pending an authoritative status check.');
    if (!this.snapshot.current?.physicalControl.supportedOperations.includes(intent.kind)) {
      throw new Error('This server does not support this durable motion operation. Update the server.');
    }
    const reserve = () => {
      this.assertSession();
      if (this.isBlocked()) throw new Error('Another motion is being tracked. Recheck before proceeding.');
      const existing = localStorage.getItem(this.storageKey);
      if (existing) {
        this.update({ saved: savedSchema.parse(JSON.parse(existing)), uncertain: true });
        throw new Error('Another tab saved a motion operation. Recheck its outcome before proceeding.');
      }
      const saved = savedSchema.parse({ operationId: crypto.randomUUID(), intent });
      // Fail closed if persistence is unavailable. Never send before the receipt is durable locally.
      localStorage.setItem(this.storageKey, JSON.stringify(saved));
      this.update({ saved, submitting: true, uncertain: true, error: null });
      return saved;
    };
    const saved = navigator.locks
      ? await navigator.locks.request(this.storageKey, reserve)
      : reserve();
    try {
      const response = await apiClient.createPrinterControlOperation(this.printerId, saved.operationId, saved.intent);
      this.assertSession();
      const operation = this.validateOperation(response, saved.operationId);
      this.validateSavedIntent(operation, saved);
      await this.confirmAdmission(operation);
    } catch {
      this.update({ error: 'The admission response was lost or rejected. Recheck this operation; it may still execute.' });
      await this.refresh().catch(() => undefined);
      throw new Error('Motion admission is uncertain. The original operation ID was retained; no command was replayed.');
    } finally {
      this.update({ submitting: false });
    }
    await this.refresh();
    while (!signal.aborted) {
      const operation = this.completedOperation?.operationId === saved.operationId ? this.completedOperation : this.snapshot.operation;
      this.assertSession();
      if (operation?.operationId === saved.operationId) {
        this.validateSavedIntent(operation, saved);
        if (isControlOperationResolved(operation)) {
          const success = operation.state === 'Succeeded' && operation.completionEvidence === 'MotionQueueDrained';
          return { success, error: success ? undefined : operation.failure?.message ?? `Motion ended as ${operation.state}, not a confirmed success.` };
        }
        if (operation.requiresRecovery || ['Unknown', 'Recovering'].includes(operation.state)) {
          throw new Error('Motion completion is unknown. An authorized operator must review recovery.');
        }
      }
      await this.waitForChange(signal);
      if (!signal.aborted) await this.poll();
    }
    throw new Error('Stopped waiting locally. The saved operation may still execute; reopen the printer to recheck.');
  }

  async retryAdmission(): Promise<void> {
    this.assertSession();
    const saved = this.snapshot.saved;
    if (!saved || !this.canRetryAdmission()) {
      throw new Error('Recheck before retrying admission with the original operation ID.');
    }
    this.update({ submitting: true });
    try {
      await this.refresh().catch(error => {
        if (mutationErrorStatus(error) !== 404) throw error;
      });
      this.assertSession();
      if (!this.snapshot.saved || this.snapshot.saved.admissionConfirmed) return;
      const { current, missingAdmission } = this.snapshot;
      const raw = localStorage.getItem(this.storageKey);
      const persisted = raw ? savedSchema.parse(JSON.parse(raw)) : null;
      if (!missingAdmission || !current || current.physicalControl.barrierHeld || current.physicalControl.requiresRecovery ||
        current.operation !== null || current.physicalControl.operationId !== null || current.physicalControl.state !== null ||
        !current.physicalControl.supportedOperations.includes(saved.intent.kind) ||
        !persisted || persisted.admissionConfirmed || JSON.stringify(persisted) !== JSON.stringify(saved)) {
        throw new Error('Admission or access changed. Recheck before confirming the original motion again.');
      }
      const response = await apiClient.createPrinterControlOperation(this.printerId, saved.operationId, saved.intent);
      this.assertSession();
      const operation = this.validateOperation(response, saved.operationId);
      this.validateSavedIntent(operation, saved);
      await this.confirmAdmission(operation);
    } finally {
      this.update({ submitting: false });
      await this.refresh().catch(() => undefined);
    }
  }

  async recover(reviewedOperationId: string, reviewedEtag: string, recovery?: PrinterControlRecovery): Promise<void> {
    this.assertSession();
    if (this.snapshot.submitting || this.snapshot.uncertain || !this.snapshot.operation?.requiresRecovery ||
      this.snapshot.operation.operationId !== reviewedOperationId || this.snapshot.etag !== reviewedEtag) {
      throw new Error('Motion status changed. Recheck and review the recovery prerequisites again.');
    }
    this.validateSavedIntent(this.snapshot.operation);
    this.update({ submitting: true });
    let invalidReceipt = false;
    try {
      const response = await apiClient.recoverPrinterControlOperation(this.printerId, reviewedOperationId, reviewedEtag, recovery);
      this.assertSession();
      try {
        this.validateOperation(response, reviewedOperationId);
      } catch (error) {
        invalidReceipt = true;
        this.update({ uncertain: true, etag: null, error: 'Recovery receipt is inconsistent. Recheck the saved motion; it remains uncertain.' });
        throw error;
      }
    } finally {
      this.update({ submitting: false });
      if (!invalidReceipt) await this.refresh().catch(() => undefined);
    }
  }
}
