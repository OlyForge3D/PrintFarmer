/**
 * Harvest Job Dialog (#722).
 *
 * Accessible multi-step flow that runs the printed-parts harvest action
 * for a completed job. States are driven by the server's canonical
 * ProblemDetails responses from #741 (wrongBin, partMappingRequired,
 * featureDisabled), plus idempotent replay semantics.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Alert } from '@/common/components/ui/Alert';
import { Badge } from '@/common/components/ui/Badge';
import { Button } from '@/common/components/ui/Button';
import { FormField } from '@/common/components/ui/FormField';
import { Input } from '@/common/components/ui/Input';
import { NumberStepper } from '@/common/components/ui/NumberStepper';
import { Textarea } from '@/common/components/ui/Textarea';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { toast } from 'sonner';
import type {
  HarvestJobResponse,
  HarvestOutputRequestItem,
  HarvestJobRequest,
  PartInventoryResponse,
  WrongBinMismatchResponse,
  PartMappingRequiredDetails,
} from '@/types/parts-inventory';
import { listParts, generateHarvestOperationKey, HarvestServiceError } from '@/services/partsHarvest';
import { useHarvestJob } from '@/features/harvest/hooks/useHarvestJob';

export interface HarvestJobDialogProps {
  /** Whether the dialog is open. */
  isOpen: boolean;
  /** Callback when the dialog should close (Cancel, ESC, or close button). */
  onClose: () => void;
  /** The completed job being harvested. */
  job: {
    id: string;
    name: string;
    /** Previous harvest timestamp; when set, dialog opens in already-harvested mode. */
    harvestedAt?: string | null;
  };
  /** Optional callback fired after a successful (or replayed) harvest. */
  onHarvested?: (response: HarvestJobResponse) => void;
}

type DialogStep =
  | { kind: 'preview' }
  | { kind: 'wrongBin'; mismatches: WrongBinMismatchResponse[]; message: string }
  | { kind: 'partMappingRequired'; details: PartMappingRequiredDetails; message: string }
  | { kind: 'featureDisabled'; message: string }
  | { kind: 'success'; response: HarvestJobResponse }
  | { kind: 'error'; message: string };

interface ManualOutputRow {
  id: string;
  sku: string;
  quantity: number;
  binCode: string;
}

function newRowId(): string {
  return `row-${Math.random().toString(36).slice(2, 10)}`;
}

export function HarvestJobDialog({
  isOpen,
  onClose,
  job,
  onHarvested,
}: HarvestJobDialogProps) {
  return (
    <HarvestJobDialogShell isOpen={isOpen} onClose={onClose} job={job} onHarvested={onHarvested} />
  );
}

/**
 * Outer shell — hosts the Modal chrome and mounts the stateful Inner
 * only while `isOpen`, so all local state resets to defaults on the next
 * open (no setState-in-effect reset, and no QueryClient dependency for
 * tests that never open the dialog).
 */
function HarvestJobDialogShell({
  isOpen,
  onClose,
  job,
  onHarvested,
}: HarvestJobDialogProps) {
  if (!isOpen) return null;
  return (
    <HarvestJobDialogInner
      isOpen={isOpen}
      onClose={onClose}
      job={job}
      onHarvested={onHarvested}
    />
  );
}

/**
 * Harvest a completed print job. Renders inline error/mapping/wrong-bin
 * recovery states inside a single accessible dialog. State is initialized
 * fresh each time this component mounts (i.e., each time the shell opens).
 */
