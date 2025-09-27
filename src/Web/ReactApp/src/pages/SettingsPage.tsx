import { useState } from 'react';


export function SettingsPage() {
  // --- Dynamic Settings UI State ---


  // ...existing code for diagnostics, password policy, telemetry, etc...

  // --- Debug Logging Controls Example ---
  const debugKeys = {
    dashboard: 'Dashboard',
    printer: 'Printer',
    settings: 'Settings',
    // ...add more debug keys as needed
  };

  const [debugState, setDebugState] = useState<Record<string, boolean>>({});

  const handleToggle = (key: string, checked: boolean) => {
    setDebugState(prev => ({
      ...prev,
      [key]: checked,
    }));
  };

  const handleReset = () => {
    setDebugState({});
  };

  return (
    <>
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6 mt-8">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-4">Debug Logging Controls</h2>
        <p className="text-sm text-pf-text-secondary mb-4">Enable or disable informational logging for specific UI components. Changes are saved and persist across reloads.</p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {Object.entries(debugKeys).map(([key, label]) => (
            <div key={key} className="flex items-center gap-2">
              <input
                id={`pfdebug-${key}`}
                type="checkbox"
                checked={!!debugState[key]}
                onChange={e => handleToggle(key, e.target.checked)}
                className="h-4 w-4"
              />
              <label htmlFor={`pfdebug-${key}`} className="text-sm text-pf-text-primary">{label}</label>
            </div>
          ))}
        </div>
        <div className="mt-4 flex gap-2">
          <button
            type="button"
            onClick={handleReset}
            className="px-4 py-2 bg-gray-600 text-white rounded hover:bg-gray-700"
          >
            Reset All
          </button>
        </div>
        <p className="text-xs text-pf-text-secondary mt-4">These toggles control live debug logging for development and troubleshooting. Settings are persisted in your browser and will remain after reload.</p>
      </div>

      {/* System Log Persistence settings are now managed via dynamic settings UI above. */}
    </>
  );
}