import { z } from 'zod';
import { apiClient } from '@/services/api';
import { mutationErrorStatus } from '@/common/utils/mutationError';
import { generateUUID } from '@/utils/uuid';
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
  PrinterControlOperationResponse,
} from '@/types/api';

const currentSchema = z.object({
  physicalControl: z.object({
    supportedOperations: z.array(kindSchema), barrierHeld: z.boolean(),
    operationId: z.string().uuid().nullable(), state: stateSchema.nullable(), requiresRecovery: z.boolean(),
  }),
  operation: operationSchema.nullable(),
});

export const CONTROL_RECHECK_MS = 1_000;
export interface ControlOperationSnapshot {
  current: PrinterControlCurrent | null;
  operation: PrinterControlOperation | null;
  saved: { operationId: string; intent: PrinterControlIntent } | null;
  checking: boolean;
  submitting: boolean;
  admitting: boolean;
  uncertain: boolean;
  error: string | null;
}

/** Session-memory tracking only. REST owns coordination; hints and timers never replay motion. */
export class PrinterControlTracker {
  private snapshot: ControlOperationSnapshot = {
    current: null, operation: null, saved: null,
    checking: true, submitting: false, admitting: false, uncertain: true, error: null,
  };
  private listeners = new Set<() => void>();
  private refreshTask: Promise<PrinterControlOperation | null> | null = null;
  private invalidated = false;
  private completedOperation: PrinterControlOperation | null = null;
  private lastReadCompletedAt = Number.NEGATIVE_INFINITY;
  private reservationGeneration = 0;
  private refreshGeneration = 0;