function HarvestJobDialogInner({
  isOpen,
  onClose,
  job,
  onHarvested,
}: HarvestJobDialogProps) {
  const [step, setStep] = useState<DialogStep>({ kind: 'preview' });
  const [sharedBinCode, setSharedBinCode] = useState<string>('');
  const [uniformQuantity, setUniformQuantity] = useState<number>(1);
  const [useUniformQuantity, setUseUniformQuantity] = useState<boolean>(false);
  const [manualRows, setManualRows] = useState<ManualOutputRow[]>([]);
  const [overrideReason, setOverrideReason] = useState<string>('');
  const [parts, setParts] = useState<PartInventoryResponse[]>([]);
  const [partsLoadError, setPartsLoadError] = useState<string | null>(null);
  // One operationKey per dialog open — replay of the same key is idempotent
  // server-side so retries from wrong-bin / mapping fallback do not double-count.
  const operationKeyRef = useRef<string>(generateHarvestOperationKey());
  const lastRequestRef = useRef<HarvestJobRequest | null>(null);

  const mutation = useHarvestJob();

  const alreadyHarvested = Boolean(job.harvestedAt);

  // Fetch SKUs for the manual/fallback rows. Feature-disabled surfaces
  // through the ProblemDetails handler on submit; here we just record the
  // error so the dialog can still render.
  useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    listParts()
      .then((rows) => {
        if (!cancelled) setParts(rows);
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        const message =
          error instanceof HarvestServiceError
            ? error.info.message
            : error instanceof Error
              ? error.message
              : 'Unable to load SKUs.';
        setPartsLoadError(message);
      });
    return () => {
      cancelled = true;
    };
  }, [isOpen]);

  const dialogTitle = useMemo(() => {
    if (alreadyHarvested && step.kind === 'preview') {
      return `Already harvested — ${job.name}`;
    }
    switch (step.kind) {
      case 'success':
        return step.response.alreadyHarvested
          ? `Already harvested — ${job.name}`
          : `Harvest complete — ${job.name}`;
      case 'wrongBin':
        return `Wrong bin — ${job.name}`;
      case 'partMappingRequired':
        return `Enter outputs manually — ${job.name}`;
      case 'featureDisabled':
        return `Printed-parts inventory unavailable`;
      case 'error':
        return `Harvest failed — ${job.name}`;
      default:
        return `Harvest — ${job.name}`;
    }
  }, [alreadyHarvested, step, job.name]);

  const runHarvest = useCallback(
    (request: HarvestJobRequest) => {
      const augmented: HarvestJobRequest = {
        ...request,
        operationKey: request.operationKey ?? operationKeyRef.current,
      };
      lastRequestRef.current = augmented;
      mutation.mutate(
        { jobId: job.id, request: augmented },
        {
          onSuccess: (response) => {
            setStep({ kind: 'success', response });
            if (!response.alreadyHarvested) {
              toast.success('Harvest complete.');
            }
            onHarvested?.(response);
          },
          onError: (error: HarvestServiceError) => {
            const info = error.info;
            switch (info.kind) {
              case 'wrongBin':
                setStep({ kind: 'wrongBin', mismatches: info.mismatches, message: info.message });
                break;
              case 'partMappingRequired':
                setStep({
                  kind: 'partMappingRequired',
                  details: info.details,
                  message: info.message,
                });
                if (manualRows.length === 0) {
                  setManualRows([{ id: newRowId(), sku: '', quantity: 1, binCode: '' }]);
                }
                break;
              case 'featureDisabled':
                setStep({ kind: 'featureDisabled', message: info.message });
                break;
              default:
                setStep({ kind: 'error', message: info.message });
            }
          },
        },
      );
    },
    [job.id, manualRows.length, mutation, onHarvested],
  );

  const submitPreview = useCallback(() => {
    const request: HarvestJobRequest = {};
    const bin = sharedBinCode.trim();
    if (bin) request.binCode = bin;
    if (useUniformQuantity) request.quantityOverride = uniformQuantity;
    runHarvest(request);
  }, [runHarvest, sharedBinCode, uniformQuantity, useUniformQuantity]);

  const submitWrongBinOverride = useCallback(() => {
    const previous = lastRequestRef.current ?? {};
    runHarvest({
      ...previous,
      allowWrongBin: true,
      overrideReason: overrideReason.trim(),
    });
  }, [overrideReason, runHarvest]);

  const submitManualOutputs = useCallback(() => {
    const outputs: HarvestOutputRequestItem[] = manualRows
      .filter((r) => r.sku.trim() !== '' && r.quantity > 0)
      .map((r) => ({ sku: r.sku.trim(), quantity: r.quantity }));
    const outputBins = manualRows
      .filter((r) => r.sku.trim() !== '' && r.binCode.trim() !== '')
      .map((r) => ({ partSku: r.sku.trim(), binCode: r.binCode.trim() }));
    const bin = sharedBinCode.trim();
    const request: HarvestJobRequest = { outputs };
    if (outputBins.length > 0) request.outputBins = outputBins;
    if (bin) request.binCode = bin;
    runHarvest(request);
  }, [manualRows, runHarvest, sharedBinCode]);

  const closeAndReset = useCallback(() => {
    onClose();
  }, [onClose]);

  const isBusy = mutation.isPending;

  // ----- Render helpers --------------------------------------------------

  const renderAlreadyHarvestedPreview = () => (
    <div className="space-y-3" data-testid="harvest-already-harvested">
      <Alert type="info">
        <span>
          This job was already harvested{' '}
          {job.harvestedAt ? (
            <>on <time dateTime={job.harvestedAt}>{new Date(job.harvestedAt).toLocaleString()}</time></>
          ) : (
            'previously'
          )}
          . Printed-part stock has already been credited.
        </span>
      </Alert>
      <p className="text-sm text-pf-text-secondary">
        You can safely close this dialog. Harvesting again with the same operation key
        will not double-count stock.
      </p>
    </div>
  );

  const renderPreview = () => (
    <div className="space-y-4" data-testid="harvest-preview">
      <p className="text-sm text-pf-text-secondary">
        Confirm printed-part outputs for <strong>{job.name}</strong>. Mapped SKUs
        and quantities come from the job's output mapping; corrections below are
        optional.
      </p>

      {partsLoadError && (
        <Alert type="warning">
          Could not preload SKU list ({partsLoadError}). You can still harvest
          using the job's mapped values.
        </Alert>
      )}

      <FormField label="Destination bin (optional)" htmlFor="harvest-shared-bin">
        <Input
          id="harvest-shared-bin"
          value={sharedBinCode}
          onChange={(e) => setSharedBinCode(e.target.value)}
          placeholder="Leave blank to use each SKU's default bin"
          aria-describedby="harvest-shared-bin-help"
        />
        <p id="harvest-shared-bin-help" className="mt-1 text-xs text-pf-text-secondary">
          Applied to every output. Mismatches against the SKU's default bin will
          be flagged before stock is credited.
        </p>
      </FormField>

      <div className="space-y-2">
        <Checkbox
          checked={useUniformQuantity}
          onChange={(e) => setUseUniformQuantity(e.target.checked)}
          label="Override quantity for every mapped SKU"
          id="harvest-uniform-qty-toggle"
        />
        {useUniformQuantity && (
          <FormField label="Quantity per SKU" htmlFor="harvest-uniform-qty">
            <NumberStepper
              id="harvest-uniform-qty"
              value={uniformQuantity}
              onChange={setUniformQuantity}
              min={1}
              step={1}
            />
          </FormField>
        )}
      </div>
    </div>
  );

  const renderWrongBin = () => {
    if (step.kind !== 'wrongBin') return null;
    return (
      <div className="space-y-4" data-testid="harvest-wrong-bin">
        <Alert type="warning">
          <div className="space-y-1">
            <p className="font-medium">Bin mismatch detected.</p>
            <p className="text-sm">{step.message}</p>
          </div>
        </Alert>
        <ul className="space-y-1 text-sm">
          {step.mismatches.map((m) => (
            <li key={m.partSku} className="flex items-center gap-2">
              <span aria-hidden className="text-pf-warning">⚠</span>
              <span>
                SKU <strong>{m.partSku}</strong>: expected bin{' '}
                <code>{m.expectedBinCode ?? '—'}</code>, scanned{' '}
                <code>{m.scannedBinCode}</code>.
              </span>
            </li>
          ))}
        </ul>
        <FormField
          label="Override reason (required)"
          htmlFor="harvest-override-reason"
          error={
            overrideReason.trim().length === 0
              ? 'A reason is required to override the wrong-bin check.'
              : undefined
          }
        >
          <Textarea
            id="harvest-override-reason"
            value={overrideReason}
            onChange={(e) => setOverrideReason(e.target.value)}
            rows={3}
            placeholder="Explain why the scanned bin is acceptable (audited)."
          />
        </FormField>
      </div>
    );
  };

  const renderMappingRequired = () => {
    if (step.kind !== 'partMappingRequired') return null;
    const addRow = () =>
      setManualRows((rows) => [...rows, { id: newRowId(), sku: '', quantity: 1, binCode: '' }]);
    const removeRow = (id: string) =>
      setManualRows((rows) => rows.filter((r) => r.id !== id));
    const updateRow = (id: string, patch: Partial<ManualOutputRow>) =>
      setManualRows((rows) => rows.map((r) => (r.id === id ? { ...r, ...patch } : r)));

    return (
      <div className="space-y-4" data-testid="harvest-mapping-required">
        <Alert type="info">
          <div className="space-y-1">
            <p className="font-medium">Printed-part mapping required.</p>
            <p className="text-sm">{step.details.guidance || step.message}</p>
          </div>
        </Alert>

        <div role="group" aria-label="Manual outputs" className="space-y-3">
          {manualRows.map((row, index) => (
            <div
              key={row.id}
              className="grid grid-cols-[1fr_auto_auto_auto] gap-2 items-end"
              data-testid="harvest-manual-row"
            >
              <FormField label={`SKU #${index + 1}`} htmlFor={`harvest-sku-${row.id}`}>
                <Input
                  id={`harvest-sku-${row.id}`}
                  list={parts.length > 0 ? 'harvest-parts-datalist' : undefined}
                  value={row.sku}
                  onChange={(e) => updateRow(row.id, { sku: e.target.value })}
                  placeholder="SKU code"
                />
              </FormField>
              <FormField label="Quantity" htmlFor={`harvest-qty-${row.id}`}>
                <NumberStepper
                  id={`harvest-qty-${row.id}`}
                  value={row.quantity}
                  onChange={(v) => updateRow(row.id, { quantity: v })}
                  min={1}
                  step={1}
                />
              </FormField>
              <FormField label="Bin (optional)" htmlFor={`harvest-bin-${row.id}`}>
                <Input
                  id={`harvest-bin-${row.id}`}
                  value={row.binCode}
                  onChange={(e) => updateRow(row.id, { binCode: e.target.value })}
                  placeholder="Bin code"
                />
              </FormField>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => removeRow(row.id)}
                aria-label={`Remove row ${index + 1}`}
                disabled={manualRows.length === 1}
              >
                ✕
              </Button>
            </div>
          ))}
          {parts.length > 0 && (
            <datalist id="harvest-parts-datalist">
              {parts.map((p) => (
                <option key={p.id} value={p.sku}>
                  {p.name}
                </option>
              ))}
            </datalist>
          )}
          <Button variant="secondary" onClick={addRow}>
            + Add another SKU
          </Button>
        </div>

        <FormField label="Shared destination bin (optional)" htmlFor="harvest-manual-shared-bin">
          <Input
            id="harvest-manual-shared-bin"
            value={sharedBinCode}
            onChange={(e) => setSharedBinCode(e.target.value)}
            placeholder="Applied when a row has no bin"
          />
        </FormField>
      </div>
    );
  };

  const renderFeatureDisabled = () => {
    if (step.kind !== 'featureDisabled') return null;
    return (
      <div className="space-y-3" data-testid="harvest-feature-disabled">
        <Alert type="info">
          <div className="space-y-1">
            <p className="font-medium">Printed-parts inventory is not enabled.</p>
            <p className="text-sm">{step.message}</p>
          </div>
        </Alert>
        <p className="text-sm text-pf-text-secondary">
          Ask an administrator to enable the printed-parts inventory feature to
          start recording harvest outputs.
        </p>
      </div>
    );
  };

  const renderSuccess = () => {
    if (step.kind !== 'success') return null;
    const { response } = step;
    return (
      <div className="space-y-4" data-testid="harvest-success">
        <Alert
          type={response.alreadyHarvested ? 'info' : 'success'}
         
        >
          <div className="space-y-1">
            <p className="font-medium">
              {response.alreadyHarvested
                ? 'This job was already harvested. Stock was not changed.'
                : 'Harvest complete. Printed-part stock updated.'}
            </p>
            <p className="text-sm">
              Harvested at{' '}
              <time dateTime={response.harvestedAt}>
                {new Date(response.harvestedAt).toLocaleString()}
              </time>
              {response.binCode ? <> · destination bin <code>{response.binCode}</code></> : null}.
            </p>
          </div>
        </Alert>
        {response.outputs.length > 0 && (
          <div>
            <p className="text-sm font-medium mb-2">Outputs</p>
            <ul className="space-y-1 text-sm">
              {response.outputs.map((o) => (
                <li key={`${o.sequence}-${o.partSku}`} className="flex items-center gap-2">
                  <Badge variant="default">×{o.quantity}</Badge>
                  <span>
                    <strong>{o.partSku}</strong> → bin <code>{o.actualBinCode}</code>
                    {o.overrideApplied ? ' (override)' : ''}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {response.adjustments.length > 0 && (
          <div>
            <p className="text-sm font-medium mb-2">Ledger adjustments</p>
            <ul className="space-y-1 text-sm">
              {response.adjustments.map((a) => (
                <li key={a.id}>
                  <strong>{a.sku}</strong>: {a.delta >= 0 ? '+' : ''}
                  {a.delta} (new balance {a.resultingBalance})
                  {a.binCode ? <> · bin <code>{a.binCode}</code></> : null}
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    );
  };

  const renderError = () => {
    if (step.kind !== 'error') return null;
    return (
      <div className="space-y-3" data-testid="harvest-error">
        <Alert type="error">
          <div className="space-y-1">
            <p className="font-medium">Harvest failed.</p>
            <p className="text-sm">{step.message}</p>
          </div>
        </Alert>
      </div>
    );
  };

  // ----- Footer buttons per step ---------------------------------------

  const renderFooter = () => {
    if (alreadyHarvested && step.kind === 'preview') {
      return (
        <div className="flex justify-end gap-2">
          <Button variant="primary" onClick={closeAndReset}>Close</Button>
        </div>
      );
    }
    switch (step.kind) {
      case 'preview':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset} disabled={isBusy}>
              Cancel
            </Button>
            <Button variant="primary" onClick={submitPreview} disabled={isBusy}>
              {isBusy ? 'Harvesting…' : 'Confirm harvest'}
            </Button>
          </div>
        );
      case 'wrongBin':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset} disabled={isBusy}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={submitWrongBinOverride}
              disabled={isBusy || overrideReason.trim().length === 0}
            >
              {isBusy ? 'Retrying…' : 'Override & harvest'}
            </Button>
          </div>
        );
      case 'partMappingRequired':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset} disabled={isBusy}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={submitManualOutputs}
              disabled={
                isBusy ||
                manualRows.filter((r) => r.sku.trim() !== '' && r.quantity > 0).length === 0
              }
            >
              {isBusy ? 'Harvesting…' : 'Confirm manual harvest'}
            </Button>
          </div>
        );
      case 'featureDisabled':
        return (
          <div className="flex justify-end">
            <Button variant="primary" onClick={closeAndReset}>Close</Button>
          </div>
        );
      case 'success':
        return (
          <div className="flex justify-end">
            <Button variant="primary" onClick={closeAndReset}>Done</Button>
          </div>
        );
      case 'error':
        return (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={closeAndReset}>Close</Button>
            <Button variant="primary" onClick={submitPreview} disabled={isBusy}>
              {isBusy ? 'Retrying…' : 'Retry'}
            </Button>
          </div>
        );
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={closeAndReset}
      title={dialogTitle}
      size="xl"
      isDisabled={isBusy}
      closeOnBackdrop={false}
      footer={renderFooter()}
    >
      {alreadyHarvested && step.kind === 'preview' ? renderAlreadyHarvestedPreview() : null}
      {(!alreadyHarvested || step.kind !== 'preview') && step.kind === 'preview' ? renderPreview() : null}
      {step.kind === 'wrongBin' ? renderWrongBin() : null}
      {step.kind === 'partMappingRequired' ? renderMappingRequired() : null}
      {step.kind === 'featureDisabled' ? renderFeatureDisabled() : null}
      {step.kind === 'success' ? renderSuccess() : null}
      {step.kind === 'error' ? renderError() : null}
    </Modal>
  );
}
