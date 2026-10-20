import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { Toaster } from 'sonner';
import { AuthContext } from '@/common/contexts/auth-context';
import { DetailedPrinterCard } from '@/features/printers/components/DetailedPrinterCard';
import { PrinterDetailsSidebar } from '@/features/printers/components/PrinterDetailsSidebar';
import { client } from '@/services/api/httpClient';
import { apiClient } from '@/services/api';
import { PrinterBackend, type Printer, type PrinterBackendCapabilitiesDto, type PrinterSensor } from '@/types/api';
import '@/index.css';

const sensors: PrinterSensor[] = [
  { id: 'extruder', kind: 'Nozzle', displayName: 'Primary nozzle', available: true,
    readings: [{ quantity: 'Temperature', value: 205, unit: 'Celsius' }, { quantity: 'TargetTemperature', value: 210, unit: 'Celsius' },
      { quantity: 'Power', value: 32, unit: 'Percent' }] },
  { id: 'extruder1', kind: 'Nozzle', displayName: 'Second nozzle', available: true,
    readings: [{ quantity: 'Temperature', value: 32.4, unit: 'Celsius' }] },
  { id: 'chamber', kind: 'Chamber', displayName: 'Chamber', available: true,
    readings: [{ quantity: 'Temperature', value: 36.2, unit: 'Celsius' }] },
  { id: 'heater_generic box1', kind: 'Chamber', displayName: 'Heater Box1', available: true,
    readings: [{ quantity: 'Temperature', value: 35.2, unit: 'Celsius' }, { quantity: 'TargetTemperature', value: 40, unit: 'Celsius' },
      { quantity: 'Power', value: 22, unit: 'Percent' }],
    writable: [{ quantity: 'TargetTemperature', min: 0, max: 80, step: 1, unit: 'Celsius' }] },
  { id: 'ambient', kind: 'Ambient', displayName: 'Enclosure environmental sensor with a long operator-defined label', available: true,
    readings: [{ quantity: 'Humidity', value: 42, unit: 'Percent' }, { quantity: 'Pressure', value: 1013, unit: 'HectoPascal' }] },
  { id: 'fan', kind: 'Fan', displayName: 'Part cooling', available: true,
    readings: [{ quantity: 'FanSpeed', value: 40, unit: 'Percent' }, { quantity: 'FanRpm', value: 3200, unit: 'Rpm' }],
    writable: [{ quantity: 'FanSpeed', min: 0, max: 100, step: 1, unit: 'Percent' }] },
  { id: 'controller', kind: 'Fan', displayName: 'Controller fan', available: false,
    readings: [{ quantity: 'FanSpeed', value: 60, unit: 'Percent' }],
    writable: [{ quantity: 'FanSpeed', min: 0, max: 100, step: 1, unit: 'Percent' }] },
  { id: 'output', kind: 'Other', displayName: 'Enclosure output', available: true,
    readings: [{ quantity: 'Power', value: 8, unit: 'Watt' }] },
  { id: 'runout', kind: 'FilamentRunout', displayName: 'Filament runout', available: true,
    readings: [{ quantity: 'FilamentDetected', value: true, unit: 'None' }] },
];

/** Isolated real controls, no app bootstrap and no hardware requests. */
export function mountSensors(element: HTMLElement) {
  // Fail closed even when opened manually, outside Playwright's network interception.
  client.defaults.adapter = async () => { throw new Error('Isolated POC: API access disabled'); };
  apiClient.setPrinterSensor = async () => ({ success: false, error: 'POC only — no command sent to hardware.' });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } });
  const printer: Printer = {
    id: 'sensor-poc-card', name: 'Workshop printer · sensor POC', backend: PrinterBackend.Moonraker,
    backendUrl: 'http://invalid.local', isOnline: true, isEnabled: true, rowVersion: 'poc',
    state: 'Idle', homedAxes: 'xyz', x: 100, y: 110, z: 5,
    hotendTemp: 205, hotendTarget: 210, bedTemp: 59.8, bedTarget: 60, sensors,
  };
  const capabilities: PrinterBackendCapabilitiesDto = {
    printerId: printer.id, printerName: printer.name, backend: printer.backend,
    supportsCamera: false, supportsFileDownload: false, supportsFileList: false, supportsFileUpload: false,
    supportsStartPrint: false, supportsControlOperations: false, supportsFileMetadata: false,
    supportsMovement: true, supportsTemperatureControl: true, supportsPrinterInformation: false,
    supportsHistory: false, supportsFilamentControl: false, supportsObjectExclusion: false,
    supportsSensorControl: true, supportsSensorTelemetry: true,
  };
  createRoot(element).render(
    <MemoryRouter>
    <QueryClientProvider client={queryClient}>
    <AuthContext value={{ user: null, isAuthenticated: true, isLoading: false, hasPermission: () => true,
      hasRole: () => false, error: null, login: async () => false, loginWithPasskey: async () => false,
      register: async () => false, logout: async () => {} }}>
      <main className="flex flex-wrap items-start gap-6 p-4 bg-pf-bg-0 text-pf-text-primary">
        <p className="basis-full text-sm text-pf-text-secondary">Actual printer components · hardware access disabled. Card online, XYZ homed; sidebar offline with only XY homed. All coordinate entry requires XYZ homing. Tab or Enter sends only the edited axis.</p>
        {(['card', 'sidebar'] as const).map(surface => (
          <section key={surface} aria-label={surface} className={`min-w-0 max-w-full ${surface === 'card' ? 'w-96' : 'w-80'}`}>
            <h2 className="text-sm mb-3">{surface === 'card' ? 'Detailed printer card' : 'Printer sidebar'}</h2>
            {surface === 'card' ? <DetailedPrinterCard printer={printer} backendCapabilities={capabilities} />
              : <PrinterDetailsSidebar printerId="sensor-poc-sidebar"
                printer={{ ...printer, id: 'sensor-poc-sidebar', isOnline: false, homedAxes: 'xy' }}
                backendCapabilities={capabilities} layout="content" onClose={() => {}} />}
          </section>
        ))}
      </main>
      <Toaster theme="dark" />
    </AuthContext>
    </QueryClientProvider>
    </MemoryRouter>,
  );
}