  constructor(
    readonly printerId: string,
    private readonly isSessionCurrent: () => boolean,
  ) {}

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
    const { current, operation, checking, submitting, admitting, uncertain } = this.snapshot;
    return !this.isSessionCurrent() || checking || submitting || admitting || uncertain || !current ||
      current.physicalControl.barrierHeld ||
      (!!operation?.barrierHeld && ['Queued', 'Running'].includes(operation.state));
  };

  private validateOperation(response: PrinterControlOperationResponse, operationId: string) {
    const operation = operationSchema.parse(response.operation);
    if (operation.printerId !== this.printerId || operation.operationId !== operationId) {
      throw new Error('The operation receipt does not match this printer.');
    }
    this.validateIntent(operation);
    return operation;
  }

  private validateIntent(operation: PrinterControlOperation): void {
    const { saved } = this.snapshot;
    if (saved?.operationId === operation.operationId && !matchesPrinterControlIntent(operation, saved.intent)) {
      throw new Error('The operation receipt does not match the requested motion intent.');
    }
  }

  private validateCurrent(value: unknown): PrinterControlCurrent {
    const current = currentSchema.parse(value);
    const { operation, physicalControl } = current;
    if (operation) this.validateIntent(operation);
    if ((!physicalControl.barrierHeld && (operation !== null || physicalControl.operationId !== null || physicalControl.state !== null)) ||
      (operation && (operation.printerId !== this.printerId || physicalControl.state !== operation.state)) ||
      physicalControl.operationId !== (operation?.operationId ?? null)) {
      throw new Error('Inconsistent current motion status. Recheck required.');
    }
    return current;
  }

  /** Coalesces overlapping reads; a hint received during a read schedules another read. */
  refresh = (): Promise<PrinterControlOperation | null> => {
    if (this.refreshTask) {
      this.invalidated = true;
      if (this.refreshGeneration !== this.reservationGeneration) {
        return this.refreshTask.then(() => this.refreshTask ?? this.refresh());
      }
      return this.refreshTask;
    }
    this.refreshGeneration = this.reservationGeneration;
    this.refreshTask = this.read().finally(() => {
      this.refreshTask = null;
      if (this.invalidated && this.isSessionCurrent()) {
        this.invalidated = false;
        void this.refresh().catch(() => undefined);
      }
    });
    return this.refreshTask;
  };

  poll = (): Promise<PrinterControlOperation | null> => {
    if (this.refreshTask) return this.refreshTask;
    if (Date.now() - this.lastReadCompletedAt < CONTROL_RECHECK_MS) return Promise.resolve(this.snapshot.operation);
    return this.refresh();
  };

  private async read(): Promise<PrinterControlOperation | null> {
    this.assertSession();
    const generation = this.reservationGeneration;
    try {
      let current = this.validateCurrent(await apiClient.getCurrentPrinterControlOperation(this.printerId));
      this.assertSession();
      if (generation !== this.reservationGeneration) {
        this.invalidated = true;
        return null;
      }
      const saved = this.snapshot.saved;
      const operationId = saved?.operationId ?? current.operation?.operationId ??
        (this.snapshot.operation?.barrierHeld ? this.snapshot.operation.operationId : null);
      let operation = current.operation ?? (this.snapshot.operation && isControlOperationResolved(this.snapshot.operation)
        ? this.snapshot.operation : null);
      let tracked: PrinterControlOperation | null = null;
      let missing = false;
      if (operationId) {
        try {
          const response = await apiClient.getPrinterControlOperation(this.printerId, operationId);
          this.assertSession();
          tracked = this.validateOperation(response, operationId);
          operation = tracked;
        } catch (error) {
          this.assertSession();
          if (![404, 410].includes(mutationErrorStatus(error) ?? 0)) throw error;
          // A missing historical receipt is not a recovery gate. Current still owns coordination.
          missing = true;
          operation = current.operation;
        }
      }
      if (saved && (missing || (tracked && isControlOperationResolved(tracked)))) {
        current = this.validateCurrent(await apiClient.getCurrentPrinterControlOperation(this.printerId));
        this.assertSession();
        if (current.operation && current.operation.operationId !== tracked?.operationId) operation = current.operation;
      }
      if (generation !== this.reservationGeneration) {
        this.invalidated = true;
        return null;
      }
      if (saved && tracked && isControlOperationResolved(tracked)) this.completedOperation = tracked;
      this.update({
        current, operation, checking: false, uncertain: false,
        saved: missing || (tracked && isControlOperationResolved(tracked)) ? null : saved,
        error: missing ? 'Motion outcome is unknown because its receipt is unavailable. Do not repeat this movement; it was not retried.'
          : this.snapshot.error && !saved && !current.operation ? this.snapshot.error : null,
      });
      return tracked;
    } catch (error) {
      if (generation !== this.reservationGeneration) {
        this.invalidated = true;
        return null;
      }
      const status = mutationErrorStatus(error);
      this.update({
        checking: false, uncertain: true,
        error: status === 404 || status === 405 || status === 410 || status === 501
          ? 'Motion status is unavailable. Check printer access and server support.'
          : 'Unable to verify current motion status. Recheck before sending another movement.',
      });
      throw error;
    } finally {
      this.lastReadCompletedAt = Date.now();
    }
  }

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

  async execute(intent: PrinterControlIntent, signal: AbortSignal): Promise<CommandResult> {
    this.assertSession();
    if (this.isBlocked()) throw new Error(this.snapshot.error ?? 'Another command is active or current motion status is unavailable.');
    if (!this.snapshot.current?.physicalControl.supportedOperations.includes(intent.kind)) {
      throw new Error('This server does not support this motion operation. Update the server.');
    }
    const saved = { operationId: generateUUID(), intent: { ...intent } };
    this.reservationGeneration++;
    this.completedOperation = null;
    this.update({ saved, operation: null, submitting: true, admitting: true, uncertain: true, error: null });
    try {
      const response = await apiClient.createPrinterControlOperation(this.printerId, saved.operationId, saved.intent);
      this.assertSession();
      this.validateOperation(response, saved.operationId);
    } catch {
      await this.refresh().catch(() => undefined);
      this.update({ admitting: false });
      throw new Error('Motion admission is uncertain. Do not repeat this movement; no command was retried.');
    } finally {
      this.update({ submitting: false });
    }
    try {
      await this.refresh();
    } finally {
      this.update({ admitting: false });
    }
    while (!signal.aborted) {
      this.assertSession();
      const completed = this.getCompletedOperation();
      const operation = completed?.operationId === saved.operationId ? completed : this.snapshot.operation;
      if (operation?.operationId === saved.operationId && isControlOperationResolved(operation)) {
        const success = operation.state === 'Succeeded' && operation.completionEvidence === 'MotionQueueDrained';
        return { success, error: success ? undefined : operation.failure?.message ??
          `Motion ended as ${operation.state}, not a confirmed success. Do not repeat this movement.` };
      }
      if (!this.snapshot.saved) {
        return { success: false, error: 'Motion outcome is unknown. Do not repeat this movement; no command was retried.' };
      }
      await this.waitForChange(signal);
      if (!signal.aborted) await this.poll();
    }
    throw new Error('Stopped waiting locally. The operation may still execute; reopen the printer to recheck.');
  }
}
