import { useState } from 'react';
import clsx from 'clsx';
import { AlertIcon, ExternalLinkIcon, HelpCircleIcon, ChevronDownIcon, ChevronUpIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import type { PrinterBackend } from '@/types/api';

interface OfflineTroubleshootingGuideProps {
  printerBackend: PrinterBackend | string;
  printerIp?: string;
  serverUrl?: string;
  frontendUrl?: string;
  onDismiss?: () => void;
  variant?: 'full' | 'compact' | 'popover';
}

interface TroubleshootingStep {
  text: string;
  command?: string;
  link?: { url: string; label: string };
}

function getBackendName(backend: PrinterBackend | string): string {
  const name = typeof backend === 'string' ? backend : String(backend);
  switch (name) {
    case '1':
    case 'Moonraker':
      return 'Moonraker';
    case '2':
    case 'PrusaLink':
      return 'PrusaLink';
    case '4':
    case 'OctoPrint':
      return 'OctoPrint';
    case '3':
    case 'SDCP':
      return 'SDCP';
    case '5':
    case 'FlashForge':
      return 'FlashForge';
    default:
      return 'Unknown';
  }
}

function getCommonSteps(printerIp?: string, serverUrl?: string, frontendUrl?: string): TroubleshootingStep[] {
  const webUrl = frontendUrl ?? serverUrl;
  return [
    { text: 'Check that the printer is powered on and the display is active' },
    { text: 'Verify the network cable is connected or WiFi is associated' },
    ...(printerIp ? [{ text: 'Ping the printer to check network connectivity', command: `ping ${printerIp}` }] : []),
    ...(webUrl ? [{ text: "Check if the printer's web interface is accessible", link: { url: webUrl, label: webUrl } }] : []),
  ];
}

function getBackendSteps(backend: string): TroubleshootingStep[] {
  switch (backend) {
    case 'Moonraker':
      return [
        { text: 'Check if the Moonraker service is running', command: 'systemctl status moonraker' },
        { text: 'Check if the Klipper firmware service is running', command: 'systemctl status klipper' },
        { text: 'Verify Moonraker is listening on the default port (7125)' },
        { text: 'Review Moonraker documentation for additional troubleshooting', link: { url: 'https://moonraker.readthedocs.io/', label: 'Moonraker Docs' } },
      ];
    case 'PrusaLink':
      return [
        { text: 'Verify PrusaLink is enabled in the printer Settings menu' },
        { text: 'Check that the firmware version supports PrusaLink' },
        { text: 'Verify the HTTP API is accessible on the default port (80)' },
        { text: 'Review PrusaLink documentation for additional troubleshooting', link: { url: 'https://help.prusa3d.com/tag/prusalink', label: 'PrusaLink Docs' } },
      ];
    case 'OctoPrint':
      return [
        { text: 'Check if the OctoPrint service is running', command: 'systemctl status octoprint' },
        { text: 'Verify the API key configured in PrintFarmer is still valid' },
        { text: 'Check OctoPrint is listening on the default port (5000)' },
        { text: 'Review OctoPrint documentation for additional troubleshooting', link: { url: 'https://docs.octoprint.org/', label: 'OctoPrint Docs' } },
      ];
    case 'FlashForge':
    case 'SDCP':
      return [
        { text: 'Power-cycle the printer and wait 30 seconds before reconnecting' },
        { text: 'Verify the printer is on the same network as the PrintFarmer server' },
      ];
    default:
      return [
        { text: 'Verify the printer firmware supports the configured backend protocol' },
        { text: 'Power-cycle the printer and wait 30 seconds before reconnecting' },
      ];
  }
}

function StepList({ steps, label }: { steps: TroubleshootingStep[]; label: string }) {
  return (
    <ol className="list-decimal list-inside space-y-2" aria-label={label}>
      {steps.map((step, i) => (
        <li key={i} className="text-sm text-pf-text-secondary">
          <span>{step.text}</span>
          {step.command && (
            <code className="ml-2 px-1.5 py-0.5 rounded bg-black/30 text-xs font-mono text-pf-text-primary">
              {step.command}
            </code>
          )}
          {step.link && (
            <a
              href={step.link.url}
              target="_blank"
              rel="noopener noreferrer"
              className="ml-2 inline-flex items-center gap-1 text-xs text-pf-accent hover:underline"
            >
              {step.link.label}
              <ExternalLinkIcon className="h-3 w-3" />
            </a>
          )}
        </li>
      ))}
    </ol>
  );
}

export function OfflineTroubleshootingGuide({
  printerBackend,
  printerIp,
  serverUrl,
  frontendUrl,
  onDismiss,
  variant = 'full',
}: OfflineTroubleshootingGuideProps) {
  const [isExpanded, setIsExpanded] = useState(variant === 'full');
  const backendName = getBackendName(printerBackend);
  const commonSteps = getCommonSteps(printerIp, serverUrl, frontendUrl);
  const backendSteps = getBackendSteps(backendName);

  if (variant === 'compact') {
    return (
      <div className="mt-2">
        <Button
          type="button"
          variant="unstyled"
          onClick={() => setIsExpanded(!isExpanded)}
          className="inline-flex flex-row flex-nowrap items-center gap-1 text-xs leading-none text-pf-warning hover:text-pf-text-primary transition-colors whitespace-nowrap"
          aria-expanded={isExpanded}
          aria-label="Toggle offline troubleshooting guide"
        >
          <HelpCircleIcon className="h-3.5 w-3.5 shrink-0" />
          <span className="inline-block shrink-0 whitespace-nowrap">Troubleshoot</span>
          {isExpanded ? <ChevronUpIcon className="h-3 w-3 shrink-0" /> : <ChevronDownIcon className="h-3 w-3 shrink-0" />}
        </Button>
        {isExpanded && (
          <div className="mt-2 p-3 rounded-lg bg-pf-warning/10 border border-pf-warning/30">
            <div className="space-y-3">
              <div>
                <h4 className="text-xs font-semibold text-pf-text-primary mb-1.5">Common Checks</h4>
                <StepList steps={commonSteps} label="Common troubleshooting steps" />
              </div>
              {backendSteps.length > 0 && (
                <div>
                  <h4 className="text-xs font-semibold text-pf-text-primary mb-1.5">{backendName}-Specific</h4>
                  <StepList steps={backendSteps} label={`${backendName}-specific troubleshooting steps`} />
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    );
  }

  if (variant === 'popover') {
    return (
      <div className="p-3 rounded-lg bg-pf-bg-1 border border-pf-warning/30 shadow-lg max-w-sm" role="dialog" aria-label="Offline troubleshooting guide">
        <div className="flex items-center justify-between mb-2">
          <div className="flex items-center gap-1.5">
            <AlertIcon className="h-4 w-4 text-pf-warning" />
            <span className="text-sm font-semibold text-pf-text-primary">Offline Troubleshooting</span>
          </div>
          {onDismiss && (
            <Button variant="ghost" size="sm" onClick={onDismiss} className="h-6 w-6 p-0" aria-label="Dismiss troubleshooting guide">
              <CloseIcon className="h-3.5 w-3.5" />
            </Button>
          )}
        </div>
        <div className="space-y-2.5">
          <div>
            <h4 className="text-xs font-semibold text-pf-text-secondary mb-1">Common Checks</h4>
            <StepList steps={commonSteps} label="Common troubleshooting steps" />
          </div>
          {backendSteps.length > 0 && (
            <div>
              <h4 className="text-xs font-semibold text-pf-text-secondary mb-1">{backendName}-Specific</h4>
              <StepList steps={backendSteps} label={`${backendName}-specific troubleshooting steps`} />
            </div>
          )}
        </div>
      </div>
    );
  }

  // Full variant (for DetailedPrinterCard)
  return (
    <div className={clsx('rounded-lg border', 'bg-pf-warning/10 border-pf-warning/30')}>
      <Button
        type="button"
        variant="unstyled"
        onClick={() => setIsExpanded(!isExpanded)}
        className="flex items-center justify-between w-full px-3 py-2 text-left"
        aria-expanded={isExpanded}
        aria-label="Toggle offline troubleshooting guide"
      >
        <div className="flex items-center gap-2">
          <AlertIcon className="h-4 w-4 text-pf-warning" />
          <span className="text-sm font-semibold text-pf-text-primary">Printer Offline — Troubleshooting Steps</span>
        </div>
        <div className="flex items-center gap-1">
          {onDismiss && (
            <span
              role="button"
              tabIndex={0}
              onClick={(e) => { e.stopPropagation(); onDismiss(); }}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.stopPropagation(); onDismiss(); } }}
              className="h-6 w-6 flex items-center justify-center rounded text-pf-text-tertiary hover:text-pf-text-primary"
              aria-label="Dismiss troubleshooting guide"
            >
              <CloseIcon className="h-3.5 w-3.5" />
            </span>
          )}
          {isExpanded ? <ChevronUpIcon className="h-4 w-4 text-pf-text-secondary" /> : <ChevronDownIcon className="h-4 w-4 text-pf-text-secondary" />}
        </div>
      </Button>
      {isExpanded && (
        <div className="px-3 pb-3 space-y-3">
          <div>
            <h4 className="text-xs font-semibold text-pf-text-secondary mb-1.5 uppercase tracking-wide">Common Checks</h4>
            <StepList steps={commonSteps} label="Common troubleshooting steps" />
          </div>
          {backendSteps.length > 0 && (
            <div>
              <h4 className="text-xs font-semibold text-pf-text-secondary mb-1.5 uppercase tracking-wide">{backendName}-Specific Steps</h4>
              <StepList steps={backendSteps} label={`${backendName}-specific troubleshooting steps`} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}
