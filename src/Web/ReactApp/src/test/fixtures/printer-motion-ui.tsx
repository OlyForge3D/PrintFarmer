import { useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { AuthContext } from '@/common/contexts/auth-context';
import { PrinterCoordinateRow } from '@/features/printers/components/PrinterCoordinateRow';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import { PrinterBackend } from '@/types/api';
import '@/index.css';

export function mountCoordinates(element: HTMLElement) {
  function CoordinateControls({ name, width }: { name: string; width: number }) {
    const [values, setValues] = useState<Record<'X' | 'Y' | 'Z', number | string>>({ X: '', Y: '', Z: '' });
    const [pending, setPending] = useState(false);
    const [submitted, setSubmitted] = useState('');
    const finish = useRef<(() => void) | null>(null);
    const submitPosition = (position: { x?: number; y?: number; z?: number }) => {
      setSubmitted(JSON.stringify(position));
      setPending(true);
      return new Promise<void>(resolve => {
        finish.current = () => { setPending(false); resolve(); };
      });
    };
    return (
      <section aria-label={name} className={width === 420 ? 'w-[420px] max-w-full' : 'w-[280px] max-w-full'}>
        <PrinterCoordinateRow
          homedAxes="xyz"
          values={values}
          positions={{ X: 80, Y: 90, Z: 10 }}
          disabled={pending}
          onChange={(axis, value) => setValues(previous => ({ ...previous, [axis]: value }))}
          onMove={(axis, amount) => { setSubmitted(`${axis}:${amount}`); }}
          onMoveTo={submitPosition}
        />
        <PrinterMotionHelp />
        <Button disabled={!pending} onClick={() => finish.current?.()}>Complete fixture motion</Button>
        <output data-testid="submitted-motion">{submitted}</output>
      </section>
    );
  }

  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } });
  queryClient.setQueryData(['settings', 'user'], {
    userId: 'fixture-user', theme: 'dark', locale: 'en', itemsPerPage: 25,
    defaultSlicerPreset: null, printablesUsername: null, printerControlMode: 'Guided', rowVersion: 'v1',
  });
  const supportState = new URLSearchParams(window.location.search).get('support');
  const motionSupportStatus = supportState === 'unknown'
    ? 'Unknown'
    : supportState === 'unsupported' ? 'Unsupported' : undefined;
  createRoot(element).render(
    <QueryClientProvider client={queryClient}>
      <AuthContext value={{ user: null, isAuthenticated: true, isLoading: false, hasPermission: () => true,
        hasRole: () => false, error: null, login: async () => false, loginWithPasskey: async () => false,
        register: async () => false, logout: async () => {} }}>
      {motionSupportStatus
        ? <section aria-label={`Motion support ${motionSupportStatus}`} className="max-w-[420px] p-3">
            <MovementControlSection
              moveX=""
              moveY=""
              moveZ=""
              step={1}
              extrudeStep={5}
              extrudeSpeed={5}
              homedAxes="xyz"
              movementActionPending={false}
              movementUnavailable={motionSupportStatus !== 'Supported'}
              movementSupportStatus={motionSupportStatus}
              backend={PrinterBackend.PrusaLink}
              canMove={motionSupportStatus === 'Supported'}
              canHomeAll={motionSupportStatus === 'Supported'}
              canHomeXY={motionSupportStatus === 'Supported'}
              canHomeZ={motionSupportStatus === 'Supported'}
              canDisableMotors={false}
              canSetStep={motionSupportStatus === 'Supported'}
              canManualMove={motionSupportStatus === 'Supported'}
              canExtrude={false}
              onMoveXChange={() => {}}
              onMoveYChange={() => {}}
              onMoveZChange={() => {}}
              onStepChange={() => {}}
              onExtrudeStepChange={() => {}}
              onExtrudeSpeedChange={() => {}}
              onMove={() => {}}
              onHome={() => {}}
              onDisableMotors={() => {}}
              onExtrude={() => {}}
            />
          </section>
        : <div className="grid gap-6 p-3">
            <CoordinateControls name="Detail coordinates" width={420} />
            <CoordinateControls name="Sidebar coordinates" width={280} />
          </div>}
      </AuthContext>
    </QueryClientProvider>,
  );
}
