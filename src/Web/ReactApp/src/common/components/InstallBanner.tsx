import { Button } from '@/common/components/ui';
import { CloseIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { useInstallPrompt } from '@/common/hooks/useInstallPrompt';

export function InstallBanner() {
  const { canInstall, promptInstall, dismiss } = useInstallPrompt();

  if (!canInstall) return null;

  const handleInstall = async () => {
    await promptInstall();
  };

  return (
    <div className="bg-pf-accent-bg border-b border-pf-accent text-pf-text-primary p-3">
      <div className="max-w-7xl mx-auto flex items-center justify-between gap-4">
        <div className="flex items-center gap-3 flex-1">
          <span className="text-2xl">📱</span>
          <div>
            <p className="font-medium text-sm">Install PrintFarmer</p>
            <p className="text-xs text-pf-text-secondary">Get quick access to your printers from your home screen</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Button
            type="button"
            variant="primary"
            size="sm"
            onClick={handleInstall}
            iconLeft={<DownloadIcon className="h-4 w-4" />}
          >
            Install
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={dismiss}
            aria-label="Dismiss install prompt"
          >
            <CloseIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>
    </div>
  );
}
