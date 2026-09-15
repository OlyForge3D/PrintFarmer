import { useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { PrinterControlOperationPanel } from '@/features/printers/components/PrinterControlOperationPanel';
import { PrinterCoordinateRow } from '@/features/printers/components/PrinterCoordinateRow';
import { PrinterControlsMode, PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import { PrinterControlTracker } from '@/services/printer-control-operations';
import type { PrinterControlOperationController } from '@/features/printers/hooks/use-printer-control-operation';
import type { PrinterControlOperation } from '@/types/api';
import '@/index.css';

export function mountMotionPanel(element: HTMLElement, state: 'Queued' | 'Running' | 'Unknown') {
  const operation: PrinterControlOperation = {
    operationId: '22222222-2222-4222-8222-222222222222',
    printerId: '11111111-1111-4111-8111-111111111111',
    kind: 'Jog', x: null, y: 10, z: null, f: null, state, rowVersion: 'fixture-v1',
    requiresRecovery: false, barrierHeld: state !== 'Unknown',
    completionEvidence: 'None', senderIsolation: 'NotRequested', failure: null,
    createdAtUtc: '2026-09-13T00:00:00Z', updatedAtUtc: '2026-09-13T00:00:00Z',
    startedAtUtc: null, completedAtUtc: null,
  };
  const tracker = new PrinterControlTracker(operation.printerId, () => true);
  tracker.refresh = async () => operation;
  const control: PrinterControlOperationController = {
    usesDurableMotion: true, blocked: operation.barrierHeld, checking: false, submitting: false,
    admitting: false, uncertain: false, error: null, saved: null, operation,
    current: {
      operation: operation.barrierHeld ? operation : null,
      physicalControl: {
        barrierHeld: operation.barrierHeld, supportedOperations: ['Jog'], requiresRecovery: false,
        operationId: operation.barrierHeld ? operation.operationId : null, state: operation.barrierHeld ? operation.state : null,
      },
    },
    tracker,
    execute: async () => { throw new Error('No physical commands in browser fixtures'); },
  };
  createRoot(element).render(
    <div style={{ maxWidth: 420, padding: 12 }}><PrinterControlOperationPanel control={control} /></div>,
  );
}

export function mountCoordinates(element: HTMLElement) {
  function CoordinateControls({ name, width }: { name: string; width: number }) {
    const [values, setValues] = useState<Record<'X' | 'Y' | 'Z', number | string>>({ X: '', Y: '', Z: '' });
    const [pending, setPending] = useState(false);
    const [submitted, setSubmitted] = useState('');
    const finish = useRef<(() => void) | null>(null);
    return (
      <section aria-label={name} style={{ width, maxWidth: '100%' }}>
        <PrinterControlsMode />
        <PrinterCoordinateRow
          values={values}
          positions={{ X: 80, Y: 90, Z: 10 }}
          disabled={pending}
          onChange={(axis, value) => setValues(previous => ({ ...previous, [axis]: value }))}
          onMove={() => { throw new Error('Absolute GO must not call relative movement'); }}
          onMoveTo={position => {
            setSubmitted([position.x, position.y, position.z].join(','));
            setPending(true);
            return new Promise<void>(resolve => {
              finish.current = () => { setPending(false); resolve(); };
            });
          }}
        />
        <PrinterMotionHelp absolute />
        <Button disabled={!pending} onClick={() => finish.current?.()}>Complete fixture motion</Button>
        <output data-testid="submitted-motion">{submitted}</output>
      </section>
    );
  }

  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(['settings', 'user'], {
    userId: 'fixture-user', theme: 'dark', locale: 'en', itemsPerPage: 25,
    defaultSlicerPreset: null, printablesUsername: null, printerControlMode: 'Guided', rowVersion: 'v1',
  });
  createRoot(element).render(
    <QueryClientProvider client={queryClient}>
      <div style={{ display: 'grid', gap: 24, padding: 12 }}>
        <CoordinateControls name="Detail coordinates" width={420} />
        <CoordinateControls name="Sidebar coordinates" width={280} />
      </div>
    </QueryClientProvider>,
  );
}
