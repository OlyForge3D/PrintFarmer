import { useTheme } from '@/contexts/ThemeContext';

/**
 * Visual theme comparison component to show active theme colors
 * Add this temporarily to Settings page to verify theme switching
 */
export function ThemeDebugPanel() {
  const { theme } = useTheme();
  
  return (
    <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border mb-6">
      <h3 className="text-lg font-semibold text-pf-text-primary mb-4">
        Active Theme Debug: {theme}
      </h3>
      
      <div className="grid grid-cols-3 gap-4">
        {/* Background Colors */}
        <div>
          <p className="text-sm text-pf-text-secondary mb-2">Backgrounds:</p>
          <div className="space-y-2">
            <div className="bg-pf-bg-0 border border-pf-border p-2 rounded">
              <code className="text-xs text-pf-text-primary">bg-0</code>
            </div>
            <div className="bg-pf-bg-1 border border-pf-border p-2 rounded">
              <code className="text-xs text-pf-text-primary">bg-1</code>
            </div>
            <div className="bg-pf-bg-2 border border-pf-border p-2 rounded">
              <code className="text-xs text-pf-text-primary">bg-2</code>
            </div>
          </div>
        </div>
        
        {/* Accent Colors */}
        <div>
          <p className="text-sm text-pf-text-secondary mb-2">Accent:</p>
          <div className="space-y-2">
            <div 
              className="border border-pf-border p-2 rounded"
              style={{ backgroundColor: 'var(--pf-accent)' }}
            >
              <code className="text-xs text-white">accent</code>
            </div>
            <div 
              className="border border-pf-border p-2 rounded"
              style={{ backgroundColor: 'var(--pf-success)' }}
            >
              <code className="text-xs text-white">success</code>
            </div>
            <div 
              className="border border-pf-border p-2 rounded"
              style={{ backgroundColor: 'var(--pf-error)' }}
            >
              <code className="text-xs text-white">error</code>
            </div>
          </div>
        </div>
        
        {/* Text Colors */}
        <div>
          <p className="text-sm text-pf-text-secondary mb-2">Text:</p>
          <div className="bg-pf-bg-2 p-3 rounded border border-pf-border">
            <p className="text-pf-text-primary text-sm mb-1">Primary</p>
            <p className="text-pf-text-secondary text-sm mb-1">Secondary</p>
            <p className="text-pf-text-tertiary text-sm">Tertiary</p>
          </div>
        </div>
      </div>
      
      {/* CSS Variable Values */}
      <div className="mt-4 p-3 bg-pf-bg-2 rounded border border-pf-border">
        <p className="text-xs font-mono text-pf-text-secondary">
          Computed CSS values:
        </p>
        <div className="grid grid-cols-2 gap-2 mt-2 text-xs font-mono">
          <div className="text-pf-text-primary">
            bg-0: <span className="text-pf-accent" id="debug-bg-0"></span>
          </div>
          <div className="text-pf-text-primary">
            accent: <span className="text-pf-accent" id="debug-accent"></span>
          </div>
        </div>
      </div>
      
      <script dangerouslySetInnerHTML={{
        __html: `
          setTimeout(() => {
            const root = document.documentElement;
            const bg0 = getComputedStyle(root).getPropertyValue('--pf-bg-0').trim();
            const accent = getComputedStyle(root).getPropertyValue('--pf-accent').trim();
            const bg0El = document.getElementById('debug-bg-0');
            const accentEl = document.getElementById('debug-accent');
            if (bg0El) bg0El.textContent = bg0;
            if (accentEl) accentEl.textContent = accent;
          }, 100);
        `
      }} />
    </div>
  );
}
