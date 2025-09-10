import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

// Service worker control: allow disabling & forced unregister via build-time flag
// Set VITE_DISABLE_SW=true to completely unregister and clear caches.
// Otherwise (in production) we register sw.js for PWA/offline support.
if ('serviceWorker' in navigator) {
  const disable = import.meta.env.VITE_DISABLE_SW === 'true';
  if (disable) {
    // Force unregister + cache purge on load
    window.addEventListener('load', () => {
      navigator.serviceWorker.getRegistrations()
        .then(regs => Promise.all(regs.map(r => r.unregister())))
        .catch(() => { /* ignore */ });
      if ('caches' in window) {
        caches.keys()
          .then(keys => Promise.all(keys.map(k => caches.delete(k))))
          .catch(() => { /* ignore */ });
      }
      // Optional: log for diagnostics (remove later if noisy)
      console.info('[SW] Unregistered all service workers and cleared caches due to VITE_DISABLE_SW=true');
    });
  } else if (import.meta.env.PROD) {
    window.addEventListener('load', () => {
      navigator.serviceWorker.register('/sw.js').catch(() => {/* ignore */})
    })
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
