import { useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { PrinterCoordinateRow } from '@/features/printers/components/PrinterCoordinateRow';
import { PrinterControlsMode, PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import '@/index.css';

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
