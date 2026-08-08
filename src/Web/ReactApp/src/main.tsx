import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import './styles/icons.css'
import './styles/tour-theme.css'
import 'driver.js/dist/driver.css'
import App from './App.tsx'
import { SpoolmanProvider } from './contexts/SpoolmanContext'

// Service worker control: allow disabling & forced unregister via build-time flag
// Set VITE_DISABLE_SW=true to completely unregister and clear caches.
// Otherwise (in production) we register sw.js for PWA/offline support.
// Dev-local safety: always unregister service workers and clear caches when running on localhost or in dev mode.
// This prevents stale cached assets from being served during active development.
if ('serviceWorker' in navigator) {
  const isLocalDev = location.hostname === 'localhost' || location.hostname === '127.0.0.1' || import.meta.env.DEV;
  if (isLocalDev) {
    window.addEventListener('load', () => {
      navigator.serviceWorker.getRegistrations()
        .then(regs => Promise.all(regs.map(r => r.unregister())))
        .catch(() => { /* ignore */ });
      if ('caches' in window) {
        caches.keys()
          .then(keys => Promise.all(keys.map(k => caches.delete(k))))
          .catch(() => { /* ignore */ });
      }
      if (window.PrintFarmerDebug?.main) {
        try { console.info('[SW] Unregistered service workers and cleared caches (dev/localhost)'); } catch { /* ignore debug stringify errors */ }
      }
    });
  }
}
// Production: register PWA service worker
if ('serviceWorker' in navigator && import.meta.env.PROD) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {/* ignore */})
  })
}

function mount() {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <SpoolmanProvider>
        <App />
      </SpoolmanProvider>
    </StrictMode>,
  )
}

// OpenTelemetry is only loaded when it will actually do something: either an
// OTLP endpoint is configured, or we're in dev (where initializeTelemetry
// also wires up the ConsoleSpanExporter — see telemetry/config.ts). In a
// default production build with no endpoint configured, this branch is a
// statically-false constant and Rollup drops the whole SDK graph from the
// bundle, so first paint is never blocked waiting on telemetry setup. When
// telemetry IS enabled we await initialization before mounting so
// document-load and early request spans aren't missed.
if (import.meta.env.DEV || import.meta.env.VITE_OTEL_EXPORTER_OTLP_ENDPOINT) {
  import('./telemetry/config')
    .then(({ initializeTelemetry }) => initializeTelemetry())
    .catch((error: unknown) => {
      console.error('[Telemetry] Failed to load OpenTelemetry SDK:', error);
    })
    .finally(mount);
} else {
  mount();
}
